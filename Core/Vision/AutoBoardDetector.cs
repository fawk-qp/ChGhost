using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ChGhost.Core.Contracts;
using ChGhost.Core.Models;

namespace ChGhost.Core.Vision;

public sealed record BoardDetectionDiagnostics(
    BoardRect? Rect,
    int MatchedCorners,
    int ParitySquares,
    int OuterRingMatches,
    double ElapsedMs
);

public sealed class AutoBoardDetector : IBoardDetector
{
    private const int MinBoardSizePx = 160;
    private const int MinSaddleScore = 26;
    private const int MaxPeaks = 600;
    private const double MinParityRgbDistance = 16.0;
    private const int MinTotalParityMatches = 48;
    private const int MinOuterRingMatches = 20;

    private readonly IAppLogger? _logger;
    private readonly Func<(Bitmap Screen, int OffsetX, int OffsetY)>? _screenCapturer;
    private readonly ManualBoardDetector _manualBounds = new();

    public Point? TopLeft => _manualBounds.TopLeft;
    public Point? BottomRight => _manualBounds.BottomRight;

    public void SetTopLeft(int x, int y) => _manualBounds.SetTopLeft(x, y);
    public void SetBottomRight(int x, int y) => _manualBounds.SetBottomRight(x, y);
    public void Reset() => _manualBounds.Reset();

    public AutoBoardDetector(
        IAppLogger? logger = null,
        Func<(Bitmap Screen, int OffsetX, int OffsetY)>? screenCapturer = null)
    {
        _logger = logger;
        _screenCapturer = screenCapturer;
    }

    public BoardRect? Detect(Bitmap? fullScreen = null)
        => DetectWithDiagnostics(fullScreen).Rect;

    public BoardDetectionDiagnostics DetectWithDiagnostics(Bitmap? fullScreen = null)
    {
        var sw = Stopwatch.StartNew();

        Bitmap? ownedBitmap = null;
        int offsetX = 0;
        int offsetY = 0;

        try
        {
            Bitmap target;
            if (fullScreen is not null)
            {
                target = fullScreen;
            }
            else if (_screenCapturer is not null)
            {
                var captured = _screenCapturer();
                ownedBitmap = captured.Screen;
                offsetX = captured.OffsetX;
                offsetY = captured.OffsetY;
                target = ownedBitmap;
            }
            else
            {
                return new BoardDetectionDiagnostics(null, 0, 0, 0, sw.Elapsed.TotalMilliseconds);
            }

            if (target.Width < MinBoardSizePx || target.Height < MinBoardSizePx)
                return new BoardDetectionDiagnostics(null, 0, 0, 0, sw.Elapsed.TotalMilliseconds);

            var localResult = DetectInBitmap(target, sw);
            if (localResult.Rect is not { } localRect)
                return localResult;

            var shifted = new BoardRect(
                localRect.Left + offsetX,
                localRect.Top + offsetY,
                localRect.Right + offsetX,
                localRect.Bottom + offsetY);

            return localResult with { Rect = shifted };
        }
        catch (Exception ex)
        {
            _logger?.Error("Ошибка автоматического поиска доски.", ex);
            return new BoardDetectionDiagnostics(null, 0, 0, 0, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            ownedBitmap?.Dispose();
        }
    }

    private static BoardDetectionDiagnostics DetectInBitmap(Bitmap bitmap, Stopwatch sw)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        BitmapData bmpData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);

