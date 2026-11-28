using System.Drawing;
using ChGhost.Core.Models;

namespace ChGhost.Core.Contracts;

public interface ICalibratableRecognizer : IBoardRecognizer
{
    int CalibrationClassCount { get; }
    CalibrationOutcome ComputeStartingPositionCalibration(Bitmap board, bool playAsBlack);
    void ApplyCalibration(CalibrationOutcome calibration);
    CalibrationOutcome CalibrateStartingPosition(Bitmap board, bool playAsBlack);
    bool CalibratePiece(Bitmap board, Square square, PieceClass piece);
    void ResetCalibration();
    SavedConfig ExportCalibration();
    void ImportCalibration(SavedConfig config);
}
