using System.Drawing;
using System.Drawing.Drawing2D;
using ChGhost.Core.Models;
using ChGhost.Core.Vision;

namespace ChGhost.Tests;

public class AutoBoardDetectorTests
{
    [Fact]
    public void Detect_FullHdScreenWithCenteredBoard_FindsExactBounds()
    {
        using Bitmap screen = CreateBrowserScreen(
            width: 1920,
            height: 1080,
            boardLeft: 520,
            boardTop: 210,
            boardSize: 640,
            lightColor: Color.FromArgb(240, 217, 181),
            darkColor: Color.FromArgb(181, 136, 99),
            drawPieces: true,
            highlightCells: new[] { (3, 3), (4, 4) });

        BoardDetectionDiagnostics diag = new AutoBoardDetector().DetectWithDiagnostics(screen);

        Assert.NotNull(diag.Rect);
        AssertRectClose(diag.Rect!.Value, 520, 210, 1160, 850, tolerance: 3);
    }

    [Fact]
    public void Detect_NonIntegerCellSizeAndHighDpiScreen_FindsExactBounds()
    {
        using Bitmap screen = CreateBrowserScreen(
            width: 2560,
            height: 1600,
            boardLeft: 315,
            boardTop: 190,
            boardSize: 550,
            lightColor: Color.FromArgb(238, 238, 210),
            darkColor: Color.FromArgb(118, 118, 95),
            drawPieces: true,
            highlightCells: new[] { (2, 5), (5, 2) });

        BoardDetectionDiagnostics diag = new AutoBoardDetector().DetectWithDiagnostics(screen);

        Assert.NotNull(diag.Rect);
        AssertRectClose(diag.Rect!.Value, 315, 190, 865, 740, tolerance: 4);
    }

    [Fact]
    public void Detect_PartiallyOccludedBoardWithCoordinates_StillFindsExactBounds()
    {
        using Bitmap screen = CreateBrowserScreen(
            width: 1600,
            height: 900,
            boardLeft: 560,
            boardTop: 210,
            boardSize: 480,
            lightColor: Color.FromArgb(235, 235, 215),
            darkColor: Color.FromArgb(115, 115, 95),
            drawPieces: true,
            drawCoordinates: true,
            occludeCenter: true,
            highlightCells: new[] { (1, 6) });

        BoardDetectionDiagnostics diag = new AutoBoardDetector().DetectWithDiagnostics(screen);

        Assert.NotNull(diag.Rect);
        AssertRectClose(diag.Rect!.Value, 560, 210, 1040, 690, tolerance: 4);
    }

    [Fact]
    public void Detect_NoBoardOnScreen_ReturnsNull()
    {
        using Bitmap screen = CreateNonBoardScreen();

        BoardRect? rect = new AutoBoardDetector().Detect(screen);

        Assert.Null(rect);
    }

    [Fact]
    public void Detect_MultiMonitorOffset_AppliesVirtualScreenOffset()
    {
        var detector = new AutoBoardDetector(screenCapturer: () => (CreateOffsetScreen(), -1920, 0));

        BoardDetectionDiagnostics diag = detector.DetectWithDiagnostics();

        Assert.NotNull(diag.Rect);
        AssertRectClose(diag.Rect!.Value, -1800, 80, -1160, 720, tolerance: 4);
    }

    private static void AssertRectClose(BoardRect actual, int left, int top, int right, int bottom, int tolerance)
    {
        Assert.True(Math.Abs(actual.Left - left) <= tolerance, $"Left {actual.Left} != {left} +/- {tolerance}");
        Assert.True(Math.Abs(actual.Top - top) <= tolerance, $"Top {actual.Top} != {top} +/- {tolerance}");
        Assert.True(Math.Abs(actual.Right - right) <= tolerance, $"Right {actual.Right} != {right} +/- {tolerance}");
        Assert.True(Math.Abs(actual.Bottom - bottom) <= tolerance, $"Bottom {actual.Bottom} != {bottom} +/- {tolerance}");
    }

    private static Bitmap CreateBrowserScreen(
        int width,
        int height,
        int boardLeft,
        int boardTop,
        int boardSize,
        Color lightColor,
        Color darkColor,
        bool drawPieces,
        bool drawCoordinates = false,
        bool occludeCenter = false,
        (int Row, int Col)[]? highlightCells = null)
    {
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.None;

        graphics.Clear(Color.FromArgb(24, 26, 34));
        using (var sidebar = new SolidBrush(Color.FromArgb(38, 41, 53)))
        {
            graphics.FillRectangle(sidebar, new Rectangle(0, 0, Math.Min(180, width / 5), height));
            graphics.FillRectangle(sidebar, new Rectangle(width - Math.Min(180, width / 5), 0, Math.Min(180, width / 5), height));
        }

        using (var panel = new SolidBrush(Color.FromArgb(52, 56, 72)))
        {
            graphics.FillRectangle(panel, new Rectangle(210, 70, width - 420, 84));
            graphics.FillRectangle(panel, new Rectangle(210, height - 130, width - 420, 60));
        }

        using (var accent = new SolidBrush(Color.FromArgb(64, 140, 255)))
            graphics.FillRectangle(accent, new Rectangle(width / 2 - 70, 82, 140, 22));

        DrawBoard(
            graphics,
            boardLeft,
            boardTop,
            boardSize,
            lightColor,
            darkColor,
            drawPieces,
            drawCoordinates,
            occludeCenter,
            highlightCells);
        return bitmap;
    }

