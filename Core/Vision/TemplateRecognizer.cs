using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ChGhost.Core.Contracts;
using ChGhost.Core.Models;

namespace ChGhost.Core.Vision;

public sealed class TemplateRecognizer : ICalibratableRecognizer
{
    private const int PATCH_SIZE = 32;
    private static readonly char[] PieceTypesAll = { 'P', 'N', 'B', 'R', 'Q', 'K', 'p', 'n', 'b', 'r', 'q', 'k' };
    private readonly object _sync = new();
    private Dictionary<char, List<PieceTemplate>> _pieceSignatures = new();
    private double _whiteBodyR = 235.0, _whiteBodyG = 235.0, _whiteBodyB = 235.0;
    private double _blackBodyR = 45.0, _blackBodyG = 45.0, _blackBodyB = 45.0;

    public string EngineName => "TemplateRecognizer";

    public bool IsReady
    {
        get
        {
            lock (_sync)
                return HasValidSignatures(_pieceSignatures);
        }
    }

    public int CalibrationClassCount
    {
        get { lock (_sync) return _pieceSignatures.Count; }
    }

    public CalibrationOutcome CalibrateStartingPosition(Bitmap board, bool playAsBlack)
    {
        RecognitionSnapshot snapshot = CaptureSnapshot();
        var templatesGrid = new PieceTemplate[8, 8];
        int backRanksOccupied = 0;
        int centerOccupied = 0;
        var topLumas = new List<double>();
        var bottomLumas = new List<double>();

        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                PieceTemplate template = ExtractPieceTemplate32x32(board, r, c, snapshot);
                templatesGrid[r, c] = template;
                if (!IsCellOccupied(template)) continue;
                if (r == 0 || r == 1)
                {
                    backRanksOccupied++;
                    topLumas.Add(template.PieceLuma);
                }
                else if (r == 6 || r == 7)
                {
                    backRanksOccupied++;
                    bottomLumas.Add(template.PieceLuma);
                }
                else
                {
                    centerOccupied++;
                }
            }
        }

        bool isStartingPosition = (backRanksOccupied >= 24 && centerOccupied <= 3)
            || (!HasValidSignatures(snapshot.Signatures) && backRanksOccupied >= 20 && centerOccupied <= 4);
        bool detectedPlayAsBlack = playAsBlack;
        if (isStartingPosition && topLumas.Count >= 8 && bottomLumas.Count >= 8)
        {
            topLumas.Sort();
            bottomLumas.Sort();
            double topMedian = topLumas[topLumas.Count / 2];
            double bottomMedian = bottomLumas[bottomLumas.Count / 2];
            if (topMedian > bottomMedian + 20.0)
                detectedPlayAsBlack = true;
            else if (bottomMedian > topMedian + 20.0)
                detectedPlayAsBlack = false;
        }

        if (isStartingPosition)
        {
            string[] startRows =
            {
                "rnbqkbnr", "pppppppp", "........", "........",
                "........", "........", "PPPPPPPP", "RNBQKBNR"
            };
            var signatures = new Dictionary<char, List<PieceTemplate>>();
            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    int boardR = detectedPlayAsBlack ? 7 - r : r;
                    int boardC = detectedPlayAsBlack ? 7 - c : c;
                    char piece = startRows[boardR][boardC];
                    if (piece == '.') continue;
                    PieceTemplate template = templatesGrid[r, c];
                    template.IsWhite = char.IsUpper(piece);
                    if (!signatures.TryGetValue(piece, out var samples))
                        signatures[piece] = samples = new List<PieceTemplate>();
                    samples.Add(template);
                }
            }

            lock (_sync)
            {
                _pieceSignatures = signatures;
                RecalculateBodyColors();
            }
        }

        lock (_sync)
        {
            return new CalibrationOutcome(
                isStartingPosition,
                backRanksOccupied,
                centerOccupied,
                detectedPlayAsBlack,
                0.299 * _whiteBodyR + 0.587 * _whiteBodyG + 0.114 * _whiteBodyB,
                0.299 * _blackBodyR + 0.587 * _blackBodyG + 0.114 * _blackBodyB);
        }
    }

    public bool CalibratePiece(Bitmap board, Square square, PieceClass piece)
    {
        if (square.Row < 0 || square.Row >= 8 || square.Col < 0 || square.Col >= 8)
            return false;
        char fenPiece = ToFenPiece(piece);
        if (fenPiece == '1') return false;

        PieceTemplate template = ExtractPieceTemplate32x32(board, square.Row, square.Col, CaptureSnapshot());
        template.IsWhite = char.IsUpper(fenPiece);
        lock (_sync)
        {
            if (!_pieceSignatures.TryGetValue(fenPiece, out var samples))
                _pieceSignatures[fenPiece] = samples = new List<PieceTemplate>();
            samples.Add(template);
            if (samples.Count > 4) samples.RemoveAt(0);
            RecalculateBodyColors();
        }
        return true;
    }

    public void ResetCalibration()
    {
        lock (_sync)
        {
            _pieceSignatures.Clear();
            _whiteBodyR = _whiteBodyG = _whiteBodyB = 235.0;
            _blackBodyR = _blackBodyG = _blackBodyB = 45.0;
        }
    }

    public SavedConfig ExportCalibration()
    {
        lock (_sync)
        {
            return new SavedConfig
            {
                WhiteBodyR = _whiteBodyR,
                WhiteBodyG = _whiteBodyG,
                WhiteBodyB = _whiteBodyB,
                BlackBodyR = _blackBodyR,
                BlackBodyG = _blackBodyG,
                BlackBodyB = _blackBodyB,
                Signatures = CloneSignatures(_pieceSignatures)
            };
        }
    }

    public void ImportCalibration(SavedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_sync)
        {
            _whiteBodyR = config.WhiteBodyR;
            _whiteBodyG = config.WhiteBodyG;
            _whiteBodyB = config.WhiteBodyB;
            _blackBodyR = config.BlackBodyR;
            _blackBodyG = config.BlackBodyG;
            _blackBodyB = config.BlackBodyB;
            _pieceSignatures = CloneSignatures(config.Signatures ?? new Dictionary<char, List<PieceTemplate>>());
        }
    }

    public BoardRecognitionResult Recognize(Bitmap board, bool playAsBlack)
    {
        ArgumentNullException.ThrowIfNull(board);
        RecognitionSnapshot snapshot = CaptureSnapshot();
        char[,] grid = new char[8, 8];
        double[,,] classScores = new double[8, 8, 13];
        double[,] confidences = new double[8, 8];
        var activeCells = new List<(int Row, int Col, double Score)>();

        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                int boardR = playAsBlack ? 7 - r : r;
                int boardC = playAsBlack ? 7 - c : c;
                PieceTemplate template = ExtractPieceTemplate32x32(board, r, c, snapshot);
                var result = RecognizeCellFull(template, snapshot.Signatures,
                    snapshot.WhiteBodyR, snapshot.WhiteBodyG, snapshot.WhiteBodyB,
                    snapshot.BlackBodyR, snapshot.BlackBodyG, snapshot.BlackBodyB);
                grid[boardR, boardC] = result.piece;
                confidences[boardR, boardC] = result.confidence;
                for (int i = 0; i < result.scores.Length; i++)
                    classScores[boardR, boardC, i + 1] = result.scores[i];
                if (result.piece != '1')
                    activeCells.Add((boardR, boardC, result.confidence));
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

    private static string GridToFen(char[,] grid)
    {
        var sb = new System.Text.StringBuilder();
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

    private RecognitionSnapshot CaptureSnapshot()
    {
        lock (_sync)
        {
            return new RecognitionSnapshot(
                CloneSignatures(_pieceSignatures),
                _whiteBodyR, _whiteBodyG, _whiteBodyB,
                _blackBodyR, _blackBodyG, _blackBodyB);
        }
    }

    private static Dictionary<char, List<PieceTemplate>> CloneSignatures(Dictionary<char, List<PieceTemplate>> signatures) =>
        signatures.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(template => new PieceTemplate
            {
                EdgePatch = (double[])template.EdgePatch.Clone(),
                BodyR = template.BodyR,
                BodyG = template.BodyG,
                BodyB = template.BodyB,
                PieceLuma = template.PieceLuma,
                RawContrast = template.RawContrast,
                CenterEdgePixels = template.CenterEdgePixels,
                IsWhite = template.IsWhite
            }).ToList());

    private static bool HasValidSignatures(Dictionary<char, List<PieceTemplate>> signatures)
    {
        if (signatures.Count < 12) return false;
        int expected = PATCH_SIZE * PATCH_SIZE;
        foreach (var pair in signatures)
        {
            if (pair.Value == null || pair.Value.Count == 0) return false;
            if (pair.Value.Any(template => template.EdgePatch.Length != expected))
                return false;
        }
        return true;
    }

    private void RecalculateBodyColors()
    {
        var whiteRs = new List<double>();
        var whiteGs = new List<double>();
        var whiteBs = new List<double>();
        var blackRs = new List<double>();
        var blackGs = new List<double>();
        var blackBs = new List<double>();

        foreach (var pair in _pieceSignatures)
        {
            bool isWhite = char.IsUpper(pair.Key);
            foreach (var template in pair.Value)
            {
                if (isWhite)
                {
                    whiteRs.Add(template.BodyR);
                    whiteGs.Add(template.BodyG);
                    whiteBs.Add(template.BodyB);
                }
                else
                {
                    blackRs.Add(template.BodyR);
                    blackGs.Add(template.BodyG);
                    blackBs.Add(template.BodyB);
                }
            }
        }

        if (whiteRs.Count > 0)
        {
            whiteRs.Sort(); whiteGs.Sort(); whiteBs.Sort();
            _whiteBodyR = whiteRs[whiteRs.Count / 2];
            _whiteBodyG = whiteGs[whiteGs.Count / 2];
            _whiteBodyB = whiteBs[whiteBs.Count / 2];
        }
        if (blackRs.Count > 0)
        {
            blackRs.Sort(); blackGs.Sort(); blackBs.Sort();
            _blackBodyR = blackRs[blackRs.Count / 2];
            _blackBodyG = blackGs[blackGs.Count / 2];
            _blackBodyB = blackBs[blackBs.Count / 2];
        }
    }

    private static char ToFenPiece(PieceClass piece) => piece switch
    {
        PieceClass.WhitePawn => 'P', PieceClass.WhiteKnight => 'N', PieceClass.WhiteBishop => 'B',
        PieceClass.WhiteRook => 'R', PieceClass.WhiteQueen => 'Q', PieceClass.WhiteKing => 'K',
        PieceClass.BlackPawn => 'p', PieceClass.BlackKnight => 'n', PieceClass.BlackBishop => 'b',
        PieceClass.BlackRook => 'r', PieceClass.BlackQueen => 'q', PieceClass.BlackKing => 'k',
        _ => '1'
    };

    private sealed record RecognitionSnapshot(
        Dictionary<char, List<PieceTemplate>> Signatures,
        double WhiteBodyR, double WhiteBodyG, double WhiteBodyB,
        double BlackBodyR, double BlackBodyG, double BlackBodyB);

    private static bool IsCellOccupied(PieceTemplate tpl)

    {

        return tpl.CenterEdgePixels >= 28 && tpl.RawContrast >= 12.0;

    }

    private static double RgbDistance(double r1, double g1, double b1, double r2, double g2, double b2)

    {

        double dr = r1 - r2;

        double dg = g1 - g2;

        double db = b1 - b2;

        return Math.Sqrt(dr * dr + dg * dg + db * db);

    }

    private PieceTemplate ExtractPieceTemplate32x32(Bitmap board, int row, int col, RecognitionSnapshot? snapshot = null)

    {

        double cellW = board.Width / 8.0;

        double cellH = board.Height / 8.0;

        int x0 = (int)(col * cellW);

        int y0 = (int)(row * cellH);

        (double fx, double fy)[] bgPoints =

        {

            (0.50, 0.04),

            (0.90, 0.06),

            (0.95, 0.45),

            (0.05, 0.45),

            (0.08, 0.94)

        };

        double[] bgRs = new double[5];

        double[] bgGs = new double[5];

        double[] bgBs = new double[5];

        for (int i = 0; i < 5; i++)

        {

            int px = Math.Clamp(x0 + (int)(cellW * bgPoints[i].fx), 0, board.Width - 1);

            int py = Math.Clamp(y0 + (int)(cellH * bgPoints[i].fy), 0, board.Height - 1);

            Color c = board.GetPixel(px, py);

            bgRs[i] = c.R;

            bgGs[i] = c.G;

            bgBs[i] = c.B;

        }

        Array.Sort(bgRs);

        Array.Sort(bgGs);

        Array.Sort(bgBs);

        double bgR = bgRs[2];

        double bgG = bgGs[2];

        double bgB = bgBs[2];

        int srcX = Math.Clamp((int)(x0 + cellW * 0.06), 0, Math.Max(0, board.Width - 1));

        int srcY = Math.Clamp((int)(y0 + cellH * 0.06), 0, Math.Max(0, board.Height - 1));

        int srcW = Math.Clamp((int)(cellW * 0.88), 1, board.Width - srcX);

        int srcH = Math.Clamp((int)(cellH * 0.88), 1, board.Height - srcY);

        using Bitmap patchBmp = new Bitmap(PATCH_SIZE, PATCH_SIZE, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

        using (Graphics g = Graphics.FromImage(patchBmp))

        {

            g.InterpolationMode = InterpolationMode.HighQualityBilinear;

            g.DrawImage(board, new System.Drawing.Rectangle(0, 0, PATCH_SIZE, PATCH_SIZE),

                        new System.Drawing.Rectangle(srcX, srcY, srcW, srcH), GraphicsUnit.Pixel);

        }

        var bmpData = patchBmp.LockBits(

            new System.Drawing.Rectangle(0, 0, PATCH_SIZE, PATCH_SIZE),

            System.Drawing.Imaging.ImageLockMode.ReadOnly,

            System.Drawing.Imaging.PixelFormat.Format24bppRgb);

        int stride = bmpData.Stride;

        byte[] rawBytes = new byte[stride * PATCH_SIZE];

        Marshal.Copy(bmpData.Scan0, rawBytes, 0, rawBytes.Length);

        patchBmp.UnlockBits(bmpData);

        double[,] rGrid = new double[PATCH_SIZE, PATCH_SIZE];

        double[,] gGrid = new double[PATCH_SIZE, PATCH_SIZE];

        double[,] bGrid = new double[PATCH_SIZE, PATCH_SIZE];

        double[,] distGrid = new double[PATCH_SIZE, PATCH_SIZE];

        for (int y = 0; y < PATCH_SIZE; y++)

        {

            int rowOffset = y * stride;

            for (int x = 0; x < PATCH_SIZE; x++)

            {

                int idx = rowOffset + x * 3;

                double b = rawBytes[idx];

                double g = rawBytes[idx + 1];

                double r = rawBytes[idx + 2];

                rGrid[y, x] = r;

                gGrid[y, x] = g;

                bGrid[y, x] = b;

                distGrid[y, x] = RgbDistance(r, g, b, bgR, bgG, bgB);

            }

        }

        double[,] gradGrid = new double[PATCH_SIZE, PATCH_SIZE];

        int centerEdgePixels = 0;

        for (int y = 0; y < PATCH_SIZE; y++)

        {

            int ym = Math.Max(0, y - 1);

            int yp = Math.Min(PATCH_SIZE - 1, y + 1);

            for (int x = 0; x < PATCH_SIZE; x++)

            {

                int xm = Math.Max(0, x - 1);

                int xp = Math.Min(PATCH_SIZE - 1, x + 1);

                double gxR = (rGrid[ym, xp] + 2 * rGrid[y, xp] + rGrid[yp, xp] - rGrid[ym, xm] - 2 * rGrid[y, xm] - rGrid[yp, xm]) * 0.25;

                double gyR = (rGrid[yp, xm] + 2 * rGrid[yp, x] + rGrid[yp, xp] - rGrid[ym, xm] - 2 * rGrid[ym, x] - rGrid[ym, xp]) * 0.25;

                double gxG = (gGrid[ym, xp] + 2 * gGrid[y, xp] + gGrid[yp, xp] - gGrid[ym, xm] - 2 * gGrid[y, xm] - gGrid[yp, xm]) * 0.25;

                double gyG = (gGrid[yp, xm] + 2 * gGrid[yp, x] + gGrid[yp, xp] - gGrid[ym, xm] - 2 * gGrid[ym, x] - gGrid[ym, xp]) * 0.25;

                double gxB = (bGrid[ym, xp] + 2 * bGrid[y, xp] + bGrid[yp, xp] - bGrid[ym, xm] - 2 * bGrid[y, xm] - bGrid[yp, xm]) * 0.25;

                double gyB = (bGrid[yp, xm] + 2 * bGrid[yp, x] + bGrid[yp, xp] - bGrid[ym, xm] - 2 * bGrid[ym, x] - bGrid[ym, xp]) * 0.25;

                double grad = Math.Sqrt((gxR * gxR + gyR * gyR + gxG * gxG + gyG * gyG + gxB * gxB + gyB * gyB) / 3.0);

                gradGrid[y, x] = grad;

                if (x >= 5 && x <= 26 && y >= 5 && y <= 26 && (grad >= 18.0 || distGrid[y, x] >= 26.0))

                    centerEdgePixels++;

            }

        }

        bool[,] hSpan = new bool[PATCH_SIZE, PATCH_SIZE];

        for (int y = 2; y < PATCH_SIZE - 2; y++)

        {

            int minX = PATCH_SIZE, maxX = -1;

            for (int x = 2; x < PATCH_SIZE - 2; x++)

            {

                if (gradGrid[y, x] >= 18.0 || distGrid[y, x] >= 24.0)

                {

                    if (x < minX) minX = x;

                    if (x > maxX) maxX = x;

                }

            }

            if (maxX - minX >= 4)

            {

                for (int x = minX + 1; x < maxX; x++)

                    hSpan[y, x] = true;

            }

        }

        bool[,] vSpan = new bool[PATCH_SIZE, PATCH_SIZE];

        for (int x = 2; x < PATCH_SIZE - 2; x++)

        {

            int minY = PATCH_SIZE, maxY = -1;

            for (int y = 2; y < PATCH_SIZE - 2; y++)

            {

                if (gradGrid[y, x] >= 18.0 || distGrid[y, x] >= 24.0)

                {

                    if (y < minY) minY = y;

                    if (y > maxY) maxY = y;

                }

            }

            if (maxY - minY >= 4)

            {

                for (int y = minY + 1; y < maxY; y++)

                    vSpan[y, x] = true;

            }

        }

        bool[,] inHull = new bool[PATCH_SIZE, PATCH_SIZE];

        var distinctFlatR = new List<double>(256);

        var distinctFlatG = new List<double>(256);

        var distinctFlatB = new List<double>(256);

        var allFlatR = new List<double>(256);

        var allFlatG = new List<double>(256);

        var allFlatB = new List<double>(256);

        var hullAll = new List<(double grad, double r, double g, double b)>(300);

        for (int y = 2; y < PATCH_SIZE - 2; y++)

        {

            for (int x = 2; x < PATCH_SIZE - 2; x++)

            {

                if (!hSpan[y, x] || !vSpan[y, x]) continue;

                inHull[y, x] = true;

                double gr = gradGrid[y, x];

                double r = rGrid[y, x];

                double g = gGrid[y, x];

                double b = bGrid[y, x];

                hullAll.Add((gr, r, g, b));

                if (gr < 28.0)

                {

                    if (x >= 7 && x <= 24 && y >= 7 && y <= 26)

                    {

                        allFlatR.Add(r);

                        allFlatG.Add(g);

                        allFlatB.Add(b);

                    }

                    if (distGrid[y, x] >= 16.0)

                    {

                        distinctFlatR.Add(r);

                        distinctFlatG.Add(g);

                        distinctFlatB.Add(b);

                    }

                }

            }

        }

        double bodyR = 128.0, bodyG = 128.0, bodyB = 128.0;

        if (distinctFlatR.Count >= 18 && (allFlatR.Count == 0 || distinctFlatR.Count >= allFlatR.Count * 0.45))

        {

            distinctFlatR.Sort(); distinctFlatG.Sort(); distinctFlatB.Sort();

            int mid = distinctFlatR.Count / 2;

            bodyR = distinctFlatR[mid];

            bodyG = distinctFlatG[mid];

            bodyB = distinctFlatB[mid];

        }

        else if (allFlatR.Count >= 12)

        {

            allFlatR.Sort(); allFlatG.Sort(); allFlatB.Sort();

            int mid = allFlatR.Count / 2;

            bodyR = allFlatR[mid];

            bodyG = allFlatG[mid];

            bodyB = allFlatB[mid];

        }

        else if (hullAll.Count > 0)

        {

            var flattest = hullAll.OrderBy(p => p.grad).Take(Math.Max(1, hullAll.Count / 2)).ToList();

            var fr = flattest.Select(p => p.r).OrderBy(v => v).ToList();

            var fg = flattest.Select(p => p.g).OrderBy(v => v).ToList();

            var fb = flattest.Select(p => p.b).OrderBy(v => v).ToList();

            int mid = fr.Count / 2;

            bodyR = fr[mid];

            bodyG = fg[mid];

            bodyB = fb[mid];

        }

        int len = PATCH_SIZE * PATCH_SIZE;

        double[] edgeVec = new double[len];

        for (int y = 0; y < PATCH_SIZE; y++)

        {

            for (int x = 0; x < PATCH_SIZE; x++)

            {

                bool nearHull = inHull[y, x]

                    || (x > 0 && inHull[y, x - 1])

                    || (x < PATCH_SIZE - 1 && inHull[y, x + 1])

                    || (y > 0 && inHull[y - 1, x])

                    || (y < PATCH_SIZE - 1 && inHull[y + 1, x]);

                double gVal = nearHull ? gradGrid[y, x] : 0.0;

                double hVal = inHull[y, x] ? 45.0 : 0.0;

                edgeVec[y * PATCH_SIZE + x] = gVal + hVal;

            }

        }

        double mean = edgeVec.Average();

        double std = Math.Sqrt(edgeVec.Sum(v => (v - mean) * (v - mean)) / len);

        double[] norm = new double[len];

        if (std > 1.0)

        {

            for (int i = 0; i < len; i++)

                norm[i] = (edgeVec[i] - mean) / std;

        }

        double pieceLuma = 0.299 * bodyR + 0.587 * bodyG + 0.114 * bodyB;

        double distW = RgbDistance(bodyR, bodyG, bodyB, snapshot?.WhiteBodyR ?? _whiteBodyR, snapshot?.WhiteBodyG ?? _whiteBodyG, snapshot?.WhiteBodyB ?? _whiteBodyB);

        double distB = RgbDistance(bodyR, bodyG, bodyB, snapshot?.BlackBodyR ?? _blackBodyR, snapshot?.BlackBodyG ?? _blackBodyG, snapshot?.BlackBodyB ?? _blackBodyB);

        return new PieceTemplate

        {

            EdgePatch = norm,

            BodyR = bodyR,

            BodyG = bodyG,

            BodyB = bodyB,

            PieceLuma = pieceLuma,

            RawContrast = std,

            CenterEdgePixels = centerEdgePixels,

            IsWhite = distW <= distB

        };

    }

    private static double ComputeShiftDot(double[] a, double[] b)

    {

        int expectedLen = PATCH_SIZE * PATCH_SIZE;

        if (a.Length != expectedLen || b.Length != expectedLen)

            return -1.0;

        double bestScore = -1.0;

        const double invCount = 1.0 / (30.0 * 30.0);

        for (int dy = -1; dy <= 1; dy++)

        {

            for (int dx = -1; dx <= 1; dx++)

            {

                double dot = 0.0;

                for (int y = 1; y < PATCH_SIZE - 1; y++)

                {

                    int rowA = y * PATCH_SIZE;

                    int rowB = (y + dy) * PATCH_SIZE + dx;

                    for (int x = 1; x < PATCH_SIZE - 1; x++)

                    {

                        dot += a[rowA + x] * b[rowB + x];

                    }

                }

                double score = dot * invCount;

                if (score > bestScore)

                    bestScore = score;

            }

        }

        return bestScore;

    }

    private static (char piece, double confidence, double[] scores) RecognizeCellFull(

        PieceTemplate tpl,

        Dictionary<char, List<PieceTemplate>> signatures,

        double wR, double wG, double wB,

        double bR, double bG, double bB)

    {

        double[] scores = new double[12];

        Array.Fill(scores, -2.0);

        if (!IsCellOccupied(tpl))

            return ('1', 0.0, scores);

        double distToWhite = RgbDistance(tpl.BodyR, tpl.BodyG, tpl.BodyB, wR, wG, wB) / 441.67;

        double distToBlack = RgbDistance(tpl.BodyR, tpl.BodyG, tpl.BodyB, bR, bG, bB) / 441.67;

        char bestPiece = '1';

        double bestScore = -2.0;

        for (int idx = 0; idx < PieceTypesAll.Length; idx++)

        {

            char key = PieceTypesAll[idx];

            if (!signatures.TryGetValue(key, out var samples))

                continue;

            bool keyIsWhite = idx < 6;

            double sideColorPenalty = keyIsWhite ? distToWhite : distToBlack;

            double maxCombined = -2.0;

            foreach (var sample in samples)

            {

                double shapeSim = ComputeShiftDot(tpl.EdgePatch, sample.EdgePatch);

                double sampleColorDist = RgbDistance(tpl.BodyR, tpl.BodyG, tpl.BodyB, sample.BodyR, sample.BodyG, sample.BodyB) / 441.67;

                double combined = shapeSim - 0.65 * sampleColorDist - 0.55 * sideColorPenalty;

                if (combined > maxCombined)

                    maxCombined = combined;

            }

            scores[idx] = maxCombined;

            if (maxCombined > bestScore)

            {

                bestScore = maxCombined;

                bestPiece = key;

            }

        }

        if (bestScore < 0.14)

            return ('1', 0.0, scores);

        return (bestPiece, bestScore, scores);

    }
}
