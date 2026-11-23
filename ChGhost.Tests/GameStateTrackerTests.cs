using ChGhost.Core.Models;
using ChGhost.Core.Tracking;

namespace ChGhost.Tests;

public class GameStateTrackerTests
{
    [Fact]
    public void SingleUnstableFrameIsIgnoredUntilTwoIdenticalFramesArrive()
    {
        var tracker = new GameStateTracker();
        char[,] initial = Board((7, 4, 'K'), (0, 4, 'k'), (7, 0, 'R'));
        char[,] transient = Board((7, 4, 'K'), (0, 4, 'k'), (6, 0, 'R'));
        tracker.Update(Recognize(initial), forceCalculate: true, playAsBlack: false);

        TrackingUpdate firstTransient = tracker.Update(Recognize(transient), forceCalculate: false, playAsBlack: false);
        TrackingUpdate firstStable = tracker.Update(Recognize(initial), forceCalculate: false, playAsBlack: false);
        TrackingUpdate secondStable = tracker.Update(Recognize(initial), forceCalculate: false, playAsBlack: false);

        Assert.True(firstTransient.IsDebounced);
        Assert.True(firstStable.IsDebounced);
        Assert.True(secondStable.IsUnchanged);
        Assert.Equal('R', secondStable.Grid[7, 0]);
        Assert.Equal('1', secondStable.Grid[6, 0]);
    }

    [Fact]
    public void OpponentMoveRequestsCalculationAndOwnMoveWaits()
    {
        char[,] initial = Board((7, 4, 'K'), (0, 4, 'k'), (0, 0, 'r'), (7, 0, 'R'));
        char[,] opponentMove = Board((7, 4, 'K'), (0, 4, 'k'), (1, 0, 'r'), (7, 0, 'R'));
        var opponentTracker = new GameStateTracker();
        opponentTracker.Update(Recognize(initial), forceCalculate: true, playAsBlack: false);
        opponentTracker.Update(Recognize(opponentMove), forceCalculate: false, playAsBlack: false);
        TrackingUpdate opponentUpdate = opponentTracker.Update(Recognize(opponentMove), forceCalculate: false, playAsBlack: false);
        Assert.True(opponentUpdate.ShouldCalculateMove);

        char[,] ownMove = Board((7, 4, 'K'), (0, 4, 'k'), (0, 0, 'r'), (6, 0, 'R'));
        var ownTracker = new GameStateTracker();
        ownTracker.Update(Recognize(initial), forceCalculate: true, playAsBlack: false);
        ownTracker.Update(Recognize(ownMove), forceCalculate: false, playAsBlack: false);
        TrackingUpdate ownUpdate = ownTracker.Update(Recognize(ownMove), forceCalculate: false, playAsBlack: false);
        Assert.True(ownUpdate.IsWaitingForOpponent);
        Assert.False(ownUpdate.ShouldCalculateMove);
    }

    [Fact]
    public void CaptureKeepsMovingPiecesColorWhenRawRecognitionInvertsIt()
    {
        char[,] initial = Board((7, 4, 'K'), (0, 4, 'k'), (4, 3, 'B'), (3, 4, 'p'));
        char[,] rawCapture = Board((7, 4, 'K'), (0, 4, 'k'), (3, 4, 'b'));
        var tracker = new GameStateTracker();
        tracker.Update(Recognize(initial), forceCalculate: true, playAsBlack: false);

        TrackingUpdate update = tracker.Update(Recognize(rawCapture), forceCalculate: false, playAsBlack: false);
        Assert.True(update.IsDebounced);
        update = tracker.Update(Recognize(rawCapture), forceCalculate: false, playAsBlack: false);

        Assert.Equal('B', update.Grid[3, 4]);
        Assert.Equal('1', update.Grid[4, 3]);
    }

    [Theory]
    [InlineData(false, 6, 5, "KQkq", "kq")]
    [InlineData(true, 2, 3, "KQkq", "kq")]
    public void CastlingStabilizesKingAndRookAndUpdatesFenRights(
        bool longCastle, int kingColumn, int rookColumn, string initialRights, string resultingRights)
    {
        char[,] initial = Board((7, 4, 'K'), (7, 0, 'R'), (7, 7, 'R'), (0, 4, 'k'), (0, 0, 'r'), (0, 7, 'r'));
        var tracker = new GameStateTracker();
        TrackingUpdate initialUpdate = tracker.Update(Recognize(initial), forceCalculate: true, playAsBlack: false);
        Assert.Equal(initialRights, initialUpdate.CastlingRights);

        char[,] castled = longCastle
            ? Board((7, 2, 'K'), (7, 3, 'R'), (7, 7, 'R'), (0, 4, 'k'), (0, 0, 'r'), (0, 7, 'r'))
            : Board((7, 6, 'K'), (7, 5, 'R'), (7, 0, 'R'), (0, 4, 'k'), (0, 0, 'r'), (0, 7, 'r'));
        TrackingUpdate update = tracker.Update(Recognize(castled), forceCalculate: true, playAsBlack: false);

        Assert.Equal('K', update.Grid[7, kingColumn]);
        Assert.Equal('R', update.Grid[7, rookColumn]);
        Assert.Equal(resultingRights, update.CastlingRights);
        string fullFen = $"{update.StableFen} w {update.CastlingRights} - 0 1";
        Assert.EndsWith($" w {resultingRights} - 0 1", fullFen);
    }

