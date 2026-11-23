namespace ChGhost.Core.Models;

public sealed record CalibrationOutcome(
    bool IsStartingPosition,
    int BackRanksOccupied,
    int CenterOccupied,
    bool PlayAsBlack,
    double WhiteLuma,
    double BlackLuma);
