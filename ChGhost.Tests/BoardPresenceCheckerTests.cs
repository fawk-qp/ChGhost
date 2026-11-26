using ChGhost.Core.Vision;

namespace ChGhost.Tests;

public class BoardPresenceCheckerTests
{
    private const int Width = 512;
    private const int Height = 512;
    private const int TileSize = 64;
    private const int Stride = Width * 3;

    [Fact]
    public void Checkerboard_IsPresent()
    {
        byte[] buffer = CreateBoard(highlighted: false);

        Assert.True(BoardPresenceChecker.IsCheckerboardPresent(buffer, Stride));
    }

    [Fact]
    public void CheckerboardWithHighlights_IsPresent()
    {
        byte[] buffer = CreateBoard(highlighted: true);

        Assert.True(BoardPresenceChecker.IsCheckerboardPresent(buffer, Stride));
    }

    [Fact]
    public void DarkBackground_IsNotPresent()
    {
        var buffer = new byte[Stride * Height];
        for (int i = 0; i < buffer.Length; i += 3)
        {
            buffer[i] = 20;
            buffer[i + 1] = 20;
            buffer[i + 2] = 20;
        }

        Assert.False(BoardPresenceChecker.IsCheckerboardPresent(buffer, Stride));
    }

    [Fact]
    public void Checkerboard48_IsPresent()
    {
        byte[] buffer = CreateBoard(highlighted: false, width: 384, height: 384, tileSize: 48);

        Assert.True(BoardPresenceChecker.IsCheckerboardPresent(buffer, 384 * 3, 48));
    }

    [Fact]
    public void DarkBackground48_IsNotPresent()
    {
        var buffer = new byte[384 * 384 * 3];
        for (int i = 0; i < buffer.Length; i += 3)
        {
            buffer[i] = 20;
            buffer[i + 1] = 20;
            buffer[i + 2] = 20;
        }

        Assert.False(BoardPresenceChecker.IsCheckerboardPresent(buffer, 384 * 3, 48));
    }

    [Fact]
    public void HorizontalWindowStripes_AreNotPresent()
    {
        var buffer = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
        {
            byte value = (byte)(((y / 16) & 1) == 0 ? 180 : 60);
            int row = y * Stride;
            for (int x = 0; x < Width; x++)
            {
                int offset = row + x * 3;
                buffer[offset] = value;
                buffer[offset + 1] = value;
                buffer[offset + 2] = value;
            }
        }

        Assert.False(BoardPresenceChecker.IsCheckerboardPresent(buffer, Stride));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TinyTileSize_ReturnsFalseWithoutThrowing(int tileSize)
    {
        var buffer = new byte[64];

        Assert.False(BoardPresenceChecker.IsCheckerboardPresent(buffer, 64, tileSize));
    }

    private static byte[] CreateBoard(bool highlighted, int width = Width, int height = Height, int tileSize = TileSize)
    {
        int stride = width * 3;
        var buffer = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int sr = y / tileSize;
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int sc = x / tileSize;
                bool even = ((sr + sc) & 1) == 0;
                bool highlight = highlighted && sr == sc && sr < 4;
                byte value = (byte)(highlight || !even ? 35 : 210);
                int offset = row + x * 3;
                buffer[offset] = value;
                buffer[offset + 1] = value;
                buffer[offset + 2] = value;
            }
        }
        return buffer;
    }
}
