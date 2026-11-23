namespace ChGhost.Core.Models;

public sealed class TrackingUpdate
{
    public required char[,] Grid { get; init; }
    public required string StableFen { get; init; }
    public required string CastlingRights { get; init; }
    public bool IsDebounced { get; init; }
    public bool IsValid { get; init; } = true;
    public bool IsUnchanged { get; init; }
    public bool IsWaitingForOpponent { get; init; }
    public bool ShouldCalculateMove { get; init; }
    public int WhiteKings { get; init; }
    public int BlackKings { get; init; }
}
