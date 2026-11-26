using ChGhost.Core.Tracking;

namespace ChGhost.Core.Models;

public sealed class TrackingUpdate
{
    public required char[,] Grid { get; init; }
    public required string StableFen { get; init; }
    public required string CastlingRights { get; init; }
    public string EnPassantTarget { get; init; } = "-";
    public int HalfmoveClock { get; init; }
    public int FullmoveNumber { get; init; } = 1;
    public bool IsDebounced { get; init; }
    public bool IsValid { get; init; } = true;
    public bool IsUnchanged { get; init; }
    public bool IsWaitingForOpponent { get; init; }
    public bool ShouldCalculateMove { get; init; }
    public int WhiteKings { get; init; }
    public int BlackKings { get; init; }
    public HistoryConfidence Confidence { get; init; } = HistoryConfidence.Unknown;
}
