using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ChGhost.Core.Models;
using ChGhost.Core.Vision;

namespace ChGhost.Tests;

public class TemplateRecognizerTests
{
    [Fact]
    public void UniformBoardIsRecognizedAsEmpty()
    {
        using var bitmap = new Bitmap(640, 640);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.Clear(Color.Gray);

        BoardRecognitionResult result = new TemplateRecognizer().Recognize(bitmap, playAsBlack: false);

        Assert.Equal("8/8/8/8/8/8/8/8", result.RawFen);
        Assert.Equal(13, result.ClassScores.GetLength(2));
    }

    [Fact]
    public void StartingPositionCanBeCalibratedAndRecognizedAgain()
    {
        using Bitmap bitmap = CreateStartingBoard();
        var recognizer = new TemplateRecognizer();

        CalibrationOutcome calibration = recognizer.CalibrateStartingPosition(bitmap, playAsBlack: false);
        BoardRecognitionResult result = recognizer.Recognize(bitmap, playAsBlack: false);

        Assert.True(calibration.IsStartingPosition);
        Assert.True(recognizer.IsReady);
        Assert.Equal("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR", result.RawFen);
    }

    [Fact]
    public void ComputeStartingPositionCalibration_DoesNotMutateStateUntilApplyCalibration()
    {
        using Bitmap bitmap = CreateStartingBoard();
        var recognizer = new TemplateRecognizer();

        CalibrationOutcome computed = recognizer.ComputeStartingPositionCalibration(bitmap, playAsBlack: false);

        Assert.True(computed.IsStartingPosition);
        Assert.False(recognizer.IsReady);
        Assert.Equal(0, recognizer.CalibrationClassCount);
        Assert.Equal("8/8/8/8/8/8/8/8", recognizer.Recognize(bitmap, playAsBlack: false).RawFen);

        recognizer.ApplyCalibration(computed);

        Assert.True(recognizer.IsReady);
        Assert.Equal(12, recognizer.CalibrationClassCount);
        Assert.Equal("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR", recognizer.Recognize(bitmap, playAsBlack: false).RawFen);
    }

    [Fact]
    public void DiscardedComputedCalibration_LeavesExistingOrSwitchedProfileSignaturesUntouched()
    {
        using Bitmap board = CreateStartingBoard();
        var recognizer = new TemplateRecognizer();

        // Simulate profile 1 being calibrated before an async computation completes
        CalibrationOutcome staleComputed = recognizer.ComputeStartingPositionCalibration(board, playAsBlack: false);
        Assert.True(staleComputed.IsStartingPosition);

        // Simulate switching to profile 2 and loading a single manual signature
        recognizer.ResetCalibration();
        recognizer.CalibratePiece(board, new Square(7, 4), PieceClass.WhiteKing);
        SavedConfig beforeDiscard = recognizer.ExportCalibration();
        Assert.Equal(1, recognizer.CalibrationClassCount);
        Assert.False(recognizer.IsReady);

        // Stale background calibration is discarded (ApplyCalibration is NOT called)
        SavedConfig afterDiscard = recognizer.ExportCalibration();
        Assert.Equal(1, recognizer.CalibrationClassCount);
        Assert.False(recognizer.IsReady);
        Assert.Single(afterDiscard.Signatures);
        Assert.True(afterDiscard.Signatures.ContainsKey('K'));
        Assert.Equal(beforeDiscard.WhiteBodyR, afterDiscard.WhiteBodyR);
    }

    private static Bitmap CreateStartingBoard()
    {
        var bitmap = new Bitmap(640, 640);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        string[] initial =
        {
            "rnbqkbnr", "pppppppp", "........", "........",
            "........", "........", "PPPPPPPP", "RNBQKBNR"
        };
        using var font = new Font(FontFamily.GenericSansSerif, 34, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                var cell = new Rectangle(col * 80, row * 80, 80, 80);
                graphics.FillRectangle((row + col) % 2 == 0 ? Brushes.BurlyWood : Brushes.SaddleBrown, cell);
                char piece = initial[row][col];
                if (piece == '.') continue;
                using var brush = new SolidBrush(char.IsUpper(piece) ? Color.WhiteSmoke : Color.FromArgb(20, 20, 20));
                graphics.DrawString(char.ToUpperInvariant(piece).ToString(), font, brush, cell, format);
            }
        }
        return bitmap;
    }
}
