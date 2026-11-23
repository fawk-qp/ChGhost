namespace ChGhost.Core.Models;

public readonly record struct BoardRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsValid => Width >= 120 && Height >= 120;
}