        int stride = bmpData.Stride;
        byte[] pixels = new byte[stride * height];
        try
        {
            Marshal.Copy(bmpData.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(bmpData);
        }

        int maxDim = Math.Max(width, height);
        int ds = maxDim >= 3000 ? 3 : (maxDim >= 1100 ? 2 : 1);
        int dWork = ds == 1 ? 4 : (ds == 2 ? 3 : 2);
        int ww = width / ds;
        int wh = height / ds;

        if (ww < 40 || wh < 40)
            return new BoardDetectionDiagnostics(null, 0, 0, 0, sw.Elapsed.TotalMilliseconds);

        // 1. Построение карты X-перекрёстков (Saddle Map) в рабочем разрешении
        int[] saddleMap = new int[ww * wh];
        sbyte[] signMap = new sbyte[ww * wh];

        for (int wy = dWork; wy < wh - dWork; wy++)
        {
            int fy = wy * ds;
            int rowBase = wy * ww;
            for (int wx = dWork; wx < ww - dWork; wx++)
            {
                int fx = wx * ds;
                int offsetPx = dWork * ds;
                int score = ComputeSaddleScore(pixels, stride, fx, fy, offsetPx, out sbyte sign);
                if (score >= MinSaddleScore)
                {
                    saddleMap[rowBase + wx] = score;
                    signMap[rowBase + wx] = sign;
                }
            }
        }

        // 2. Извлечение центроидов плато локальных максимумов
        var peaks = ExtractPlateauCentroids(saddleMap, signMap, ww, wh, dWork);
        if (peaks.Count < 16)
            return new BoardDetectionDiagnostics(null, 0, 0, 0, sw.Elapsed.TotalMilliseconds);

        // 3. Поиск доминирующих шагов сетки по парам перекрёстков с противоположной полярностью
        int minCellWork = Math.Max(10, (MinBoardSizePx / 8) / ds);
        int maxCellWork = Math.Min(ww, wh) / 8 + 2;
        if (maxCellWork <= minCellWork)
            return new BoardDetectionDiagnostics(null, 0, 0, 0, sw.Elapsed.TotalMilliseconds);

        var candidateSteps = FindDominantSteps(peaks, minCellWork, maxCellWork);
        if (candidateSteps.Count == 0)
            return new BoardDetectionDiagnostics(null, 0, 0, 0, sw.Elapsed.TotalMilliseconds);

        // 4. Генерация гипотез решётки 7x7 из найденных шагов
        var hypotheses = BuildLatticeHypotheses(peaks, candidateSteps);
        if (hypotheses.Count == 0)
            return new BoardDetectionDiagnostics(null, 0, 0, 0, sw.Elapsed.TotalMilliseconds);

        // 5. Уточнение всех 49 стыков в полном разрешении + IRLS линейная регрессия + 64-клеточная верификация
        BoardDetectionDiagnostics best = new(null, 0, 0, 0, 0);
        double bestScore = double.NegativeInfinity;

        foreach (var hyp in hypotheses)
        {
            var refined = RefineAndVerifyCandidate(pixels, stride, width, height, hyp, ds);
            if (refined is null) continue;

            var (rect, corners, parity, outerRing, score) = refined.Value;
            if (score > bestScore)
            {
                bestScore = score;
                best = new BoardDetectionDiagnostics(rect, corners, parity, outerRing, 0);
            }
        }

        sw.Stop();
        return best with { ElapsedMs = sw.Elapsed.TotalMilliseconds };
    }

    private static int ComputeSaddleScore(
        byte[] pixels, int stride, int cx, int cy, int d, out sbyte sign)
    {
        int rowTop = (cy - d) * stride;
        int rowBot = (cy + d) * stride;
        int xLeft = (cx - d) * 3;
        int xRight = (cx + d) * 3;

        int tl = rowTop + xLeft;
        int tr = rowTop + xRight;
        int bl = rowBot + xLeft;
        int br = rowBot + xRight;

        int tlB = pixels[tl], tlG = pixels[tl + 1], tlR = pixels[tl + 2];
        int trB = pixels[tr], trG = pixels[tr + 1], trR = pixels[tr + 2];
        int blB = pixels[bl], blG = pixels[bl + 1], blR = pixels[bl + 2];
        int brB = pixels[br], brG = pixels[br + 1], brR = pixels[br + 2];

        int diag1 = Math.Abs(tlR - brR) + Math.Abs(tlG - brG) + Math.Abs(tlB - brB);
        int diag2 = Math.Abs(trR - blR) + Math.Abs(trG - blG) + Math.Abs(trB - blB);

        int dR = (tlR + brR) - (trR + blR);
        int dG = (tlG + brG) - (trG + blG);
        int dB = (tlB + brB) - (trB + blB);

        int cross = (Math.Abs(dR) + Math.Abs(dG) + Math.Abs(dB)) >> 1;
        int score = cross - (diag1 + diag2);

        if (score <= 0)
        {
            sign = 0;
            return 0;
        }

        int signedLuma = dR * 3 + dG * 6 + dB;
        if (Math.Abs(signedLuma) >= 24)
        {
            sign = signedLuma > 0 ? (sbyte)1 : (sbyte)-1;
        }
        else
        {
            int dom = Math.Abs(dR) >= Math.Abs(dG)
                ? (Math.Abs(dR) >= Math.Abs(dB) ? dR : dB)
                : (Math.Abs(dG) >= Math.Abs(dB) ? dG : dB);
            sign = dom >= 0 ? (sbyte)1 : (sbyte)-1;
        }

        return score;
    }

