using ChGhost.Core.Tracking;

namespace ChGhost.Tests;

public class ChessHistoryTrackerTests
{
    [Theory]
    [InlineData(7, 4, 6, 4, "kq")]
    [InlineData(7, 7, 6, 7, "Qkq")]
    [InlineData(7, 0, 6, 0, "Kkq")]
    [InlineData(0, 4, 1, 4, "KQ")]
    [InlineData(0, 7, 1, 7, "KQq")]
    [InlineData(0, 0, 1, 0, "KQk")]
    public void CastlingRightStaysLostAfterPieceReturnsToHomeSquare(
        int fromRow, int fromCol, int toRow, int toCol, string expectedRights)
    {
        var tracker = new ChessHistoryTracker();
        char[,] initial = CastlingGrid();
        ChessHistorySnapshot first = tracker.Update(initial);
        Assert.Equal("KQkq", first.CastlingFen);

        char[,] departed = Move(initial, fromRow, fromCol, toRow, toCol);
        ChessHistorySnapshot departure = tracker.Update(departed);
        Assert.Equal(expectedRights, departure.CastlingFen);

        char[,] returned = Move(departed, toRow, toCol, fromRow, fromCol);
        ChessHistorySnapshot returnedSnapshot = tracker.Update(returned);
        Assert.Equal(expectedRights, returnedSnapshot.CastlingFen);
    }

    [Fact]
    public void KnightShuttlesBackToStartDoNotResetMoveCounters()
    {
        var tracker = new ChessHistoryTracker();
        char[,] start = StartGrid();
        tracker.Update(start);

        char[,] afterNf3 = Move(start, 7, 6, 5, 5);
        ChessHistorySnapshot whiteFirst = tracker.Update(afterNf3);
        Assert.Equal(1, whiteFirst.HalfmoveClock);
        Assert.Equal(1, whiteFirst.FullmoveNumber);

        char[,] afterNf6 = Move(afterNf3, 0, 6, 2, 5);
        ChessHistorySnapshot blackFirst = tracker.Update(afterNf6);
        Assert.Equal(2, blackFirst.HalfmoveClock);
        Assert.Equal(2, blackFirst.FullmoveNumber);

        char[,] afterNg1 = Move(afterNf6, 5, 5, 7, 6);
        ChessHistorySnapshot whiteSecond = tracker.Update(afterNg1);
        Assert.Equal(3, whiteSecond.HalfmoveClock);
        Assert.Equal(2, whiteSecond.FullmoveNumber);

        char[,] afterNg8 = Move(afterNg1, 2, 5, 0, 6);
        ChessHistorySnapshot returnedToStart = tracker.Update(afterNg8);
        Assert.Equal("KQkq", returnedToStart.CastlingFen);
        Assert.Equal("-", returnedToStart.EnPassantTarget);
        Assert.Equal(4, returnedToStart.HalfmoveClock);
        Assert.Equal(3, returnedToStart.FullmoveNumber);
        Assert.Equal(HistoryConfidence.Tracked, returnedToStart.Confidence);
    }

    [Fact]
    public void PawnRollbackToStartResetsHistory()
    {
        var tracker = new ChessHistoryTracker();
        char[,] start = StartGrid();
        tracker.Update(start);

        char[,] afterE4 = Move(start, 6, 4, 4, 4);
        ChessHistorySnapshot doublePush = tracker.Update(afterE4);
        Assert.Equal("e3", doublePush.EnPassantTarget);

        char[,] rolledBack = Move(afterE4, 4, 4, 6, 4);
        ChessHistorySnapshot reset = tracker.Update(rolledBack);
        Assert.Equal("KQkq", reset.CastlingFen);
        Assert.Equal("-", reset.EnPassantTarget);
        Assert.Equal(0, reset.HalfmoveClock);
        Assert.Equal(1, reset.FullmoveNumber);
        Assert.Equal(HistoryConfidence.Tracked, reset.Confidence);
    }

    [Fact]
    public void StartPositionAfterMiddlegameResetsHistory()
    {
        var tracker = new ChessHistoryTracker();
        char[,] middlegame = Grid(
            (7, 4, 'K'), (0, 4, 'k'),
            (7, 0, 'R'), (0, 0, 'r'),
            (3, 3, 'P'), (4, 4, 'p'));
        ChessHistorySnapshot unknown = tracker.Update(middlegame);
        Assert.Equal(HistoryConfidence.Unknown, unknown.Confidence);

        ChessHistorySnapshot reset = tracker.Update(StartGrid());
        Assert.Equal("KQkq", reset.CastlingFen);
        Assert.Equal("-", reset.EnPassantTarget);
        Assert.Equal(0, reset.HalfmoveClock);
        Assert.Equal(1, reset.FullmoveNumber);
        Assert.Equal(HistoryConfidence.Tracked, reset.Confidence);
    }

