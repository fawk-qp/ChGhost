namespace ChGhost.Core.Models;

public class PieceTemplate
{
    public double[] EdgePatch { get; set; } = Array.Empty<double>();
    public double BodyR { get; set; }
    public double BodyG { get; set; }
    public double BodyB { get; set; }
    public double PieceLuma { get; set; }
    public double RawContrast { get; set; }
    public int CenterEdgePixels { get; set; }
    public bool IsWhite { get; set; }
}
