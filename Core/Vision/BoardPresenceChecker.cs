namespace ChGhost.Core.Vision;

public static class BoardPresenceChecker
{
    private const int TileSize = 64;
    private const int Margin = 4;
    private const double MinParityRgbDistance = 16.0;
    private const int MinMatchingSquares = 46;

    public static bool IsCheckerboardPresent(ReadOnlySpan<byte> bgr24Buffer, int stride)
    {
        if (stride < TileSize * 8 * 3 || bgr24Buffer.Length < stride * TileSize * 8)
            return false;

        Span<int> tileR = stackalloc int[64];
        Span<int> tileG = stackalloc int[64];
        Span<int> tileB = stackalloc int[64];

        Span<int> evenR = stackalloc int[32];
        Span<int> evenG = stackalloc int[32];
        Span<int> evenB = stackalloc int[32];
        Span<int> oddR = stackalloc int[32];
        Span<int> oddG = stackalloc int[32];
        Span<int> oddB = stackalloc int[32];

        int evenIdx = 0;
        int oddIdx = 0;

        for (int sr = 0; sr < 8; sr++)
        {
            int yTop = sr * TileSize + Margin;
            int yBot = (sr + 1) * TileSize - 1 - Margin;

            for (int sc = 0; sc < 8; sc++)
            {
                int xLeft = sc * TileSize + Margin;
                int xRight = (sc + 1) * TileSize - 1 - Margin;

                int p0 = yTop * stride + xLeft * 3;
                int p1 = yTop * stride + xRight * 3;
                int p2 = yBot * stride + xLeft * 3;
                int p3 = yBot * stride + xRight * 3;

                int b = TrimmedMean4(bgr24Buffer[p0], bgr24Buffer[p1], bgr24Buffer[p2], bgr24Buffer[p3]);
                int g = TrimmedMean4(bgr24Buffer[p0 + 1], bgr24Buffer[p1 + 1], bgr24Buffer[p2 + 1], bgr24Buffer[p3 + 1]);
                int r = TrimmedMean4(bgr24Buffer[p0 + 2], bgr24Buffer[p1 + 2], bgr24Buffer[p2 + 2], bgr24Buffer[p3 + 2]);

                int idx = (sr << 3) | sc;
                tileR[idx] = r;
                tileG[idx] = g;
                tileB[idx] = b;

                if (((sr + sc) & 1) == 0)
                {
                    evenR[evenIdx] = r;
                    evenG[evenIdx] = g;
                    evenB[evenIdx] = b;
                    evenIdx++;
                }
                else
                {
                    oddR[oddIdx] = r;
                    oddG[oddIdx] = g;
                    oddB[oddIdx] = b;
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
        double parityDistSq = dr * dr + dg * dg + db * db;

        if (parityDistSq < MinParityRgbDistance * MinParityRgbDistance)
            return false;

        int matching = 0;
        for (int sr = 0; sr < 8; sr++)
        {
            for (int sc = 0; sc < 8; sc++)
            {
                int idx = (sr << 3) | sc;
                int r = tileR[idx], g = tileG[idx], b = tileB[idx];

                int dEven = DistSq(r, g, b, medEvenR, medEvenG, medEvenB);
                int dOdd = DistSq(r, g, b, medOddR, medOddG, medOddB);

                bool isEven = ((sr + sc) & 1) == 0;
                if (isEven ? dEven < dOdd : dOdd < dEven)
                    matching++;
            }
        }

        return matching >= MinMatchingSquares;
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
}