    [Fact]
    public void DoublePawnPushSetsEnPassantUntilNextMove()
    {
        var tracker = new ChessHistoryTracker();
        char[,] start = StartGrid();
        tracker.Update(start);

        char[,] afterE4 = Move(start, 6, 4, 4, 4);
        ChessHistorySnapshot push = tracker.Update(afterE4);
        Assert.Equal("e3", push.EnPassantTarget);
        Assert.Equal(0, push.HalfmoveClock);
        Assert.Equal(1, push.FullmoveNumber);

        char[,] afterNf6 = Move(afterE4, 0, 6, 2, 5);
        ChessHistorySnapshot nextMove = tracker.Update(afterNf6);
        Assert.Equal("-", nextMove.EnPassantTarget);
        Assert.Equal(1, nextMove.HalfmoveClock);
        Assert.Equal(2, nextMove.FullmoveNumber);
        Assert.Equal(HistoryConfidence.Tracked, nextMove.Confidence);
    }

    [Fact]
    public void SkippedHalfmovesMarkHistoryUnknown()
    {
        var tracker = new ChessHistoryTracker();
        char[,] start = StartGrid();
        tracker.Update(start);

        char[,] afterE4 = Move(start, 6, 4, 4, 4);
        tracker.Update(afterE4);

        char[,] skipped = Move(Move(afterE4, 0, 6, 2, 5), 7, 6, 5, 5);
        ChessHistorySnapshot snapshot = tracker.Update(skipped);
        Assert.Equal("-", snapshot.EnPassantTarget);
        Assert.Equal(0, snapshot.HalfmoveClock);
        Assert.Equal(HistoryConfidence.Unknown, snapshot.Confidence);
    }

    [Fact]
    public void RealEnPassantCaptureAfterDoublePushStaysTracked()
    {
        var tracker = new ChessHistoryTracker();
        char[,] start = StartGrid();
        tracker.Update(start);

        char[,] afterE4 = Move(start, 6, 4, 4, 4);
        tracker.Update(afterE4);

        char[,] afterNf6 = Move(afterE4, 0, 6, 2, 5);
        tracker.Update(afterNf6);

        char[,] afterE5 = Move(afterNf6, 4, 4, 3, 4);
        tracker.Update(afterE5);

        char[,] afterD5 = Move(afterE5, 1, 3, 3, 3);
        ChessHistorySnapshot doublePush = tracker.Update(afterD5);
        Assert.Equal("d6", doublePush.EnPassantTarget);

        char[,] afterExd6 = EnPassantCapture(afterD5, 3, 4, 2, 3, 3, 3);
        ChessHistorySnapshot capture = tracker.Update(afterExd6);
        Assert.Equal(HistoryConfidence.Tracked, capture.Confidence);
        Assert.Equal("-", capture.EnPassantTarget);
        Assert.Equal(0, capture.HalfmoveClock);
        Assert.Equal(3, capture.FullmoveNumber);
    }

    [Fact]
    public void EnPassantPatternWithoutEnPassantTargetMarksUnknown()
    {
        var tracker = new ChessHistoryTracker();
        char[,] start = StartGrid();
        tracker.Update(start);

        char[,] afterE4 = Move(start, 6, 4, 4, 4);
        tracker.Update(afterE4);

        char[,] afterNf6 = Move(afterE4, 0, 6, 2, 5);
        tracker.Update(afterNf6);

        char[,] afterE5 = Move(afterNf6, 4, 4, 3, 4);
        tracker.Update(afterE5);

        char[,] afterD5 = Move(afterE5, 1, 3, 3, 3);
        tracker.Update(afterD5);

        char[,] afterNf3 = Move(afterD5, 7, 6, 5, 5);
        ChessHistorySnapshot quiet = tracker.Update(afterNf3);
        Assert.Equal("-", quiet.EnPassantTarget);

        char[,] fakeEnPassant = EnPassantCapture(afterNf3, 3, 4, 2, 3, 3, 3);
        ChessHistorySnapshot snapshot = tracker.Update(fakeEnPassant);
        Assert.Equal(HistoryConfidence.Unknown, snapshot.Confidence);
        Assert.Equal("-", snapshot.EnPassantTarget);
    }

    [Fact]
    public void TwoMovesBySameSideMarkHistoryUnknownAndClearEnPassant()
    {
        var tracker = new ChessHistoryTracker();
        char[,] start = StartGrid();
        tracker.Update(start);

        char[,] afterE4 = Move(start, 6, 4, 4, 4);
        ChessHistorySnapshot push = tracker.Update(afterE4);
        Assert.Equal("e3", push.EnPassantTarget);

        char[,] afterNf3 = Move(afterE4, 7, 6, 5, 5);
        ChessHistorySnapshot snapshot = tracker.Update(afterNf3);
        Assert.Equal(HistoryConfidence.Unknown, snapshot.Confidence);
        Assert.Equal("-", snapshot.EnPassantTarget);
    }

