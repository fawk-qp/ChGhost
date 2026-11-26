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

    [Fact]
    public void TryDetectPlayAsBlackStrict_MisorientedFlippedFen_ReturnsTrue()
    {
        char[,] grid = CreateGridFromRows(
            "RNBKQBNR",
            "PPP1PPPP",
            "11111111",
            "111P1111",
            "11111111",
            "11111111",
            "pppppppp",
            "rnbkqbnr");

        bool? detected = OnnxBoardRecognizer.TryDetectPlayAsBlackStrict(grid, currentPlayAsBlack: false);

        Assert.Equal(true, detected);
    }

    [Fact]
    public void TryDetectPlayAsBlackStrict_WhiteStartingPosition_ReturnsFalse()
    {
        char[,] grid = CreateStartingGrid();

        bool? detected = OnnxBoardRecognizer.TryDetectPlayAsBlackStrict(grid, currentPlayAsBlack: false);

        Assert.Equal(false, detected);
    }

    [Fact]
    public void TryDetectPlayAsBlackStrict_LateEndgame_ReturnsNull()
    {
        char[,] grid = CreateGridFromRows(
            "R1111111",
            "111K1111",
            "11111111",
            "11111111",
            "11111111",
            "11111111",
            "1111k111",
            "1111111r");

        bool? detected = OnnxBoardRecognizer.TryDetectPlayAsBlackStrict(grid, currentPlayAsBlack: false);

        Assert.Null(detected);
    }

    [Fact]
    public void ValidateModelMetadata_RejectsNonFloatInputElementType()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OnnxBoardRecognizer.ValidateModelMetadata(
                new[] { 1, 3, 64, 64 }, typeof(double), "input",
                new[] { 1, 13 }, typeof(float), "output",
                out _, out _));
    }

    [Fact]
    public void ValidateModelMetadata_RejectsNonFloatOutputElementType()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OnnxBoardRecognizer.ValidateModelMetadata(
                new[] { 1, 3, 64, 64 }, typeof(float), "input",
                new[] { 1, 13 }, typeof(double), "output",
                out _, out _));
    }

    [Fact]
    public void ValidateModelMetadata_RejectsFixedOutputBatchGreaterThanOne()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OnnxBoardRecognizer.ValidateModelMetadata(
                new[] { -1, 3, 64, 64 }, typeof(float), "input",
                new[] { 4, 13 }, typeof(float), "output",
                out _, out _));
    }

    [Fact]
    public void ValidateModelMetadata_OutputBatchOneForcesFixedBatchOne()
    {
        OnnxBoardRecognizer.ValidateModelMetadata(
            new[] { -1, 3, 48, 48 }, typeof(float), "input",
            new[] { 1, 13 }, typeof(float), "output",
            out int tileSize, out bool fixedBatchOne);

        Assert.Equal(48, tileSize);
        Assert.True(fixedBatchOne);
    }

    private static char[,] CreateGridFromRows(params string[] rows)
    {
        Assert.Equal(8, rows.Length);
        var grid = new char[8, 8];
        for (int r = 0; r < 8; r++)
        {
            Assert.Equal(8, rows[r].Length);
            for (int c = 0; c < 8; c++)
                grid[r, c] = rows[r][c];
        }
        return grid;
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
