using System.Drawing;
using ChGhost.Core.Models;

namespace ChGhost.Core.Contracts;

public interface IBoardRecognizer
{
    string EngineName { get; }
    bool IsReady { get; }
    BoardRecognitionResult Recognize(Bitmap board, bool playAsBlack);
}