    [Fact]
    public void CastlingIsTrackedAsSingleHalfmove()
    {
        var tracker = new ChessHistoryTracker();
        char[,] g = StartGrid();
        tracker.Update(g);

        g = Move(g, 6, 4, 4, 4); tracker.Update(g);   // 1. e4
        g = Move(g, 1, 4, 3, 4); tracker.Update(g);   // 1... e5
        g = Move(g, 7, 6, 5, 5); tracker.Update(g);   // 2. Nf3
        g = Move(g, 0, 1, 2, 2); tracker.Update(g);   // 2... Nc6
        g = Move(g, 7, 5, 4, 2); tracker.Update(g);   // 3. Bc4
        g = Move(g, 0, 5, 3, 2); tracker.Update(g);   // 3... Bc5
        g = Move(Move(g, 7, 4, 7, 6), 7, 7, 7, 5);   // 4. O-O
        ChessHistorySnapshot castle = tracker.Update(g);
        Assert.Equal(HistoryConfidence.Tracked, castle.Confidence);
        Assert.Equal("-", castle.EnPassantTarget);
        Assert.Equal("kq", castle.CastlingFen);
        Assert.Equal(5, castle.HalfmoveClock);
        Assert.Equal(4, castle.FullmoveNumber);
    }

    [Fact]
    public void PawnPromotionIsTrackedAsSingleHalfmove()
    {
        var tracker = new ChessHistoryTracker();
        char[,] g = StartGrid();
        tracker.Update(g);

        g = Move(g, 7, 1, 5, 2); tracker.Update(g);   // Nb1-c3
        g = Move(g, 1, 0, 3, 0);                      // a7-a5
        ChessHistorySnapshot doublePush = tracker.Update(g);
        Assert.Equal("a6", doublePush.EnPassantTarget);
        g = Move(g, 5, 2, 4, 4); tracker.Update(g);   // Nc3-e4
        g = Move(g, 3, 0, 4, 0); tracker.Update(g);   // a5-a4
        g = Move(g, 4, 4, 5, 2); tracker.Update(g);   // Ne4-c3
        g = Move(g, 4, 0, 5, 0); tracker.Update(g);   // a4-a3
        g = Move(g, 5, 2, 4, 4); tracker.Update(g);   // Nc3-e4
        g = Move(g, 5, 0, 6, 1); tracker.Update(g);   // axb2
        g = Move(g, 4, 4, 5, 2); tracker.Update(g);   // Ne4-c3
        g = Promote(g, 6, 1, 7, 1, 'q');              // b2-b1=q
        ChessHistorySnapshot promo = tracker.Update(g);
        Assert.Equal(HistoryConfidence.Tracked, promo.Confidence);
        Assert.Equal("-", promo.EnPassantTarget);
        Assert.Equal(0, promo.HalfmoveClock);
        Assert.Equal(6, promo.FullmoveNumber);
    }

    private static char[,] CastlingGrid() => Grid(
        (7, 4, 'K'), (7, 0, 'R'), (7, 7, 'R'),
        (0, 4, 'k'), (0, 0, 'r'), (0, 7, 'r'));

    private static char[,] StartGrid()
    {
        char[,] grid = EmptyGrid();
        string[] rows =
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
        for (int row = 0; row < 8; row++)
            for (int col = 0; col < 8; col++)
                grid[row, col] = rows[row][col];
        return grid;
    }

    private static char[,] Grid(params (int Row, int Col, char Piece)[] pieces)
    {
        char[,] grid = EmptyGrid();
        foreach ((int row, int col, char piece) in pieces)
            grid[row, col] = piece;
        return grid;
    }

    private static char[,] EmptyGrid()
    {
        var grid = new char[8, 8];
        for (int row = 0; row < 8; row++)
            for (int col = 0; col < 8; col++)
                grid[row, col] = '1';
        return grid;
    }

    private static char[,] Move(char[,] grid, int fromRow, int fromCol, int toRow, int toCol)
    {
        char[,] copy = (char[,])grid.Clone();
        char piece = copy[fromRow, fromCol];
        copy[fromRow, fromCol] = '1';
        copy[toRow, toCol] = piece;
        return copy;
    }

    private static char[,] EnPassantCapture(char[,] grid, int fromRow, int fromCol, int toRow, int toCol, int capturedRow, int capturedCol)
    {
        char[,] copy = (char[,])grid.Clone();
        char piece = copy[fromRow, fromCol];
        copy[fromRow, fromCol] = '1';
        copy[toRow, toCol] = piece;
        copy[capturedRow, capturedCol] = '1';
        return copy;
    }

    private static char[,] Promote(char[,] grid, int fromRow, int fromCol, int toRow, int toCol, char piece)
    {
        char[,] copy = (char[,])grid.Clone();
        copy[fromRow, fromCol] = '1';
        copy[toRow, toCol] = piece;
        return copy;
    }
}
