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
}
