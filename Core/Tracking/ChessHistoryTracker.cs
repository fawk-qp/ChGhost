namespace ChGhost.Core.Tracking;

public enum HistoryConfidence
{
    Tracked,
    Unknown
}

[Flags]
public enum CastlingMask
{
    None = 0,
    WhiteKingSide = 1,
    WhiteQueenSide = 2,
    BlackKingSide = 4,
    BlackQueenSide = 8,
    All = WhiteKingSide | WhiteQueenSide | BlackKingSide | BlackQueenSide
}

public sealed record ChessHistorySnapshot(
    string CastlingFen,
    string EnPassantTarget,
    int HalfmoveClock,
    int FullmoveNumber,
    HistoryConfidence Confidence);

public sealed class ChessHistoryTracker
{
    private static readonly string[] StartRows =
    {
        "rnbqkbnr",
        "pppppppp",
        "11111111",
        "11111111",
        "11111111",
        "11111111",
        "PPPPPPPP",
        "RNBQKBNR"
    };

    private char[,]? _prevGrid;
    private CastlingMask _castling = CastlingMask.All;
    private string _enPassant = "-";
    private int _halfmoveClock;
    private int _fullmoveNumber = 1;
    private HistoryConfidence _confidence = HistoryConfidence.Tracked;
    private bool? _lastMoverIsWhite;

    public void Reset()
    {
        _prevGrid = null;
        _castling = CastlingMask.All;
        _enPassant = "-";
        _halfmoveClock = 0;
        _fullmoveNumber = 1;
        _confidence = HistoryConfidence.Tracked;
        _lastMoverIsWhite = null;
    }

    public ChessHistorySnapshot Update(char[,] stableGrid)
    {
        if (stableGrid.GetLength(0) != 8 || stableGrid.GetLength(1) != 8)
            throw new ArgumentException("Grid must be 8x8.", nameof(stableGrid));

        if (_prevGrid is null)
        {
            _prevGrid = CloneGrid(stableGrid);
            _castling = CastlingMask.All;
            SyncCastlingWithBoard(stableGrid);
            _enPassant = "-";
            _halfmoveClock = 0;
            _fullmoveNumber = 1;
            _confidence = IsStartingPosition(stableGrid)
                ? HistoryConfidence.Tracked
                : HistoryConfidence.Unknown;
            _lastMoverIsWhite = null;
            return BuildSnapshot();
        }

        if (GridsEqual(_prevGrid, stableGrid))
            return BuildSnapshot();

        var changes = CollectChanges(_prevGrid, stableGrid);
        int prevCount = CountPieces(_prevGrid);
        int currCount = CountPieces(stableGrid);

        // 1. Проверка возврата к стартовой расстановке:
        // Единственные легальные ходы, способные вернуть доску в точную стартовую позицию —
        // манёвры коней при всех 32 фигурах (например, 1. Nf3 Nf6 2. Ng1 Ng8).
        // Любой другой переход в стартовую расстановку — достоверно новая партия.
        if (IsStartingPosition(stableGrid))
        {
            if (IsReversibleKnightReturnToStart(changes, prevCount, currCount, out int plies, out bool includesBlackMove))
            {
                _enPassant = "-";
                _lastMoverIsWhite = !includesBlackMove;
                if (_confidence == HistoryConfidence.Tracked)
                {
                    _halfmoveClock += plies;
                    if (includesBlackMove) _fullmoveNumber++;
                }
            }
            else
            {
                _castling = CastlingMask.All;
                _enPassant = "-";
                _halfmoveClock = 0;
                _fullmoveNumber = 1;
                _confidence = HistoryConfidence.Tracked;
                _lastMoverIsWhite = null;
            }

            _prevGrid = CloneGrid(stableGrid);
            return BuildSnapshot();
        }

        // 2. Обновление прав рокировки по подтверждённому стабильному кадру
        SyncCastlingWithBoard(stableGrid);
        foreach (var ch in changes)
        {
            // Если фигура покинула или была снята с ключевой клетки рокировки
            ClearCastlingBySquare(ch.R, ch.C);
        }

        // 3. Классификация перехода (1 полуход: обычный/взятие/превращение, en passant, рокировка)
        if (TryClassifySingleMove(changes, stableGrid, out bool isPawnMove, out bool isCapture, out bool blackMoved, out string epSquare))
        {
            if (!HasSkippedHalfmove(!blackMoved))
            {
                _enPassant = epSquare;
                if (_confidence == HistoryConfidence.Tracked)
                {
                    _halfmoveClock = (isPawnMove || isCapture) ? 0 : _halfmoveClock + 1;
                    if (blackMoved) _fullmoveNumber++;
                }
            }
        }
        else if (TryClassifyEnPassantCapture(changes, _enPassant, out bool epBlackMoved))
        {
            if (!HasSkippedHalfmove(!epBlackMoved))
            {
                _enPassant = "-";
                if (_confidence == HistoryConfidence.Tracked)
                {
                    _halfmoveClock = 0;
                    if (epBlackMoved) _fullmoveNumber++;
                }
            }
        }
        else if (TryClassifyCastling(changes, out bool castleBlackMoved))
        {
            if (!HasSkippedHalfmove(!castleBlackMoved))
            {
                _enPassant = "-";
                if (_confidence == HistoryConfidence.Tracked)
                {
                    _halfmoveClock++;
                    if (castleBlackMoved) _fullmoveNumber++;
                }
            }
        }
        else
        {
            // Пропущено несколько полуходов (например, быстрый премув) — не придумываем счётчики
            _enPassant = "-";
            _halfmoveClock = 0;
            _confidence = HistoryConfidence.Unknown;
            _lastMoverIsWhite = null;
        }

        _prevGrid = CloneGrid(stableGrid);
        return BuildSnapshot();
    }

