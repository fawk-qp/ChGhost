using ChGhost.Core.Tracking;

namespace ChGhost.Tests;

public class FenLegalityValidatorTests
{
    [Fact]
    public void StartingPosition_IsLegal()
    {
        char[,] grid = GridFromRows(
            "rnbqkbnr",
            "pppppppp",
            "11111111",
            "11111111",
            "11111111",
            "11111111",
            "PPPPPPPP",
            "RNBQKBNR");

        (bool isLegal, string? reason) = FenLegalityValidator.Validate(grid, whiteToMove: true);

        Assert.True(isLegal, reason);
    }

    [Fact]
    public void SpanishPosition_IsLegal()
    {
        char[,] grid = GridFromRows(
            "r1bqkbnr",
            "1ppp1ppp",
            "p1n11111",
            "1B11p111",
            "1111P111",
            "11111N11",
            "PPPP1PPP",
            "R1BQK1NR");

        (bool isLegal, string? reason) = FenLegalityValidator.Validate(grid, whiteToMove: true);

        Assert.True(isLegal, reason);
    }

    [Fact]
    public void WhitePawnOnBackRank_IsIllegal()
    {
        char[,] grid = EmptyGrid();
        grid[0, 7] = 'P';
        grid[0, 0] = 'k';
        grid[7, 7] = 'K';

        (bool isLegal, _) = FenLegalityValidator.Validate(grid, whiteToMove: true);

        Assert.False(isLegal);
    }

    [Fact]
    public void BlackPawnOnBackRank_IsIllegal()
    {
        char[,] grid = EmptyGrid();
        grid[7, 0] = 'p';
        grid[0, 4] = 'k';
        grid[7, 7] = 'K';

        (bool isLegal, _) = FenLegalityValidator.Validate(grid, whiteToMove: false);

        Assert.False(isLegal);
    }

    [Fact]
    public void AdjacentKings_AreIllegal()
    {
        char[,] grid = EmptyGrid();
        grid[7, 4] = 'K';
        grid[7, 3] = 'k';

        (bool isLegal, _) = FenLegalityValidator.Validate(grid, whiteToMove: true);

        Assert.False(isLegal);
    }

    [Fact]
    public void BlackKingInCheck_WhenWhiteToMove_IsIllegal()
    {
        char[,] grid = EmptyGrid();
        grid[7, 4] = 'K';
        grid[0, 4] = 'k';
        grid[0, 0] = 'R';

        (bool isLegal, _) = FenLegalityValidator.Validate(grid, whiteToMove: true);

        Assert.False(isLegal);
    }

    [Fact]
    public void WhiteKingInCheck_WhenWhiteToMove_IsLegal()
    {
        char[,] grid = EmptyGrid();
        grid[7, 4] = 'K';
        grid[0, 4] = 'k';
        grid[7, 0] = 'r';

        (bool isLegal, string? reason) = FenLegalityValidator.Validate(grid, whiteToMove: true);

        Assert.True(isLegal, reason);
    }

    [Fact]
    public void TooManyPieces_AreIllegal()
    {
        char[,] grid = EmptyGrid();
        grid[0, 4] = 'k';
        grid[7, 4] = 'K';
        for (int c = 0; c < 8; c++)
        {
            grid[1, c] = 'R';
            grid[2, c] = 'R';
        }

        (bool isLegal, _) = FenLegalityValidator.Validate(grid, whiteToMove: true);

        Assert.False(isLegal);
    }

    private static char[,] EmptyGrid()
    {
        var grid = new char[8, 8];
        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 8; c++)
            grid[r, c] = '1';
        return grid;
    }

    private static char[,] GridFromRows(params string[] rows)
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
}
