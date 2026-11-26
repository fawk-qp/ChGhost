using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ChGhost.Core.Contracts;
using ChGhost.Core.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ChGhost.Core.Vision;

public sealed class OnnxBoardRecognizer : IBoardRecognizer, IDisposable
{
    private const int NumClasses = 13;
    private const float InvHalfByte = 1.0f / 127.5f;

    // Порог среднего отличия пикселя (0..255) на клетке 64x64.
    // Сетка/антиалиасинг и сжатие дают < 1.0, движение фигуры или подсветка > 5.0.
    private const double DirtyTileMadThreshold = 2.6;
    private const int PeriodicFullRefreshTicks = 40;

    private static readonly char[] ClassToFen =
    {
        '1',
        'P', 'N', 'B', 'R', 'Q', 'K',
        'p', 'n', 'b', 'r', 'q', 'k'
    };

    private readonly object _inferLock = new();
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _tileSize;
    private readonly bool _fixedBatchOne;
    private readonly int _boardRenderSize;
    private readonly int _planeLength;
    private readonly int _sampleFloatLength;
    private readonly double[,,] _cachedScreenProbs = new double[8, 8, NumClasses];
    private readonly int[] _dirtyTiles = new int[64];
    private float[] _tensorBuffer = Array.Empty<float>();

    private byte[] _pixelBuffer = Array.Empty<byte>();
    private byte[] _prevPixelBuffer = Array.Empty<byte>();
    private Bitmap? _resizedBitmap;
    private int _prevStride;
    private bool _hasCache;
    private int _ticksSinceFullRefresh;
    private bool _disposed;

    public OnnxBoardRecognizer(string modelPath, IAppLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("Путь к ONNX-модели не задан.", nameof(modelPath));
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX-модель не найдена: {modelPath}", modelPath);

        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1
        };

        _session = new InferenceSession(modelPath, sessionOptions);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
        ValidateModelMetadata(_session, _inputName, _outputName, out _tileSize, out _fixedBatchOne);
        _boardRenderSize = _tileSize * 8;
        _planeLength = _tileSize * _tileSize;
        _sampleFloatLength = 3 * _planeLength;
        _tensorBuffer = new float[64 * _sampleFloatLength];

