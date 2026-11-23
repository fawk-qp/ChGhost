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
    private const int TileSize = 64;
    private const int BoardRenderSize = TileSize * 8;
    private const int NumClasses = 13;
    private const int PlaneLength = TileSize * TileSize;
    private const int SampleFloatLength = 3 * PlaneLength;
    private const float InvHalfByte = 1.0f / 127.5f;

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
    private readonly float[] _tensorBuffer = new float[64 * SampleFloatLength];
    private byte[] _pixelBuffer = Array.Empty<byte>();
    private bool _disposed;

    public OnnxBoardRecognizer(string modelPath, IAppLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("Путь к ONNX-модели не задан.", nameof(modelPath));
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX-модель не найдена: {modelPath}", modelPath);

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1
        };

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
        logger?.Info($"ONNX-распознаватель загружен: {modelPath} (input={_inputName}, output={_outputName}).");
    }

    public string EngineName => "OnnxBoardRecognizer";

    public bool IsReady => !_disposed;

    public BoardRecognitionResult Recognize(Bitmap board, bool playAsBlack)
    {
        ArgumentNullException.ThrowIfNull(board);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_inferLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            FillInputTensor(board);

            var inputTensor = new DenseTensor<float>(_tensorBuffer, new[] { 64, 3, TileSize, TileSize });
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, inputTensor) };

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(inputs);
            Tensor<float> logitsTensor = outputs.First(v => v.Name == _outputName).AsTensor<float>();

            char[,] grid = new char[8, 8];
            double[,,] classScores = new double[8, 8, NumClasses];
            double[,] confidences = new double[8, 8];
            var activeCells = new List<(int Row, int Col, double Score)>(32);

            Span<double> probs = stackalloc double[NumClasses];

            for (int sr = 0; sr < 8; sr++)
            {
                for (int sc = 0; sc < 8; sc++)
                {
                    int batchIdx = sr * 8 + sc;
                    int boardR = playAsBlack ? 7 - sr : sr;
                    int boardC = playAsBlack ? 7 - sc : sc;

                    ComputeSoftmax(logitsTensor, batchIdx, probs);

                    int bestClass = 0;
                    double bestProb = probs[0];
                    for (int k = 0; k < NumClasses; k++)
                    {
                        classScores[boardR, boardC, k] = probs[k];
                        if (probs[k] > bestProb)
                        {
                            bestProb = probs[k];
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
    }

    private void FillInputTensor(Bitmap sourceBoard)
    {
        using var resized = new Bitmap(BoardRenderSize, BoardRenderSize, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(resized))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(
                sourceBoard,
                new Rectangle(0, 0, BoardRenderSize, BoardRenderSize),
                new Rectangle(0, 0, sourceBoard.Width, sourceBoard.Height),
                GraphicsUnit.Pixel);
        }

        BitmapData bmpData = resized.LockBits(
            new Rectangle(0, 0, BoardRenderSize, BoardRenderSize),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);

        int stride = bmpData.Stride;
        int totalBytes = stride * BoardRenderSize;
        if (_pixelBuffer.Length < totalBytes)
            _pixelBuffer = new byte[totalBytes];

        try
        {
            Marshal.Copy(bmpData.Scan0, _pixelBuffer, 0, totalBytes);
        }
        finally
        {
            resized.UnlockBits(bmpData);
        }

        for (int sr = 0; sr < 8; sr++)
        {
            int yBase = sr * TileSize;
            for (int sc = 0; sc < 8; sc++)
            {
                int xBase = sc * TileSize;
                int sampleOffset = (sr * 8 + sc) * SampleFloatLength;
                int rOffset = sampleOffset;
                int gOffset = sampleOffset + PlaneLength;
                int bOffset = sampleOffset + 2 * PlaneLength;

                for (int ty = 0; ty < TileSize; ty++)
                {
                    int srcRow = (yBase + ty) * stride;
                    int dstRow = ty * TileSize;

                    for (int tx = 0; tx < TileSize; tx++)
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
            _session.Dispose();
        }
    }
}