    private static List<JunctionPeak> ExtractPlateauCentroids(
        int[] saddleMap, sbyte[] signMap, int ww, int wh, int dWork)
    {
        var seeds = new List<(int Idx, int Score)>(1024);

        for (int y = dWork; y < wh - dWork; y++)
        {
            int row = y * ww;
            for (int x = dWork; x < ww - dWork; x++)
            {
                int idx = row + x;
                int val = saddleMap[idx];
                if (val < MinSaddleScore) continue;

                // Быстрая проверка локального 3x3 нестрогого максимума
                if (val < saddleMap[idx - 1] || val < saddleMap[idx + 1]
                    || val < saddleMap[idx - ww] || val < saddleMap[idx + ww]
                    || val < saddleMap[idx - ww - 1] || val < saddleMap[idx - ww + 1]
                    || val < saddleMap[idx + ww - 1] || val < saddleMap[idx + ww + 1])
                {
                    continue;
                }

                seeds.Add((idx, val));
            }
        }

        seeds.Sort((a, b) => b.Score.CompareTo(a.Score));

        bool[] suppressed = new bool[ww * wh];
        int[] queue = new int[256];
        var result = new List<JunctionPeak>(MaxPeaks);
        int boxRadius = dWork + 2;

        foreach (var (seedIdx, seedScore) in seeds)
        {
            if (suppressed[seedIdx]) continue;
            if (result.Count >= MaxPeaks) break;

            int sx = seedIdx % ww;
            int sy = seedIdx / ww;
            sbyte seedSign = signMap[seedIdx];
            int minPlateauScore = (seedScore * 88) / 100;

            int qHead = 0, qTail = 0;
            queue[qTail++] = seedIdx;
            suppressed[seedIdx] = true;

            double sumX = 0, sumY = 0, sumW = 0;

            while (qHead < qTail)
            {
                int cur = queue[qHead++];
                int cx = cur % ww;
                int cy = cur / ww;
                int w = saddleMap[cur];

                sumX += cx * (double)w;
                sumY += cy * (double)w;
                sumW += w;

                if (qTail >= queue.Length - 4) continue;

                TryEnqueue(cx - 1, cy);
                TryEnqueue(cx + 1, cy);
                TryEnqueue(cx, cy - 1);
                TryEnqueue(cx, cy + 1);
            }

            if (sumW <= 0) continue;

            double centX = sumX / sumW;
            double centY = sumY / sumW;
            result.Add(new JunctionPeak(centX, centY, seedScore, seedSign));

            // Подавляем окрестность вокруг найденного центроида
            int minY = Math.Max(0, (int)Math.Round(centY) - boxRadius);
            int maxY = Math.Min(wh - 1, (int)Math.Round(centY) + boxRadius);
            int minX = Math.Max(0, (int)Math.Round(centX) - boxRadius);
            int maxX = Math.Min(wh - 1, (int)Math.Round(centX) + boxRadius);
            for (int ny = minY; ny <= maxY; ny++)
            {
                int nrow = ny * ww;
                for (int nx = minX; nx <= maxX; nx++)
                    suppressed[nrow + nx] = true;
            }

            void TryEnqueue(int nx, int ny)
            {
                if (nx < 0 || nx >= ww || ny < 0 || ny >= wh) return;
                if (Math.Abs(nx - sx) > boxRadius * 2 || Math.Abs(ny - sy) > boxRadius * 2) return;
                int nIdx = ny * ww + nx;
                if (suppressed[nIdx]) return;
                if (signMap[nIdx] != seedSign || saddleMap[nIdx] < minPlateauScore) return;
                suppressed[nIdx] = true;
                queue[qTail++] = nIdx;
            }
        }

        return result;
    }