    private static void DrawBoard(
        Graphics graphics,
        int left,
        int top,
        int size,
        Color lightColor,
        Color darkColor,
        bool drawPieces,
        bool drawCoordinates,
        bool occludeCenter,
        (int Row, int Col)[]? highlightCells)
    {
        float cell = size / 8f;
        using var lightBrush = new SolidBrush(lightColor);
        using var darkBrush = new SolidBrush(darkColor);
        using var highlightBrush = new SolidBrush(Color.FromArgb(150, 255, 235, 59));
        using var pieceLightBrush = new SolidBrush(Color.FromArgb(245, 245, 245));
        using var pieceDarkBrush = new SolidBrush(Color.FromArgb(32, 32, 32));

        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                var rect = new RectangleF(
                    left + c * cell,
                    top + r * cell,
                    cell,
                    cell);
                graphics.FillRectangle((r + c) % 2 == 0 ? lightBrush : darkBrush, rect);

                if (drawPieces)
                {
                    float diameter = cell * 0.62f;
                    var pieceRect = new RectangleF(
                        left + (c + 0.5f) * cell - diameter / 2,
                        top + (r + 0.5f) * cell - diameter / 2,
                        diameter,
                        diameter);
                    graphics.FillEllipse((r + c) % 2 == 0 ? pieceDarkBrush : pieceLightBrush, pieceRect);
                }
            }
        }

        if (highlightCells != null)
        {
            foreach (var (r, c) in highlightCells)
            {
                graphics.FillRectangle(
                    highlightBrush,
                    new RectangleF(left + c * cell, top + r * cell, cell, cell));
            }
        }

        if (drawCoordinates)
        {
            using var coordBrush = new SolidBrush(Color.FromArgb(220, 20, 20, 24));
            using var font = new Font("Consolas", Math.Max(8, size / 60f), FontStyle.Bold, GraphicsUnit.Pixel);
            for (int c = 0; c < 8; c++)
            {
                graphics.DrawString(((char)('a' + c)).ToString(), font, coordBrush,
                    new PointF(left + c * cell + 2, top + 7 * cell + cell - font.Height - 2));
                graphics.DrawString(((char)('a' + c)).ToString(), font, coordBrush,
                    new PointF(left + c * cell + 2, top + 2));
            }
            for (int r = 0; r < 8; r++)
            {
                graphics.DrawString((8 - r).ToString(), font, coordBrush,
                    new PointF(left + 2, top + r * cell + 2));
                graphics.DrawString((8 - r).ToString(), font, coordBrush,
                    new PointF(left + 7 * cell + cell - font.Height - 2, top + r * cell + 2));
            }
        }

        if (occludeCenter)
        {
            using var banner = new SolidBrush(Color.FromArgb(140, 35, 70, 140));
            graphics.FillRectangle(banner, new RectangleF(left + 2 * cell, top + 3 * cell, 4 * cell, 2 * cell));
            graphics.FillRectangle(banner, new RectangleF(left + 3 * cell, top + 2 * cell, 2 * cell, 4 * cell));
        }
    }

    private static Bitmap CreateOffsetScreen()
        => CreateBrowserScreen(
            width: 1024,
            height: 768,
            boardLeft: 120,
            boardTop: 80,
            boardSize: 640,
            lightColor: Color.FromArgb(240, 217, 181),
            darkColor: Color.FromArgb(181, 136, 99),
            drawPieces: true,
            highlightCells: new[] { (3, 4) });

    private static Bitmap CreateNonBoardScreen()
    {
        var bitmap = new Bitmap(1280, 720, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.None;
        graphics.Clear(Color.FromArgb(30, 30, 36));

        using (var sidebar = new SolidBrush(Color.FromArgb(40, 40, 48)))
            graphics.FillRectangle(sidebar, new Rectangle(0, 0, 240, 720));

        using (var lineBrush = new SolidBrush(Color.FromArgb(70, 72, 84)))
        {
            for (int i = 0; i < 22; i++)
            {
                int y = 90 + i * 24;
                int widthLine = 500 + ((i * 97) % 220);
                graphics.FillRectangle(lineBrush, new Rectangle(280, y, widthLine, 10));
            }
        }

        using (var windowBrush = new SolidBrush(Color.FromArgb(48, 50, 62)))
        {
            graphics.FillRectangle(windowBrush, new Rectangle(280, 40, 860, 560));
            graphics.FillRectangle(windowBrush, new Rectangle(280, 640, 420, 50));
        }

        using (var thumbBrush = new SolidBrush(Color.FromArgb(58, 60, 74)))
        using (var borderPen = new Pen(Color.FromArgb(20, 20, 26)))
        {
            for (int r = 0; r < 4; r++)
            for (int c = 0; c < 5; c++)
            {
                var rect = new Rectangle(300 + c * 165, 70 + r * 120, 140, 90);
                graphics.FillRectangle(thumbBrush, rect);
                graphics.DrawRectangle(borderPen, rect);
            }
        }

        return bitmap;
    }
}