        logger?.Info($"ONNX-распознаватель загружен: {modelPath} (input={_inputName}, output={_outputName}, tile={_tileSize}x{_tileSize}).");
    }

    public string EngineName => "OnnxBoardRecognizer";
    public bool IsReady => !_disposed;
    public int TileSize => _tileSize;
    public double LastInferenceMs { get; private set; }
    public int LastDirtySquareCount { get; private set; }
    public bool LastBoardVisible { get; private set; } = true;

    public void InvalidateCache()
    {
        lock (_inferLock)
        {
            _hasCache = false;
            _ticksSinceFullRefresh = 0;
        }
    }

    public BoardRecognitionResult ReprojectCachedResult(bool playAsBlack)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_inferLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_hasCache)
                return CreateEmptyResult();
            return BuildResultFromCache(playAsBlack);
        }
    }

    /// <summary>
    /// Автоматически определяет сторону (true = чёрные снизу), сравнивая
    /// распределение белых и чёрных фигур по верхней и нижней половинам доски.
    /// Возвращает null, если позиция слишком симметрична/пуста для уверенного вывода.
    /// </summary>
    public static bool? TryDetectPlayAsBlack(char[,] boardGrid, bool currentPlayAsBlack)
    {
        double whiteScreenRowSum = 0, blackScreenRowSum = 0;
        int whiteCount = 0, blackCount = 0;

        for (int sr = 0; sr < 8; sr++)
        {
            for (int sc = 0; sc < 8; sc++)
            {
                int br = currentPlayAsBlack ? 7 - sr : sr;
                int bc = currentPlayAsBlack ? 7 - sc : sc;
                char p = boardGrid[br, bc];
                if (p == '1') continue;

                // Короли и пешки дают самый сильный сигнал стороны
                double weight = (char.ToUpperInvariant(p) == 'K') ? 3.0
                    : (char.ToUpperInvariant(p) == 'P') ? 1.5
                    : 1.0;

                if (char.IsUpper(p))
                {
                    whiteScreenRowSum += sr * weight;
                    whiteCount += (int)Math.Round(weight);
                }
                else
                {
                    blackScreenRowSum += sr * weight;
                    blackCount += (int)Math.Round(weight);
                }
            }
        }

        if (whiteCount < 4 || blackCount < 4)
            return null;

        double whiteAvgRow = whiteScreenRowSum / whiteCount;
        double blackAvgRow = blackScreenRowSum / blackCount;

        // Если средние ряды разнесены хотя бы на 1.5 горизонтали — ориентация надёжна
        if (blackAvgRow - whiteAvgRow >= 1.5) return true;   // Чёрные внизу (sr ближе к 7)
        if (whiteAvgRow - blackAvgRow >= 1.5) return false;  // Белые внизу (sr ближе к 7)
        return null;
    }

    public static bool? TryDetectPlayAsBlackStrict(char[,] boardGrid, bool currentPlayAsBlack)
    {
        int wpTop = 0, wpBot = 0, bpTop = 0, bpBot = 0;
        int whitePieces = 0, blackPieces = 0;
        double whiteRowSum = 0, blackRowSum = 0;
        double whiteWeightSum = 0, blackWeightSum = 0;

        for (int sr = 0; sr < 8; sr++)
        {
            int br = currentPlayAsBlack ? 7 - sr : sr;
            for (int bc = 0; bc < 8; bc++)
            {
                char p = boardGrid[br, bc];
                if (p == '1') continue;

                bool isWhite = char.IsUpper(p);
                char upper = char.ToUpperInvariant(p);
                double weight = upper == 'K' ? 3.0 : upper == 'P' ? 1.5 : 1.0;

                if (isWhite)
                {
                    whitePieces++;
                    whiteRowSum += sr * weight;
                    whiteWeightSum += weight;
                    if (p == 'P')
                    {
                        if (sr <= 2) wpTop++;
                        else if (sr >= 5) wpBot++;
                    }
                }
                else
                {
                    blackPieces++;
                    blackRowSum += sr * weight;
                    blackWeightSum += weight;
                    if (p == 'p')
                    {
                        if (sr <= 2) bpTop++;
                        else if (sr >= 5) bpBot++;
                    }
                }
            }
        }

        if (wpTop >= 4 && bpBot >= 4 && wpBot <= 1 && bpTop <= 1)
            return true;
        if (wpBot >= 4 && bpTop >= 4 && wpTop <= 1 && bpBot <= 1)
            return false;

        int totalPieces = whitePieces + blackPieces;
        if (totalPieces >= 20 && whitePieces >= 8 && blackPieces >= 8
            && whiteWeightSum > 0 && blackWeightSum > 0)
        {
            double whiteAvgRow = whiteRowSum / whiteWeightSum;
            double blackAvgRow = blackRowSum / blackWeightSum;
            if (blackAvgRow - whiteAvgRow >= 2.2) return true;
            if (whiteAvgRow - blackAvgRow >= 2.2) return false;
        }

        return null;
    }

    public BoardRecognitionResult Recognize(Bitmap board, bool playAsBlack)
    {
        ArgumentNullException.ThrowIfNull(board);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_inferLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var sw = Stopwatch.StartNew();
            int stride = CaptureResizedPixels(board);

            if (!BoardPresenceChecker.IsCheckerboardPresent(_pixelBuffer, stride, _tileSize))
            {
                LastBoardVisible = false;
                LastDirtySquareCount = 0;
                sw.Stop();
                LastInferenceMs = sw.Elapsed.TotalMilliseconds;
                _hasCache = false;
                return CreateEmptyResult();
            }

            LastBoardVisible = true;

            bool forceFull = !_hasCache
                || _prevStride != stride
                || _prevPixelBuffer.Length != _pixelBuffer.Length
                || _ticksSinceFullRefresh >= PeriodicFullRefreshTicks;

            int dirtyCount = CollectDirtyTiles(stride, forceFull);
            LastDirtySquareCount = dirtyCount;

            if (dirtyCount > 0)
            {
                PackDirtyTilesToTensor(stride, dirtyCount);

                Span<double> probs = stackalloc double[NumClasses];
                if (_fixedBatchOne)
                {
                    for (int i = 0; i < dirtyCount; i++)
                    {
                        int tileIdx = _dirtyTiles[i];
                        int sr = tileIdx >> 3;
                        int sc = tileIdx & 7;

                        var singleTensor = new DenseTensor<float>(
                            _tensorBuffer.AsMemory(i * _sampleFloatLength, _sampleFloatLength),
                            new[] { 1, 3, _tileSize, _tileSize });
                        var singleInputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, singleTensor) };
                        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> singleOutputs = _session.Run(singleInputs);
                        Tensor<float> singleLogits = singleOutputs.First(v => v.Name == _outputName).AsTensor<float>();

                        ComputeSoftmax(singleLogits, 0, probs);
                        for (int k = 0; k < NumClasses; k++)
                            _cachedScreenProbs[sr, sc, k] = probs[k];
                    }
                }
                else
                {
                    int floatCount = dirtyCount * _sampleFloatLength;
                    var inputTensor = new DenseTensor<float>(
                        _tensorBuffer.AsMemory(0, floatCount),
                        new[] { dirtyCount, 3, _tileSize, _tileSize });

                    var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, inputTensor) };
                    using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(inputs);
                    Tensor<float> logitsTensor = outputs.First(v => v.Name == _outputName).AsTensor<float>();

                    for (int i = 0; i < dirtyCount; i++)
                    {
                        int tileIdx = _dirtyTiles[i];
                        int sr = tileIdx >> 3;
                        int sc = tileIdx & 7;

                        ComputeSoftmax(logitsTensor, i, probs);
                        for (int k = 0; k < NumClasses; k++)
                            _cachedScreenProbs[sr, sc, k] = probs[k];
                    }
                }

                // Сохраняем текущий буфер пикселей как эталон для следующего кадра
                if (_prevPixelBuffer.Length != _pixelBuffer.Length)
                    _prevPixelBuffer = new byte[_pixelBuffer.Length];
                Buffer.BlockCopy(_pixelBuffer, 0, _prevPixelBuffer, 0, _pixelBuffer.Length);
                _prevStride = stride;
                _hasCache = true;
                _ticksSinceFullRefresh = forceFull ? 0 : _ticksSinceFullRefresh + 1;
            }
            else
            {
                _ticksSinceFullRefresh++;
            }

            sw.Stop();
            LastInferenceMs = sw.Elapsed.TotalMilliseconds;

            return BuildResultFromCache(playAsBlack);
        }
    }

    private int CaptureResizedPixels(Bitmap sourceBoard)
    {
        if (_resizedBitmap == null || _resizedBitmap.Width != _boardRenderSize || _resizedBitmap.Height != _boardRenderSize)
        {
            _resizedBitmap?.Dispose();
            _resizedBitmap = new Bitmap(_boardRenderSize, _boardRenderSize, PixelFormat.Format24bppRgb);
        }

        Bitmap resized = _resizedBitmap!;
        using (Graphics g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(
                sourceBoard,
                new Rectangle(0, 0, _boardRenderSize, _boardRenderSize),
                new Rectangle(0, 0, sourceBoard.Width, sourceBoard.Height),
                GraphicsUnit.Pixel);
        }

        BitmapData bmpData = resized.LockBits(
            new Rectangle(0, 0, _boardRenderSize, _boardRenderSize),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);

        int stride = bmpData.Stride;
        int totalBytes = stride * _boardRenderSize;
        if (_pixelBuffer.Length != totalBytes)
            _pixelBuffer = new byte[totalBytes];

        try
        {
            Marshal.Copy(bmpData.Scan0, _pixelBuffer, 0, totalBytes);
        }
        finally
        {
            resized.UnlockBits(bmpData);
        }

        return stride;
    }

    private int CollectDirtyTiles(int stride, bool forceFull)
    {
        int count = 0;
        if (forceFull)
        {
            for (int i = 0; i < 64; i++)
                _dirtyTiles[count++] = i;
            return count;
        }

        // Проверяем каждый 2-й пиксель по X и Y (1024 точки на клетку) — < 0.4 мс на всю доску
        int sampledChannels = (_tileSize / 2) * (_tileSize / 2) * 3;
        long thresholdSum = (long)(DirtyTileMadThreshold * sampledChannels);

        for (int sr = 0; sr < 8; sr++)
        {
            int yBase = sr * _tileSize;
            for (int sc = 0; sc < 8; sc++)
            {
                int xBase = sc * _tileSize;
                long diffSum = 0;

                for (int ty = 0; ty < _tileSize; ty += 2)
                {
                    int rowOffset = (yBase + ty) * stride;
                    for (int tx = 0; tx < _tileSize; tx += 2)
                    {
                        int px = rowOffset + (xBase + tx) * 3;
                        diffSum += Math.Abs(_pixelBuffer[px] - _prevPixelBuffer[px]);
                        diffSum += Math.Abs(_pixelBuffer[px + 1] - _prevPixelBuffer[px + 1]);
                        diffSum += Math.Abs(_pixelBuffer[px + 2] - _prevPixelBuffer[px + 2]);
                    }
                }

                if (diffSum >= thresholdSum)
                    _dirtyTiles[count++] = (sr << 3) | sc;
            }
        }

        return count;
    }

    private void PackDirtyTilesToTensor(int stride, int dirtyCount)
    {
        for (int i = 0; i < dirtyCount; i++)
        {
            int tileIdx = _dirtyTiles[i];
            int sr = tileIdx >> 3;
            int sc = tileIdx & 7;
            int yBase = sr * _tileSize;
            int xBase = sc * _tileSize;

            int sampleOffset = i * _sampleFloatLength;
            int rOffset = sampleOffset;
            int gOffset = sampleOffset + _planeLength;
            int bOffset = sampleOffset + 2 * _planeLength;

            for (int ty = 0; ty < _tileSize; ty++)
            {
                int srcRow = (yBase + ty) * stride;
                int dstRow = ty * _tileSize;

                for (int tx = 0; tx < _tileSize; tx++)
                {
                    int px = srcRow + (xBase + tx) * 3;
                    int dstIdx = dstRow + tx;

                    float b = (_pixelBuffer[px] - 127.5f) * InvHalfByte;
                    float g = (_pixelBuffer[px + 1] - 127.5f) * InvHalfByte;
                    float r = (_pixelBuffer[px + 2] - 127.5f) * InvHalfByte;

                    _tensorBuffer[rOffset + dstIdx] = r;
                    _tensorBuffer[gOffset + dstIdx] = g;
                    _tensorBuffer[bOffset + dstIdx] = b;
                }
            }
        }
    }

    private static BoardRecognitionResult CreateEmptyResult()
    {
        var grid = new char[8, 8];
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
            grid[r, c] = '1';
        return new BoardRecognitionResult(grid, new double[8, 8, NumClasses], new double[8, 8], "8/8/8/8/8/8/8/8");
    }

    private BoardRecognitionResult BuildResultFromCache(bool playAsBlack)
    {
        char[,] grid = new char[8, 8];
        double[,,] classScores = new double[8, 8, NumClasses];
        double[,] confidences = new double[8, 8];
        var activeCells = new List<(int Row, int Col, double Score)>(32);

        for (int sr = 0; sr < 8; sr++)
        {
            for (int sc = 0; sc < 8; sc++)
            {
                int boardR = playAsBlack ? 7 - sr : sr;
                int boardC = playAsBlack ? 7 - sc : sc;

                int bestClass = 0;
                double bestProb = _cachedScreenProbs[sr, sc, 0];
                classScores[boardR, boardC, 0] = bestProb;

                for (int k = 1; k < NumClasses; k++)
                {
                    double p = _cachedScreenProbs[sr, sc, k];
                    classScores[boardR, boardC, k] = p;
                    if (p > bestProb)
                    {
                        bestProb = p;
                        bestClass = k;
                    }
                }

                char piece = ClassToFen[bestClass];
                grid[boardR, boardC] = piece;
                confidences[boardR, boardC] = bestClass == 0 ? 0.0 : bestProb;

                if (piece != '1')
                    activeCells.Add((boardR, boardC, bestProb));
            }
        }

        if (activeCells.Count > 32)
        {
            var toDrop = activeCells.OrderBy(x => x.Score).Take(activeCells.Count - 32);
            foreach (var item in toDrop)
                grid[item.Row, item.Col] = '1';
        }

        return new BoardRecognitionResult(grid, classScores, confidences, GridToFen(grid));
    }

    private static void ValidateModelMetadata(
        InferenceSession session,
        string inputName,
        string outputName,
        out int tileSize,
        out bool fixedBatchOne)
    {
        var inDims = session.InputMetadata[inputName].Dimensions;
        if (inDims.Length != 4)
            throw new InvalidOperationException($"ONNX input '{inputName}' must have rank 4 (NCHW), got {inDims.Length}.");

        if (inDims[1] != 3)
            throw new InvalidOperationException($"ONNX input '{inputName}' must have 3 RGB channels, got {inDims[1]}.");

        if (inDims[2] <= 0 || inDims[2] != inDims[3])
            throw new InvalidOperationException($"ONNX input '{inputName}' must have positive square spatial dims, got {inDims[2]}x{inDims[3]}.");

        if (inDims[0] <= 0)
            fixedBatchOne = false;
        else if (inDims[0] == 1)
            fixedBatchOne = true;
        else
            throw new InvalidOperationException($"ONNX input '{inputName}' has unsupported fixed batch dimension: {inDims[0]}.");

        var outDims = session.OutputMetadata[outputName].Dimensions;
        if (outDims.Length != 2 || outDims[1] != 13)
            throw new InvalidOperationException($"ONNX output '{outputName}' must have shape [B, 13], got [{string.Join(", ", outDims)}].");

        tileSize = inDims[2];
    }

    private static void ComputeSoftmax(Tensor<float> logits, int batchIdx, Span<double> probs)
    {
        double maxLogit = double.NegativeInfinity;
        for (int k = 0; k < NumClasses; k++)
        {
            double v = logits[batchIdx, k];
            if (v > maxLogit) maxLogit = v;
        }

        double sum = 0.0;
        for (int k = 0; k < NumClasses; k++)
        {
            double e = Math.Exp(logits[batchIdx, k] - maxLogit);
            probs[k] = e;
            sum += e;
        }

        double invSum = sum > 0.0 ? 1.0 / sum : 1.0 / NumClasses;
        for (int k = 0; k < NumClasses; k++)
            probs[k] *= invSum;
    }

    private static string GridToFen(char[,] grid)
    {
        var sb = new StringBuilder(72);
        for (int r = 0; r < 8; r++)
        {
            int emptyCount = 0;
            for (int c = 0; c < 8; c++)
            {
                if (grid[r, c] == '1') emptyCount++;
                else
                {
                    if (emptyCount > 0) { sb.Append(emptyCount); emptyCount = 0; }
                    sb.Append(grid[r, c]);
                }
            }
            if (emptyCount > 0) sb.Append(emptyCount);
            if (r < 7) sb.Append('/');
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_inferLock)
        {
            if (_disposed) return;
            _disposed = true;
            _resizedBitmap?.Dispose();
            _resizedBitmap = null;
            _session.Dispose();
        }
    }
}