    private static List<double> FindDominantSteps(List<JunctionPeak> peaks, int minStep, int maxStep)
    {
        double[] hist = new double[maxStep + 3];
        double[] sumStep = new double[maxStep + 3];
        double[] sumCount = new double[maxStep + 3];

        // Горизонтальные пары (сортировка по Y)
        var byY = peaks.OrderBy(p => p.Y).ThenBy(p => p.X).ToArray();
        for (int i = 0; i < byY.Length; i++)
        {
            var pi = byY[i];
            for (int j = i + 1; j < byY.Length; j++)
            {
                double dy = byY[j].Y - pi.Y;
                if (dy > 2.5) break;
                if (byY[j].Sign == pi.Sign) continue;

                double dx = Math.Abs(byY[j].X - pi.X);
                if (dx < minStep || dx > maxStep) continue;

                VoteStep(dx, hist, sumStep, sumCount, minStep, maxStep);
            }
        }

        // Вертикальные пары (сортировка по X)
        var byX = peaks.OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        for (int i = 0; i < byX.Length; i++)
        {
            var pi = byX[i];
            for (int j = i + 1; j < byX.Length; j++)
            {
                double dx = byX[j].X - pi.X;
                if (dx > 2.5) break;
                if (byX[j].Sign == pi.Sign) continue;

                double dy = Math.Abs(byX[j].Y - pi.Y);
                if (dy < minStep || dy > maxStep) continue;

                VoteStep(dy, hist, sumStep, sumCount, minStep, maxStep);
            }
        }

        var stepCandidates = new List<(double Step, double Votes)>();
        for (int s = minStep; s <= maxStep; s++)
        {
            double v = hist[s];
            if (v < 6.0) continue;
            if (v >= hist[s - 1] && v >= hist[s + 1])
            {
                double exactStep = sumCount[s] > 0 ? sumStep[s] / sumCount[s] : s;
                stepCandidates.Add((exactStep, v));
            }
        }

        stepCandidates.Sort((a, b) => b.Votes.CompareTo(a.Votes));

        var selected = new List<double>(8);
        foreach (var (step, _) in stepCandidates)
        {
            if (selected.All(existing => Math.Abs(existing - step) >= 2.5))
            {
                selected.Add(step);
                if (selected.Count >= 8) break;
            }
        }

        return selected;
    }

    private static void VoteStep(
        double dist, double[] hist, double[] sumStep, double[] sumCount, int minStep, int maxStep)
    {
        int bin = (int)Math.Round(dist);
        if (bin < minStep || bin > maxStep) return;

        hist[bin] += 1.0;
        sumStep[bin] += dist;
        sumCount[bin] += 1.0;

        if (bin - 1 >= minStep) hist[bin - 1] += 0.45;
        if (bin + 1 <= maxStep) hist[bin + 1] += 0.45;
    }

