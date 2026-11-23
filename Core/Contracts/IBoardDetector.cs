using System.Drawing;
using ChGhost.Core.Models;

namespace ChGhost.Core.Contracts;

public interface IBoardDetector
{
    Point? TopLeft { get; }
    Point? BottomRight { get; }
    BoardRect? Detect(Bitmap? fullScreen = null);
    void SetTopLeft(int x, int y);
    void SetBottomRight(int x, int y);
    void Reset();
}
