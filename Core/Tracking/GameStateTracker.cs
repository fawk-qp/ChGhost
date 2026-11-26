using System.Text;
using ChGhost.Core.Models;

namespace ChGhost.Core.Tracking;

public sealed class GameStateTracker
{
    private static readonly char[] PieceTypes =
        { 'P', 'N', 'B', 'R', 'Q', 'K', 'p', 'n', 'b', 'r', 'q', 'k' };

    private char[,]? _confirmedGrid;
    private string _lastRawFen = "";
    private string _lastStableFen = "";
    private readonly ChessHistoryTracker _history = new();

    public void Reset()
    {
        _confirmedGrid = null;
        _lastRawFen = "";
        _lastStableFen = "";
        _history.Reset();
    }

    public TrackingUpdate Update(BoardRecognitionResult recognition, bool forceCalculate, bool playAsBlack)
    {
        if (!forceCalculate && recognition.RawFen != _lastRawFen)
        {
            _lastRawFen = recognition.RawFen;
            return new TrackingUpdate
            {
                Grid = (char[,])recognition.RawGrid.Clone(),
                StableFen = "",
                CastlingRights = "-",
                IsDebounced = true
            };
        }

        _lastRawFen = recognition.RawFen;
        var tracked = StabilizeGridWithTracking(recognition.RawGrid);
        char[,] stableGrid = tracked.Grid;

        for (int c = 0; c < 8; c++)
        {
            if (stableGrid[0, c] == 'P')
                stableGrid[0, c] = BestPieceFromScores(recognition.ClassScores, 0, c, isWhite: true, allowPawn: false, allowKing: true);
            if (stableGrid[0, c] == 'p')
                stableGrid[0, c] = BestPieceFromScores(recognition.ClassScores, 0, c, isWhite: false, allowPawn: false, allowKing: true);
            if (stableGrid[7, c] == 'P')
                stableGrid[7, c] = BestPieceFromScores(recognition.ClassScores, 7, c, isWhite: true, allowPawn: false, allowKing: true);
            if (stableGrid[7, c] == 'p')
                stableGrid[7, c] = BestPieceFromScores(recognition.ClassScores, 7, c, isWhite: false, allowPawn: false, allowKing: true);
        }

        EnforceKingConstraint(stableGrid, recognition.ClassScores, isWhite: true, tracked.LockedWhiteKing);
        EnforceKingConstraint(stableGrid, recognition.ClassScores, isWhite: false, tracked.LockedBlackKing);
        EnforcePawnCap(stableGrid, recognition.ClassScores, isWhite: true);
        EnforcePawnCap(stableGrid, recognition.ClassScores, isWhite: false);

        int whiteKings = 0;
        int blackKings = 0;
        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                if (stableGrid[r, c] == 'K') whiteKings++;
                if (stableGrid[r, c] == 'k') blackKings++;
            }
        }

        string stableFen = GridToFen(stableGrid);
        string castlingRights = DetectCastlingRights(stableGrid);
        if (whiteKings != 1 || blackKings != 1)
        {
            return new TrackingUpdate
            {
                Grid = stableGrid,
                StableFen = stableFen,
                CastlingRights = castlingRights,
                IsValid = false,
                WhiteKings = whiteKings,
                BlackKings = blackKings
            };
        }

        ChessHistorySnapshot history = _history.Update(stableGrid);

        if (!forceCalculate && stableFen == _lastStableFen)
        {
            return new TrackingUpdate
            {
                Grid = stableGrid,
                StableFen = stableFen,
                CastlingRights = history.CastlingFen,
                EnPassantTarget = history.EnPassantTarget,
                HalfmoveClock = history.HalfmoveClock,
                FullmoveNumber = history.FullmoveNumber,
                IsUnchanged = true,
                WhiteKings = whiteKings,
                BlackKings = blackKings
            };
        }

        bool isMyTurn = true;
        if (!forceCalculate && _confirmedGrid != null)
        {
            bool opponentMoved = false;
            bool iMoved = false;

            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    char oldCell = _confirmedGrid[r, c];
                    char newCell = stableGrid[r, c];
                    if (oldCell != newCell && newCell != '1')
                    {
                        bool isWhitePiece = char.IsUpper(newCell);
                        bool isMyPiece = playAsBlack ? !isWhitePiece : isWhitePiece;
                        if (isMyPiece) iMoved = true;
                        else opponentMoved = true;
                    }
                }
            }

            if (iMoved && !opponentMoved)
            {
                _lastStableFen = stableFen;
                _confirmedGrid = stableGrid;
                return new TrackingUpdate
                {
                    Grid = stableGrid,
                    StableFen = stableFen,
                    CastlingRights = history.CastlingFen,
                    EnPassantTarget = history.EnPassantTarget,
                    HalfmoveClock = history.HalfmoveClock,
                    FullmoveNumber = history.FullmoveNumber,
                    IsWaitingForOpponent = true,
                    WhiteKings = whiteKings,
                    BlackKings = blackKings
                };
            }

            isMyTurn = opponentMoved || (!iMoved && !opponentMoved);
        }

        _lastStableFen = stableFen;
        _confirmedGrid = stableGrid;
        return new TrackingUpdate
        {
            Grid = stableGrid,
            StableFen = stableFen,
            CastlingRights = history.CastlingFen,
            EnPassantTarget = history.EnPassantTarget,
            HalfmoveClock = history.HalfmoveClock,
            FullmoveNumber = history.FullmoveNumber,
            ShouldCalculateMove = isMyTurn,
            WhiteKings = whiteKings,
            BlackKings = blackKings
        };
    }

    public static string GridToFen(char[,] grid)
    {
        StringBuilder sb = new();
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

    public static string DetectCastlingRights(char[,] grid)
    {
        StringBuilder rights = new();
        if (grid[7, 4] == 'K')
        {
            if (grid[7, 7] == 'R') rights.Append('K');
            if (grid[7, 0] == 'R') rights.Append('Q');
        }
        if (grid[0, 4] == 'k')
        {
            if (grid[0, 7] == 'r') rights.Append('k');
            if (grid[0, 0] == 'r') rights.Append('q');
        }
        return rights.Length > 0 ? rights.ToString() : "-";
    }

    private (char[,] Grid, Square? LockedWhiteKing, Square? LockedBlackKing) StabilizeGridWithTracking(char[,] rawGrid)
    {
        if (_confirmedGrid == null)
            return ((char[,])rawGrid.Clone(), null, null);

        char[,] stabilized = (char[,])rawGrid.Clone();
        Square? lockedWK = null;
        Square? lockedBK = null;
        var departed = new List<(int Row, int Col, char Piece)>();
        var arrived = new List<(int Row, int Col, char DetectedPiece, char PreviousPiece)>();
        var recoloredOccupied = new List<(int Row, int Col, char DetectedPiece, char PreviousPiece)>();

        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                char oldPiece = _confirmedGrid[r, c];
                char newPiece = rawGrid[r, c];

                if (oldPiece == newPiece)
                {
                    if (oldPiece == 'K') lockedWK = new Square(r, c);
                    if (oldPiece == 'k') lockedBK = new Square(r, c);
                    continue;
                }

                if (oldPiece != '1' && newPiece == '1')
                {
                    departed.Add((r, c, oldPiece));
                }
                else if (oldPiece == '1' && newPiece != '1')
                {
                    arrived.Add((r, c, newPiece, oldPiece));
                }
                else if (oldPiece != '1' && newPiece != '1')
                {
                    if (oldPiece == 'K' || oldPiece == 'k')
                    {
                        stabilized[r, c] = oldPiece;
                        if (oldPiece == 'K') lockedWK = new Square(r, c);
                        if (oldPiece == 'k') lockedBK = new Square(r, c);
                    }
                    else if (char.IsUpper(oldPiece) == char.IsUpper(newPiece))
                    {
                        stabilized[r, c] = oldPiece;
                        if (oldPiece != newPiece)
                            recoloredOccupied.Add((r, c, newPiece, oldPiece));
                    }
                    else
                    {
                        arrived.Add((r, c, newPiece, oldPiece));
                    }
                }
            }
        }

        if (departed.Count == 1 && arrived.Count == 0 && recoloredOccupied.Count == 1
            && char.IsUpper(departed[0].Piece) != char.IsUpper(recoloredOccupied[0].PreviousPiece))
        {
            var candidate = recoloredOccupied[0];
            arrived.Add((candidate.Row, candidate.Col, candidate.DetectedPiece, candidate.PreviousPiece));
        }

        if (departed.Count == 0 && arrived.Count > 0)
        {
            foreach (var item in arrived)
            {
                if (item.PreviousPiece != '1')
                    stabilized[item.Row, item.Col] = item.PreviousPiece;
            }
            return (stabilized, lockedWK, lockedBK);
        }

        if (departed.Count == 1 && arrived.Count == 1)
        {
            char movedPiece = departed[0].Piece;
            bool isPromotion = char.ToUpper(movedPiece) == 'P' && (arrived[0].Row == 0 || arrived[0].Row == 7);
            if (!isPromotion)
            {
                stabilized[arrived[0].Row, arrived[0].Col] = movedPiece;
                if (movedPiece == 'K') lockedWK = new Square(arrived[0].Row, arrived[0].Col);
                if (movedPiece == 'k') lockedBK = new Square(arrived[0].Row, arrived[0].Col);
            }
        }
        else if (departed.Count == 2 && arrived.Count == 1)
        {
            var attackingPawn = departed.FirstOrDefault(d =>
                char.IsUpper(d.Piece) == char.IsUpper(arrived[0].DetectedPiece));
            if (attackingPawn.Piece != '\0' && char.ToUpper(attackingPawn.Piece) == 'P')
                stabilized[arrived[0].Row, arrived[0].Col] = attackingPawn.Piece;
        }
        else if (departed.Count == 2 && arrived.Count == 2)
        {
            var kingDeparture = departed.FirstOrDefault(d => char.ToUpper(d.Piece) == 'K');
            var rookDeparture = departed.FirstOrDefault(d => char.ToUpper(d.Piece) == 'R');
            if (kingDeparture.Piece != '\0' && rookDeparture.Piece != '\0')
            {
                foreach (var item in arrived)
                {
                    if (item.Col == 2 || item.Col == 6)
                    {
                        stabilized[item.Row, item.Col] = kingDeparture.Piece;
                        if (kingDeparture.Piece == 'K') lockedWK = new Square(item.Row, item.Col);
                        if (kingDeparture.Piece == 'k') lockedBK = new Square(item.Row, item.Col);
                    }
                    else if (item.Col == 3 || item.Col == 5)
                    {
                        stabilized[item.Row, item.Col] = rookDeparture.Piece;
                    }
                }
            }
        }

        return (stabilized, lockedWK, lockedBK);
    }

    private static void EnforceKingConstraint(char[,] grid, double[,,] scores, bool isWhite, Square? lockedKing)
    {
        char kingChar = isWhite ? 'K' : 'k';
        char otherKingChar = isWhite ? 'k' : 'K';
        int offset = isWhite ? 1 : 7;

        if (lockedKing.HasValue)
        {
            int kingRow = lockedKing.Value.Row;
            int kingCol = lockedKing.Value.Col;
            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    if (r == kingRow && c == kingCol)
                        grid[r, c] = kingChar;
                    else if (grid[r, c] == kingChar)
                        grid[r, c] = BestPieceFromScores(scores, r, c, isWhite, allowPawn: r > 0 && r < 7, allowKing: false);
                }
            }
            return;
        }

        var kings = new List<(int Row, int Col, double Score, double Margin)>();
        var sameColor = new List<(int Row, int Col, double KingScore)>();
        var anyNonOtherKing = new List<(int Row, int Col, double KingScore)>();

        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                char piece = grid[r, c];
                if (piece == '1') continue;

                double kingScore = scores[r, c, offset + 5];
                if (piece != otherKingChar)
                    anyNonOtherKing.Add((r, c, kingScore));
                if (char.IsUpper(piece) != isWhite) continue;

                sameColor.Add((r, c, kingScore));
                if (piece == kingChar)
                {
                    double bestOther = -2.0;
                    for (int i = 0; i < 5; i++)
                    {
                        if ((r == 0 || r == 7) && i == 0) continue;
                        if (scores[r, c, offset + i] > bestOther) bestOther = scores[r, c, offset + i];
                    }
                    kings.Add((r, c, kingScore, kingScore - bestOther));
                }
            }
        }

        if (kings.Count == 0)
        {
            if (sameColor.Count > 0)
            {
                var bestSame = sameColor.OrderByDescending(x => x.KingScore).First();
                var bestAny = anyNonOtherKing.OrderByDescending(x => x.KingScore).First();
                var chosen = bestAny.KingScore > bestSame.KingScore + 0.15 ? bestAny : bestSame;
                grid[chosen.Row, chosen.Col] = kingChar;
            }
            else if (anyNonOtherKing.Count > 0)
            {
                var bestAny = anyNonOtherKing.OrderByDescending(x => x.KingScore).First();
                grid[bestAny.Row, bestAny.Col] = kingChar;
            }
        }
        else if (kings.Count > 1)
        {
            var ordered = kings.OrderByDescending(x => x.Margin).ThenByDescending(x => x.Score).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                int row = ordered[i].Row;
                int col = ordered[i].Col;
                grid[row, col] = BestPieceFromScores(scores, row, col, isWhite, allowPawn: row > 0 && row < 7, allowKing: false);
            }
        }
    }

    private static void EnforcePawnCap(char[,] grid, double[,,] scores, bool isWhite)
    {
        char pawnChar = isWhite ? 'P' : 'p';
        int offset = isWhite ? 1 : 7;
        var pawns = new List<(int Row, int Col, double Margin)>();

        for (int r = 1; r < 7; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                if (grid[r, c] != pawnChar) continue;
                double pawnScore = scores[r, c, offset];
                double bestNonPawn = -2.0;
                for (int i = 1; i < 5; i++)
                {
                    if (scores[r, c, offset + i] > bestNonPawn)
                        bestNonPawn = scores[r, c, offset + i];
                }
                pawns.Add((r, c, pawnScore - bestNonPawn));
            }
        }

        if (pawns.Count > 8)
        {
            var excess = pawns.OrderBy(x => x.Margin).Take(pawns.Count - 8);
            foreach (var item in excess)
                grid[item.Row, item.Col] = BestPieceFromScores(scores, item.Row, item.Col, isWhite, allowPawn: false, allowKing: false);
        }
    }

    private static char BestPieceFromScores(double[,,] scores, int row, int col, bool isWhite, bool allowPawn, bool allowKing)
    {
        int offset = isWhite ? 1 : 7;
        int bestIndex = -1;
        double bestValue = double.NegativeInfinity;
        for (int i = 0; i < 6; i++)
        {
            if (!allowPawn && i == 0) continue;
            if (!allowKing && i == 5) continue;
            if (scores[row, col, offset + i] > bestValue)
            {
                bestValue = scores[row, col, offset + i];
                bestIndex = i;
            }
        }
        if (bestIndex < 0) bestIndex = 4;
        return PieceTypes[(isWhite ? 0 : 6) + bestIndex];
    }
}
