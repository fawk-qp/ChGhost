namespace ChGhost.Core.Models;

public sealed class BoardRecognitionResult
{
    public BoardRecognitionResult(char[,] rawGrid, double[,,] classScores, double[,] confidences, string rawFen)
    {
        RawGrid = rawGrid;
        ClassScores = classScores;
        Confidences = confidences;
        RawFen = rawFen;
    }

    public char[,] RawGrid { get; }
    public double[,,] ClassScores { get; }
    public double[,] Confidences { get; }
    public string RawFen { get; }
}
