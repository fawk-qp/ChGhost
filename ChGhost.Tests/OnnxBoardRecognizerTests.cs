using ChGhost.Core.Vision;

namespace ChGhost.Tests;

public class OnnxBoardRecognizerTests
{
    [Fact]
    public void MissingModelThrowsFileNotFoundException()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "pieces.onnx");

        Assert.Throws<FileNotFoundException>(() => new OnnxBoardRecognizer(missingPath));
    }

    [Fact]
    public void TryDetectPlayAsBlack_ReturnsFalse_ForWhiteStartingPosition()
    {
        char[,] grid = CreateStartingGrid();

        bool? detected = OnnxBoardRecognizer.TryDetectPlayAsBlack(grid, currentPlayAsBlack: false);

        Assert.Equal(false, detected);
    }

    [Fact]
    public void TryDetectPlayAsBlack_ReturnsTrue_ForBlackStartingPosition()
    {
        char[,] grid = CreateStartingGrid();

        bool? detected = OnnxBoardRecognizer.TryDetectPlayAsBlack(grid, currentPlayAsBlack: true);

        Assert.Equal(true, detected);
    }

    [Fact]
    public void TryDetectPlayAsBlack_ReturnsTrue_WhenWhiteAssumptionIsMisoriented()
    {
        char[,] rotated = Rotate180(CreateStartingGrid());

        bool? detected = OnnxBoardRecognizer.TryDetectPlayAsBlack(rotated, currentPlayAsBlack: false);

        Assert.Equal(true, detected);
    }

    private static char[,] CreateStartingGrid()
    {
        const string blackBackRank = "rnbqkbnr";
        const string whiteBackRank = "RNBQKBNR";
        var grid = new char[8, 8];

        for (int c = 0; c < 8; c++)
        {
            grid[0, c] = blackBackRank[c];
            grid[1, c] = 'p';
            grid[6, c] = 'P';
            grid[7, c] = whiteBackRank[c];
        }

        for (int r = 2; r <= 5; r++)
        for (int c = 0; c < 8; c++)
            grid[r, c] = '1';

        return grid;
    }

    private static char[,] Rotate180(char[,] source)
    {
        var result = new char[8, 8];
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
            result[r, c] = source[7 - r, 7 - c];
        return result;
    }
}
