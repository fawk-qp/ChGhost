namespace ChGhost.Core.Tracking;

public static class FenLegalityValidator
{
    private static readonly (int Dr, int Dc)[] KnightOffsets =
    {
        (-2, -1), (-2, 1), (-1, -2), (-1, 2),
        (1, -2), (1, 2), (2, -1), (2, 1)
    };

    private static readonly (int Dr, int Dc)[] OrthogonalDirs =
    {
        (-1, 0), (1, 0), (0, -1), (0, 1)
    };

    private static readonly (int Dr, int Dc)[] DiagonalDirs =
    {
        (-1, -1), (-1, 1), (1, -1), (1, 1)
    };

    public static (bool IsLegal, string? Reason) Validate(char[,] grid, bool whiteToMove)
    {
        if (grid.GetLength(0) != 8 || grid.GetLength(1) != 8)
            return (false, "Неверный размер сетки.");

        int wkCount = 0, bkCount = 0;
        int wkR = -1, wkC = -1, bkR = -1, bkC = -1;
        int wpCount = 0, bpCount = 0;
        int wTotal = 0, bTotal = 0;
        int wQ = 0, wR = 0, wB = 0, wN = 0;
        int bQ = 0, bR = 0, bB = 0, bN = 0;

        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                char p = grid[r, c];
                if (p == '1') continue;

                bool isWhite = char.IsUpper(p);
                if (isWhite) wTotal++; else bTotal++;

                switch (p)
                {
                    case 'K': wkCount++; wkR = r; wkC = c; break;
                    case 'k': bkCount++; bkR = r; bkC = c; break;
                    case 'P':
                        if (r == 0 || r == 7)
                            return (false, $"Белая пешка на недопустимой горизонтали (r={r}).");
                        wpCount++;
                        break;
                    case 'p':
                        if (r == 0 || r == 7)
                            return (false, $"Чёрная пешка на недопустимой горизонтали (r={r}).");
                        bpCount++;
                        break;
                    case 'Q': wQ++; break;
                    case 'q': bQ++; break;
                    case 'R': wR++; break;
                    case 'r': bR++; break;
                    case 'B': wB++; break;
                    case 'b': bB++; break;
                    case 'N': wN++; break;
                    case 'n': bN++; break;
                    default:
                        return (false, $"Недопустимый символ клетки: '{p}'.");
                }
            }
        }

        if (wkCount != 1 || bkCount != 1)
            return (false, $"Некорректное число королей (Б={wkCount}, Ч={bkCount}).");

        if (wpCount > 8 || bpCount > 8 || wTotal > 16 || bTotal > 16)
            return (false, $"Превышен лимит фигур (Б={wTotal}/пешек={wpCount}, Ч={bTotal}/пешек={bpCount}).");

        int extraWhite = Math.Max(0, wQ - 1) + Math.Max(0, wR - 2) + Math.Max(0, wB - 2) + Math.Max(0, wN - 2);
        int extraBlack = Math.Max(0, bQ - 1) + Math.Max(0, bR - 2) + Math.Max(0, bB - 2) + Math.Max(0, bN - 2);
        if (wpCount + extraWhite > 8)
            return (false, $"Превышен бюджет превращений белых (пешек={wpCount}, лишних фигур={extraWhite}).");
        if (bpCount + extraBlack > 8)
            return (false, $"Превышен бюджет превращений чёрных (пешек={bpCount}, лишних фигур={extraBlack}).");

        if (Math.Abs(wkR - bkR) <= 1 && Math.Abs(wkC - bkC) <= 1)
            return (false, "Короли находятся на соседних клетках.");

        if (whiteToMove)
        {
            if (IsSquareAttacked(grid, bkR, bkC, byWhite: true))
                return (false, "Нелегальная позиция: чёрный король под шахом при ходе белых.");
        }
        else
        {
            if (IsSquareAttacked(grid, wkR, wkC, byWhite: false))
                return (false, "Нелегальная позиция: белый король под шахом при ходе чёрных.");
        }

        return (true, null);
    }

    public static bool IsSquareAttacked(char[,] grid, int targetR, int targetC, bool byWhite)
    {
        char pawn = byWhite ? 'P' : 'p';
        int pawnSrcR = byWhite ? targetR + 1 : targetR - 1;
        if (pawnSrcR is >= 0 and < 8)
        {
            if (targetC - 1 >= 0 && grid[pawnSrcR, targetC - 1] == pawn) return true;
            if (targetC + 1 < 8 && grid[pawnSrcR, targetC + 1] == pawn) return true;
        }

        char knight = byWhite ? 'N' : 'n';
        foreach (var (dr, dc) in KnightOffsets)
        {
            int r = targetR + dr, c = targetC + dc;
            if (r is >= 0 and < 8 && c is >= 0 and < 8 && grid[r, c] == knight)
                return true;
        }

        char king = byWhite ? 'K' : 'k';
        for (int dr = -1; dr <= 1; dr++)
        {
            for (int dc = -1; dc <= 1; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                int r = targetR + dr, c = targetC + dc;
                if (r is >= 0 and < 8 && c is >= 0 and < 8 && grid[r, c] == king)
                    return true;
            }
        }

        char rook = byWhite ? 'R' : 'r';
        char queen = byWhite ? 'Q' : 'q';
        foreach (var (dr, dc) in OrthogonalDirs)
        {
            int r = targetR + dr, c = targetC + dc;
            while (r is >= 0 and < 8 && c is >= 0 and < 8)
            {
                char p = grid[r, c];
                if (p != '1')
                {
                    if (p == rook || p == queen) return true;
                    break;
                }
                r += dr;
                c += dc;
            }
        }

        char bishop = byWhite ? 'B' : 'b';
        foreach (var (dr, dc) in DiagonalDirs)
        {
            int r = targetR + dr, c = targetC + dc;
            while (r is >= 0 and < 8 && c is >= 0 and < 8)
            {
                char p = grid[r, c];
                if (p != '1')
                {
                    if (p == bishop || p == queen) return true;
                    break;
                }
                r += dr;
                c += dc;
            }
        }

        return false;
    }
}