    private static bool TryClassifySingleMove(
        List<CellChange> changes,
        char[,] currGrid,
        out bool isPawnMove,
        out bool isCapture,
        out bool blackMoved,
        out string epSquare)
    {
        isPawnMove = false;
        isCapture = false;
        blackMoved = false;
        epSquare = "-";

        if (changes.Count != 2) return false;

        CellChange from, to;
        if (changes[0].After == '1' && changes[0].Before != '1' && changes[1].After != '1')
        {
            from = changes[0];
            to = changes[1];
        }
        else if (changes[1].After == '1' && changes[1].Before != '1' && changes[0].After != '1')
        {
            from = changes[1];
            to = changes[0];
        }
        else
        {
            return false;
        }

        char mover = from.Before;
        char arrived = to.After;
        bool moverIsWhite = char.IsUpper(mover);
        if (char.IsUpper(arrived) != moverIsWhite) return false;

        // Обычный ход или превращение пешки на 1-й / 8-й горизонтали
        bool isPromotion = char.ToUpperInvariant(mover) == 'P'
            && ((moverIsWhite && to.R == 0) || (!moverIsWhite && to.R == 7))
            && char.ToUpperInvariant(arrived) is 'Q' or 'R' or 'B' or 'N';

        if (mover != arrived && !isPromotion)
            return false;

        // Нельзя «взять» собственную фигуру
        if (to.Before != '1' && char.IsUpper(to.Before) == moverIsWhite)
            return false;

        isPawnMove = char.ToUpperInvariant(mover) == 'P';
        isCapture = to.Before != '1';
        blackMoved = !moverIsWhite;

        // Двойной ход пешки со стартовой горизонтали -> en passant
        if (isPawnMove && !isCapture && from.C == to.C && Math.Abs(to.R - from.R) == 2)
        {
            bool validDouble = (mover == 'P' && from.R == 6 && to.R == 4)
                || (mover == 'p' && from.R == 1 && to.R == 3);
            int midR = (from.R + to.R) >> 1;
            if (validDouble && currGrid[midR, from.C] == '1')
            {
                epSquare = ToAlgebraic(midR, from.C);
            }
        }

        return true;
    }