    [Fact]
    public void EnPassantCaptureKeepsAttackingPawn()
    {
        char[,] initial = Board((7, 4, 'K'), (0, 4, 'k'), (3, 4, 'P'), (3, 3, 'p'));
        char[,] rawAfterCapture = Board((7, 4, 'K'), (0, 4, 'k'), (2, 3, 'P'));
        var tracker = new GameStateTracker();
        tracker.Update(Recognize(initial), forceCalculate: true, playAsBlack: false);

        TrackingUpdate update = tracker.Update(Recognize(rawAfterCapture), forceCalculate: true, playAsBlack: false);

        Assert.Equal('P', update.Grid[2, 3]);
        Assert.Equal('1', update.Grid[3, 3]);
        Assert.Equal('1', update.Grid[3, 4]);
    }

    [Theory]
    [InlineData('P', 1, 0, 0, 0, 'Q')]
    [InlineData('p', 6, 7, 7, 7, 'q')]
    public void PromotionKeepsRecognizedPromotedPiece(char pawn, int fromRow, int fromCol, int toRow, int toCol, char promoted)
    {
        char[,] initial = Board((7, 4, 'K'), (0, 4, 'k'), (fromRow, fromCol, pawn));
        char[,] rawPromotion = Board((7, 4, 'K'), (0, 4, 'k'), (toRow, toCol, promoted));
        var tracker = new GameStateTracker();
        tracker.Update(Recognize(initial), forceCalculate: true, playAsBlack: false);

        TrackingUpdate update = tracker.Update(Recognize(rawPromotion), forceCalculate: true, playAsBlack: false);

        Assert.Equal(promoted, update.Grid[toRow, toCol]);
        Assert.Equal('1', update.Grid[fromRow, fromCol]);
    }

    [Fact]
    public void KingAndPawnLimitsSelectBestRemainingClassScores()
    {
        char[,] raw = Board(
            (7, 4, 'K'), (6, 4, 'K'), (0, 4, 'k'),
            (1, 0, 'P'), (1, 1, 'P'), (1, 2, 'P'), (1, 3, 'P'), (1, 4, 'P'),
            (1, 5, 'P'), (1, 6, 'P'), (1, 7, 'P'), (2, 0, 'P'));
        BoardRecognitionResult result = Recognize(raw);
        result.ClassScores[6, 4, (int)PieceClass.WhiteKing] = 0.6;
        result.ClassScores[6, 4, (int)PieceClass.WhiteQueen] = 0.8;
        result.ClassScores[7, 4, (int)PieceClass.WhiteKing] = 0.95;
        result.ClassScores[7, 4, (int)PieceClass.WhiteQueen] = 0.2;
        result.ClassScores[2, 0, (int)PieceClass.WhitePawn] = 0.1;
        result.ClassScores[2, 0, (int)PieceClass.WhiteQueen] = 0.9;

        TrackingUpdate update = new GameStateTracker().Update(result, forceCalculate: true, playAsBlack: false);

        Assert.Equal('Q', update.Grid[6, 4]);
        Assert.Equal('K', update.Grid[7, 4]);
        Assert.Equal('Q', update.Grid[2, 0]);
        Assert.Equal(8, Count(update.Grid, 'P'));
        Assert.True(update.IsValid);
    }

    [Fact]
    public void OccupiedPieceIsNotRecoloredWhenNoPieceDeparted()
    {
        char[,] initial = Board((7, 4, 'K'), (0, 4, 'k'), (4, 3, 'B'));
        char[,] inverted = Board((7, 4, 'K'), (0, 4, 'k'), (4, 3, 'b'));
        var tracker = new GameStateTracker();
        tracker.Update(Recognize(initial), forceCalculate: true, playAsBlack: false);

        TrackingUpdate update = tracker.Update(Recognize(inverted), forceCalculate: false, playAsBlack: false);
        Assert.True(update.IsDebounced);
        update = tracker.Update(Recognize(inverted), forceCalculate: false, playAsBlack: false);

        Assert.Equal('B', update.Grid[4, 3]);
        Assert.True(update.IsUnchanged);
    }

    private static BoardRecognitionResult Recognize(char[,] grid)
    {
        var scores = new double[8, 8, 13];
        var confidences = new double[8, 8];
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                for (int pieceClass = 0; pieceClass < 13; pieceClass++)
                    scores[row, col, pieceClass] = -2.0;
                scores[row, col, (int)ToPieceClass(grid[row, col])] = 1.0;
                confidences[row, col] = grid[row, col] == '1' ? 0.0 : 1.0;
            }
        }
        return new BoardRecognitionResult(grid, scores, confidences, GameStateTracker.GridToFen(grid));
    }

    private static char[,] Board(params (int Row, int Col, char Piece)[] pieces)
    {
        var grid = new char[8, 8];
        for (int row = 0; row < 8; row++)
            for (int col = 0; col < 8; col++)
                grid[row, col] = '1';
        foreach (var piece in pieces)
            grid[piece.Row, piece.Col] = piece.Piece;
        return grid;
    }

    private static int Count(char[,] grid, char piece)
    {
        int count = 0;
        foreach (char cell in grid)
            if (cell == piece) count++;
        return count;
    }

    private static PieceClass ToPieceClass(char piece) => piece switch
    {
        'P' => PieceClass.WhitePawn, 'N' => PieceClass.WhiteKnight, 'B' => PieceClass.WhiteBishop,
        'R' => PieceClass.WhiteRook, 'Q' => PieceClass.WhiteQueen, 'K' => PieceClass.WhiteKing,
        'p' => PieceClass.BlackPawn, 'n' => PieceClass.BlackKnight, 'b' => PieceClass.BlackBishop,
        'r' => PieceClass.BlackRook, 'q' => PieceClass.BlackQueen, 'k' => PieceClass.BlackKing,
        _ => PieceClass.Empty
    };
}
