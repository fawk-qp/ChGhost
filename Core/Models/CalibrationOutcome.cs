namespace ChGhost.Core.Models;

public sealed record CalibrationOutcome(
    bool IsStartingPosition,
    int BackRanksOccupied,
    int CenterOccupied,
    bool PlayAsBlack,
    double WhiteLuma,
    double BlackLuma)
{
    internal IReadOnlyDictionary<char, IReadOnlyList<PieceTemplate>>? Signatures { get; init; }
    internal double WhiteBodyR { get; init; } = 235.0;
    internal double WhiteBodyG { get; init; } = 235.0;
    internal double WhiteBodyB { get; init; } = 235.0;
    internal double BlackBodyR { get; init; } = 45.0;
    internal double BlackBodyG { get; init; } = 45.0;
    internal double BlackBodyB { get; init; } = 45.0;
}