    private static List<LatticeHypothesis> BuildLatticeHypotheses(
        List<JunctionPeak> peaks, List<double> candidateSteps)
    {
        var byY = peaks.OrderBy(p => p.Y).ToArray();
        var results = new List<LatticeHypothesis>(16);
        var seenOrigins = new HashSet<long>();

        foreach (double step in candidateSteps)
        {
            double tol = Math.Clamp(step * 0.06, 2.0, 4.5);

            for (int i = 0; i < byY.Length; i++)
            {
                var p = byY[i];
                sbyte oppSign = (sbyte)-p.Sign;

                // Быстрый отсев одиночных шумовых точек
                bool hasRight = FindNearestPeak(byY, p.X + step, p.Y, oppSign, tol) >= 0;
                bool hasDown = FindNearestPeak(byY, p.X, p.Y + step, oppSign, tol) >= 0;
                if (!hasRight && !hasDown) continue;

                // Расширение горизонтальной и вертикальной чередующихся цепочек
                var (xMinIdx, xMaxIdx, xStart, stepX) = ExpandChain(byY, p, step, horizontal: true, tol);
                var (yMinIdx, yMaxIdx, yStart, stepY) = ExpandChain(byY, p, step, horizontal: false, tol);

                int xLen = xMaxIdx - xMinIdx + 1;
                int yLen = yMaxIdx - yMinIdx + 1;
                if (xLen < 3 || yLen < 3) continue;

                int maxShiftX = Math.Max(0, 7 - xLen);
                int maxShiftY = Math.Max(0, 7 - yLen);

                for (int sx = 0; sx <= maxShiftX; sx++)
                {
                    for (int sy = 0; sy <= maxShiftY; sy++)
                    {
                        double x0 = xStart - sx * stepX;
                        double y0 = yStart - sy * stepY;

                        long key = QuantizeHypothesisKey(x0, y0, stepX);
                        if (!seenOrigins.Add(key)) continue;

                        // Полярность узла (0, 0) выводится из полярности исходного p, лежащего в (kP, mP)
                        int kP = sx - xMinIdx;
                        int mP = sy - yMinIdx;
                        sbyte baseSign = (((kP + mP) & 1) == 0) ? p.Sign : oppSign;

                        int matched = 0;
                        double strengthSum = 0;
                        for (int m = 0; m < 7; m++)
                        {
                            double ty = y0 + m * stepY;
                            for (int k = 0; k < 7; k++)
                            {
                                double tx = x0 + k * stepX;
                                sbyte expSign = (((k + m) & 1) == 0) ? baseSign : (sbyte)-baseSign;
                                int hit = FindNearestPeak(byY, tx, ty, expSign, tol);
                                if (hit >= 0)
                                {
                                    matched++;
                                    strengthSum += byY[hit].Score;
                                }
                            }
                        }

                        if (matched >= 16)
                        {
                            double score = matched * 1000.0 + strengthSum / Math.Max(1, matched);
                            results.Add(new LatticeHypothesis(x0, y0, stepX, stepY, baseSign, matched, score));
                        }
                    }
                }
            }
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Оставляем до 6 пространственно различных лучших гипотез
        var topDistinct = new List<LatticeHypothesis>(6);
        foreach (var h in results)
        {
            bool overlaps = topDistinct.Any(e =>
                Math.Abs(e.X0 - h.X0) < e.StepX * 0.5
                && Math.Abs(e.Y0 - h.Y0) < e.StepY * 0.5
                && Math.Abs(e.StepX - h.StepX) < 3.0);

            if (!overlaps)
            {
                topDistinct.Add(h);
                if (topDistinct.Count >= 6) break;
            }
        }

        return topDistinct;
    }

    private static (int MinIdx, int MaxIdx, double StartCoord, double RefinedStep) ExpandChain(
        JunctionPeak[] byY, JunctionPeak seed, double step, bool horizontal, double tol)
    {
        int minIdx = 0;
        int maxIdx = 0;
        double sumCoord = horizontal ? seed.X : seed.Y;
        int count = 1;

        var coords = new List<(int Idx, double Val)>(7) { (0, sumCoord) };

        // Вперёд (+1..+6)
        double curX = seed.X;
        double curY = seed.Y;
        for (int k = 1; k <= 6; k++)
        {
            sbyte expSign = (k & 1) == 0 ? seed.Sign : (sbyte)-seed.Sign;
            double tx = horizontal ? curX + step : curX;
            double ty = horizontal ? curY : curY + step;
            int hit = FindNearestPeak(byY, tx, ty, expSign, tol);
            if (hit < 0) break;

            maxIdx = k;
            curX = byY[hit].X;
            curY = byY[hit].Y;
            coords.Add((k, horizontal ? curX : curY));
            count++;
        }

        // Назад (-1..-6) с ограничением общей длины 7
        curX = seed.X;
        curY = seed.Y;
        for (int k = -1; k >= -6 && (maxIdx - k + 1) <= 7; k--)
        {
            sbyte expSign = ((k & 1) == 0) ? seed.Sign : (sbyte)-seed.Sign;
            double tx = horizontal ? curX - step : curX;
            double ty = horizontal ? curY : curY - step;
            int hit = FindNearestPeak(byY, tx, ty, expSign, tol);
            if (hit < 0) break;

            minIdx = k;
            curX = byY[hit].X;
            curY = byY[hit].Y;
            coords.Add((k, horizontal ? curX : curY));
            count++;
        }

        double refinedStep = step;
        double startCoord = horizontal ? seed.X + minIdx * step : seed.Y + minIdx * step;

        if (count >= 2 && TryFitLine1D(coords, out double intercept, out double slope))
        {
            if (Math.Abs(slope - step) <= tol)
            {
                refinedStep = slope;
                startCoord = intercept + minIdx * slope;
            }
        }

        return (minIdx, maxIdx, startCoord, refinedStep);
    }

    private static int FindNearestPeak(
        JunctionPeak[] byY, double tx, double ty, sbyte expectedSign, double tol)
    {
        double minY = ty - tol;
        double maxY = ty + tol;

        int lo = 0, hi = byY.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (byY[mid].Y < minY) lo = mid + 1;
            else hi = mid;
        }

        int bestIdx = -1;
        double bestDistSq = tol * tol;

        for (int i = lo; i < byY.Length && byY[i].Y <= maxY; i++)
        {
            if (byY[i].Sign != expectedSign) continue;
            double dx = byY[i].X - tx;
            double dy = byY[i].Y - ty;
            double dSq = dx * dx + dy * dy;
            if (dSq <= bestDistSq)
            {
                bestDistSq = dSq;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    private static (BoardRect Rect, int RefinedCorners, int ParityMatches, int OuterRingMatches, double Score)?
        RefineAndVerifyCandidate(
            byte[] pixels, int stride, int width, int height, LatticeHypothesis hyp, int ds)
    {
        double initX0 = hyp.X0 * ds;
        double initY0 = hyp.Y0 * ds;
        double initStepX = hyp.StepX * ds;
        double initStepY = hyp.StepY * ds;

        int dFull = 4;
        int searchRad = Math.Clamp(ds * 3 + 2, 5, 10);

        var xSamples = new List<(int Idx, double Val)>(49);
        var ySamples = new List<(int Idx, double Val)>(49);
        int distinctColsMask = 0;
        int distinctRowsMask = 0;
        int refinedCorners = 0;

        for (int m = 0; m < 7; m++)
        {
            int py = (int)Math.Round(initY0 + m * initStepY);
            for (int k = 0; k < 7; k++)
            {
                int px = (int)Math.Round(initX0 + k * initStepX);
                sbyte expSign = (((k + m) & 1) == 0) ? hyp.BaseSign : (sbyte)-hyp.BaseSign;

                if (TryRefineSingleCorner(pixels, stride, width, height, px, py, dFull, searchRad, expSign,
                        out double rx, out double ry))
                {
                    xSamples.Add((k, rx));
                    ySamples.Add((m, ry));
                    distinctColsMask |= 1 << k;
                    distinctRowsMask |= 1 << m;
                    refinedCorners++;
                }
            }
        }

        if (refinedCorners < 14
            || System.Numerics.BitOperations.PopCount((uint)distinctColsMask) < 4
            || System.Numerics.BitOperations.PopCount((uint)distinctRowsMask) < 4)
        {
            return null;
        }

        if (!TryFitLineRobust(xSamples, out double x0Ref, out double stepXRef)
            || !TryFitLineRobust(ySamples, out double y0Ref, out double stepYRef))
        {
            return null;
        }

        double maxStep = Math.Max(stepXRef, stepYRef);
        if (maxStep < MinBoardSizePx / 8.0) return null;
        if (Math.Abs(stepXRef - stepYRef) / maxStep > 0.05) return null;

        int left = (int)Math.Round(x0Ref - stepXRef);
        int top = (int)Math.Round(y0Ref - stepYRef);
        int right = (int)Math.Round(x0Ref + 7.0 * stepXRef);
        int bottom = (int)Math.Round(y0Ref + 7.0 * stepYRef);

        if (left < -4 || top < -4 || right > width + 4 || bottom > height + 4)
            return null;

        left = Math.Clamp(left, 0, width - 1);
        top = Math.Clamp(top, 0, height - 1);
        right = Math.Clamp(right, left + 1, width);
        bottom = Math.Clamp(bottom, top + 1, height);

        var rect = new BoardRect(left, top, right, bottom);
        if (rect.Width < MinBoardSizePx || rect.Height < MinBoardSizePx)
            return null;

        double aspect = (double)rect.Width / rect.Height;
        if (aspect < 0.95 || aspect > 1.05)
            return null;

        if (!Verify64SquareParity(pixels, stride, rect, out int totalParity, out int outerRingParity))
            return null;

        double finalScore = totalParity * 25.0
            + outerRingParity * 15.0
            + refinedCorners * 12.0
            + Math.Sqrt((double)rect.Width * rect.Height) * 0.2;

        return (rect, refinedCorners, totalParity, outerRingParity, finalScore);
    }

    private static bool TryRefineSingleCorner(
        byte[] pixels, int stride, int width, int height,
        int centerX, int centerY, int dFull, int searchRad, sbyte expectedSign,
        out double refinedX, out double refinedY)
    {
        refinedX = 0;
        refinedY = 0;

        int minX = Math.Max(dFull, centerX - searchRad);
        int maxX = Math.Min(width - 1 - dFull, centerX + searchRad);
        int minY = Math.Max(dFull, centerY - searchRad);
        int maxY = Math.Min(height - 1 - dFull, centerY + searchRad);
        if (minX > maxX || minY > maxY) return false;

        int bestScore = 0;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int sc = ComputeSaddleScore(pixels, stride, x, y, dFull, out sbyte sign);
                if (sign == expectedSign && sc > bestScore)
                    bestScore = sc;
            }
        }

        if (bestScore < MinSaddleScore) return false;

        int plateauMin = (bestScore * 90) / 100;
        double sumX = 0, sumY = 0, sumW = 0;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int sc = ComputeSaddleScore(pixels, stride, x, y, dFull, out sbyte sign);
                if (sign == expectedSign && sc >= plateauMin)
                {
                    sumX += x * (double)sc;
                    sumY += y * (double)sc;
                    sumW += sc;
                }
            }
        }

