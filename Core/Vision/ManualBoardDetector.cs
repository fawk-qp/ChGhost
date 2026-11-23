using System.Drawing;
using ChGhost.Core.Contracts;
using ChGhost.Core.Models;

namespace ChGhost.Core.Vision;

public sealed class ManualBoardDetector : IBoardDetector
{
    private readonly object _sync = new();
    private Point? _topLeft;
    private Point? _bottomRight;

    public Point? TopLeft
    {
        get { lock (_sync) return _topLeft; }
    }

    public Point? BottomRight
    {
        get { lock (_sync) return _bottomRight; }
    }

    public BoardRect? Detect(Bitmap? fullScreen = null)
    {
        lock (_sync)
        {
            if (!_topLeft.HasValue || !_bottomRight.HasValue)
                return null;

            return new BoardRect(
                _topLeft.Value.X,
                _topLeft.Value.Y,
                _bottomRight.Value.X,
                _bottomRight.Value.Y);
        }
    }

    public void SetTopLeft(int x, int y)
    {
        lock (_sync)
            _topLeft = new Point(x, y);
    }

    public void SetBottomRight(int x, int y)
    {
        lock (_sync)
            _bottomRight = new Point(x, y);
    }

    public void Reset()
    {
        lock (_sync)
        {
            _topLeft = null;
            _bottomRight = null;
        }
    }
}