    private bool HasSkippedHalfmove(bool moverIsWhite)
    {
        if (_lastMoverIsWhite.HasValue && _lastMoverIsWhite.Value == moverIsWhite)
        {
            // Между подтверждёнными кадрами пропущен ответный полуход
            _confidence = HistoryConfidence.Unknown;
            _enPassant = "-";
            _halfmoveClock = 0;
            return true;
        }
        _lastMoverIsWhite = moverIsWhite;
        return false;
    }

    private static bool TryClassifyEnPassantCapture(List<CellChange> changes, string enPassant, out bool blackMoved)
    {
        blackMoved = false;
        if (changes.Count != 3 || enPassant == "-") return false;

        // Ровно 1 клетка получила пешку из пустоты, 2 клетки опустели (своя пешка + взятая вражеская)
        int arrivedIdx = -1;
        for (int i = 0; i < 3; i++)
        {
            if (changes[i].Before == '1' && char.ToUpperInvariant(changes[i].After) == 'P')
            {
                if (arrivedIdx >= 0) return false;
                arrivedIdx = i;
            }
        }
        if (arrivedIdx < 0) return false;

        var dest = changes[arrivedIdx];
        if (!string.Equals(ToAlgebraic(dest.R, dest.C), enPassant, StringComparison.Ordinal))
            return false;
        bool isWhite = dest.After == 'P';
        char enemyPawn = isWhite ? 'p' : 'P';
        int expectedFromR = isWhite ? dest.R + 1 : dest.R - 1;

        bool sawMoverFrom = false;
        bool sawCapturedPawn = false;
        for (int i = 0; i < 3; i++)
        {
            if (i == arrivedIdx) continue;
            var c = changes[i];
            if (c.After != '1') return false;
            if (c.Before == dest.After && c.R == expectedFromR && Math.Abs(c.C - dest.C) == 1)
                sawMoverFrom = true;
            else if (c.Before == enemyPawn && c.R == expectedFromR && c.C == dest.C)
                sawCapturedPawn = true;
        }

        if (!sawMoverFrom || !sawCapturedPawn) return false;
        blackMoved = !isWhite;
        return true;
    }

    private static bool TryClassifyCastling(List<CellChange> changes, out bool blackMoved)
    {
        blackMoved = false;
        if (changes.Count != 4) return false;

        bool CheckPattern(int rank, char king, char rook, int kToC, int rFromC, int rToC)
        {
            bool kFrom = false, kTo = false, rFrom = false, rTo = false;
            foreach (var c in changes)
            {
                if (c.R != rank) return false;
                if (c.C == 4 && c.Before == king && c.After == '1') kFrom = true;
                else if (c.C == kToC && c.Before == '1' && c.After == king) kTo = true;
                else if (c.C == rFromC && c.Before == rook && c.After == '1') rFrom = true;
                else if (c.C == rToC && c.Before == '1' && c.After == rook) rTo = true;
            }
            return kFrom && kTo && rFrom && rTo;
        }

        if (CheckPattern(7, 'K', 'R', 6, 7, 5) || CheckPattern(7, 'K', 'R', 2, 0, 3))
        {
            blackMoved = false;
            return true;
        }
        if (CheckPattern(0, 'k', 'r', 6, 7, 5) || CheckPattern(0, 'k', 'r', 2, 0, 3))
        {
            blackMoved = true;
            return true;
        }
        return false;
    }

    private static bool IsReversibleKnightReturnToStart(
        List<CellChange> changes, int prevCount, int currCount, out int plies, out bool includesBlackMove)
    {
        plies = 0;
        includesBlackMove = false;
        if (prevCount != 32 || currCount != 32) return false;
        if (changes.Count is not (2 or 4)) return false;

        foreach (var c in changes)
        {
            bool validBefore = c.Before is '1' or 'N' or 'n';
            bool validAfter = c.After is '1' or 'N' or 'n';
            if (!validBefore || !validAfter) return false;
        }

        var wFrom = changes.Where(c => c.Before == 'N' && c.After == '1').ToList();
        var wTo = changes.Where(c => c.Before == '1' && c.After == 'N').ToList();
        var bFrom = changes.Where(c => c.Before == 'n' && c.After == '1').ToList();
        var bTo = changes.Where(c => c.Before == '1' && c.After == 'n').ToList();

        if (wFrom.Count != wTo.Count || bFrom.Count != bTo.Count) return false;
        if (wFrom.Count + bFrom.Count != changes.Count / 2) return false;

        static bool IsKnightStep(CellChange a, CellChange b)
        {
            int dr = Math.Abs(a.R - b.R);
            int dc = Math.Abs(a.C - b.C);
            return (dr == 2 && dc == 1) || (dr == 1 && dc == 2);
        }

        if (wFrom.Count == 1 && !IsKnightStep(wFrom[0], wTo[0])) return false;
        if (bFrom.Count == 1 && !IsKnightStep(bFrom[0], bTo[0])) return false;

        plies = wFrom.Count + bFrom.Count;
        includesBlackMove = bFrom.Count > 0;
        return plies > 0;
    }

