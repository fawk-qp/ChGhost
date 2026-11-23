namespace ChGhost.Core.Models;

public class SavedConfig
{
    public int TopLeftX { get; set; }
    public int TopLeftY { get; set; }
    public int BottomRightX { get; set; }
    public int BottomRightY { get; set; }
    public double WhiteBodyR { get; set; } = 235.0;
    public double WhiteBodyG { get; set; } = 235.0;
    public double WhiteBodyB { get; set; } = 235.0;
    public double BlackBodyR { get; set; } = 45.0;
    public double BlackBodyG { get; set; } = 45.0;
    public double BlackBodyB { get; set; } = 45.0;
    public Dictionary<char, List<PieceTemplate>> Signatures { get; set; } = new();
}