        if (sumW <= 0) return false;
        refinedX = sumX / sumW;
        refinedY = sumY / sumW;
        return true;
    }

    private static bool TryFitLineRobust(List<(int Idx, double Val)> samples, out double intercept, out double slope)
    {
        if (!TryFitLine1D(samples, out intercept, out slope))
            return false;

        // 1 раунд отсечения выбросов (IRLS), чтобы 1-2 задетых фигурой стыка не сдвигали границы
        var inliers = new List<(int Idx, double Val)>(samples.Count);
        foreach (var s in samples)
        {
            double pred = intercept + s.Idx * slope;
            if (Math.Abs(s.Val - pred) <= 2.5)
                inliers.Add(s);
        }

        if (inliers.Count >= 10 && TryFitLine1D(inliers, out double rIntercept, out double rSlope))
        {
            intercept = rIntercept;
            slope = rSlope;
        }

        return true;
    }

    private static bool TryFitLine1D(List<(int Idx, double Val)> samples, out double intercept, out double slope)
    {
        intercept = 0;
        slope = 0;
        int n = samples.Count;
        if (n < 2) return false;

        double sumX = 0, sumY = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += samples[i].Idx;
            sumY += samples[i].Val;
        }

        double meanX = sumX / n;
        double meanY = sumY / n;

        double varX = 0, covXY = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = samples[i].Idx - meanX;
            double dy = samples[i].Val - meanY;
            varX += dx * dx;
            covXY += dx * dy;
        }

        if (varX < 1e-6) return false;
        slope = covXY / varX;
        intercept = meanY - slope * meanX;
        return true;
    }

    private static bool Verify64SquareParity(
        byte[] pixels, int stride, BoardRect rect, out int totalMatches, out int outerRingMatches)
    {
        totalMatches = 0;
        outerRingMatches = 0;

        double cellW = rect.Width / 8.0;
        double cellH = rect.Height / 8.0;
        int marginX = Math.Max(2, (int)Math.Round(cellW * 0.08));
        int marginY = Math.Max(2, (int)Math.Round(cellH * 0.08));

        Span<int> tileR = stackalloc int[64];
        Span<int> tileG = stackalloc int[64];
        Span<int> tileB = stackalloc int[64];

        Span<int> evenR = stackalloc int[32];
        Span<int> evenG = stackalloc int[32];
        Span<int> evenB = stackalloc int[32];
        Span<int> oddR = stackalloc int[32];
        Span<int> oddG = stackalloc int[32];
        Span<int> oddB = stackalloc int[32];

        int evenIdx = 0, oddIdx = 0;

        for (int r = 0; r < 8; r++)
        {
            int yTop = rect.Top + (int)Math.Round(r * cellH) + marginY;
            int yBot = rect.Top + (int)Math.Round((r + 1) * cellH) - 1 - marginY;
            yTop = Math.Clamp(yTop, rect.Top, rect.Bottom - 1);
            yBot = Math.Clamp(yBot, rect.Top, rect.Bottom - 1);

            for (int c = 0; c < 8; c++)
            {
                int xLeft = rect.Left + (int)Math.Round(c * cellW) + marginX;
                int xRight = rect.Left + (int)Math.Round((c + 1) * cellW) - 1 - marginX;
                xLeft = Math.Clamp(xLeft, rect.Left, rect.Right - 1);
                xRight = Math.Clamp(xRight, rect.Left, rect.Right - 1);

                int p0 = yTop * stride + xLeft * 3;
                int p1 = yTop * stride + xRight * 3;
                int p2 = yBot * stride + xLeft * 3;
                int p3 = yBot * stride + xRight * 3;

                // Усечённое среднее 2 средних углов — устойчиво к буквам координат (a-h / 1-8) в одном углу
                int bVal = TrimmedMean4(pixels[p0], pixels[p1], pixels[p2], pixels[p3]);
                int gVal = TrimmedMean4(pixels[p0 + 1], pixels[p1 + 1], pixels[p2 + 1], pixels[p3 + 1]);
                int rVal = TrimmedMean4(pixels[p0 + 2], pixels[p1 + 2], pixels[p2 + 2], pixels[p3 + 2]);

                int idx = (r << 3) | c;
                tileR[idx] = rVal;
                tileG[idx] = gVal;
                tileB[idx] = bVal;

                if (((r + c) & 1) == 0)
                {
                    evenR[evenIdx] = rVal;
                    evenG[evenIdx] = gVal;
                    evenB[evenIdx] = bVal;
                    evenIdx++;
                }
                else
                {
                    oddR[oddIdx] = rVal;
                    oddG[oddIdx] = gVal;
                    oddB[oddIdx] = bVal;
                    oddIdx++;
                }
            }
        }

        evenR.Sort(); evenG.Sort(); evenB.Sort();
        oddR.Sort(); oddG.Sort(); oddB.Sort();

        int medEvenR = evenR[16], medEvenG = evenG[16], medEvenB = evenB[16];
        int medOddR = oddR[16], medOddG = oddG[16], medOddB = oddB[16];

        double dr = medEvenR - medOddR;
        double dg = medEvenG - medOddG;
        double db = medEvenB - medOddB;
        if (dr * dr + dg * dg + db * db < MinParityRgbDistance * MinParityRgbDistance)
            return false;

        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                int idx = (r << 3) | c;
                int dEven = DistSq(tileR[idx], tileG[idx], tileB[idx], medEvenR, medEvenG, medEvenB);
                int dOdd = DistSq(tileR[idx], tileG[idx], tileB[idx], medOddR, medOddG, medOddB);

                bool isEven = ((r + c) & 1) == 0;
                if (isEven ? dEven < dOdd : dOdd < dEven)
                {
                    totalMatches++;
                    if (r == 0 || r == 7 || c == 0 || c == 7)
                        outerRingMatches++;
                }
            }
        }

        return totalMatches >= MinTotalParityMatches && outerRingMatches >= MinOuterRingMatches;
    }

    private static int TrimmedMean4(int a, int b, int c, int d)
    {
        int min = Math.Min(Math.Min(a, b), Math.Min(c, d));
        int max = Math.Max(Math.Max(a, b), Math.Max(c, d));
        return (a + b + c + d - min - max) >> 1;
    }

    private static int DistSq(int r1, int g1, int b1, int r2, int g2, int b2)
    {
        int dr = r1 - r2;
        int dg = g1 - g2;
        int db = b1 - b2;
        return dr * dr + dg * dg + db * db;
    }

    private static long QuantizeHypothesisKey(double x0, double y0, double step)
    {
        long qx = (long)Math.Round(x0 / 4.0) & 0xFFFFF;
        long qy = (long)Math.Round(y0 / 4.0) & 0xFFFFF;
        long qs = (long)Math.Round(step / 2.0) & 0xFFFFF;
        return (qx << 40) | (qy << 20) | qs;
    }

    private readonly record struct JunctionPeak(double X, double Y, int Score, sbyte Sign);

    private readonly record struct LatticeHypothesis(
        double X0, double Y0, double StepX, double StepY, sbyte BaseSign, int MatchedCorners, double Score);
}