    private void SyncCastlingWithBoard(char[,] g)
    {
        if (g[7, 4] != 'K') _castling &= ~(CastlingMask.WhiteKingSide | CastlingMask.WhiteQueenSide);
        if (g[7, 7] != 'R') _castling &= ~CastlingMask.WhiteKingSide;
        if (g[7, 0] != 'R') _castling &= ~CastlingMask.WhiteQueenSide;
        if (g[0, 4] != 'k') _castling &= ~(CastlingMask.BlackKingSide | CastlingMask.BlackQueenSide);
        if (g[0, 7] != 'r') _castling &= ~CastlingMask.BlackKingSide;
        if (g[0, 0] != 'r') _castling &= ~CastlingMask.BlackQueenSide;
    }

    private void ClearCastlingBySquare(int r, int c)
    {
        if (r == 7 && c == 4) _castling &= ~(CastlingMask.WhiteKingSide | CastlingMask.WhiteQueenSide);
        else if (r == 7 && c == 7) _castling &= ~CastlingMask.WhiteKingSide;
        else if (r == 7 && c == 0) _castling &= ~CastlingMask.WhiteQueenSide;
        else if (r == 0 && c == 4) _castling &= ~(CastlingMask.BlackKingSide | CastlingMask.BlackQueenSide);
        else if (r == 0 && c == 7) _castling &= ~CastlingMask.BlackKingSide;
        else if (r == 0 && c == 0) _castling &= ~CastlingMask.BlackQueenSide;
    }

    private ChessHistorySnapshot BuildSnapshot()
    {
        string castling = _castling == CastlingMask.None
            ? "-"
            : string.Concat(
                (_castling & CastlingMask.WhiteKingSide) != 0 ? "K" : "",
                (_castling & CastlingMask.WhiteQueenSide) != 0 ? "Q" : "",
                (_castling & CastlingMask.BlackKingSide) != 0 ? "k" : "",
                (_castling & CastlingMask.BlackQueenSide) != 0 ? "q" : "");

        return new ChessHistorySnapshot(castling, _enPassant, _halfmoveClock, _fullmoveNumber, _confidence);
    }

    public static bool IsStartingPosition(char[,] g)
    {
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                if (g[r, c] != StartRows[r][c]) return false;
        return true;
    }

    private static List<CellChange> CollectChanges(char[,] prev, char[,] curr)
    {
        var list = new List<CellChange>(4);
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                if (prev[r, c] != curr[r, c])
                    list.Add(new CellChange(r, c, prev[r, c], curr[r, c]));
        return list;
    }

    private static bool GridsEqual(char[,] a, char[,] b)
    {
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                if (a[r, c] != b[r, c]) return false;
        return true;
    }

    private static int CountPieces(char[,] g)
    {
        int n = 0;
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                if (g[r, c] != '1') n++;
        return n;
    }

    private static char[,] CloneGrid(char[,] src)
    {
        var dst = new char[8, 8];
        Array.Copy(src, dst, 64);
        return dst;
    }

    private static string ToAlgebraic(int r, int c)
        => string.Create(2, (r, c), static (span, state) =>
        {
            span[0] = (char)('a' + state.c);
            span[1] = (char)('8' - state.r);
        });

    private readonly record struct CellChange(int R, int C, char Before, char After);
}
