using ChGhost.Core.Diagnostics;

namespace ChGhost.Tests;

public class FileAppLoggerTests
{
    [Fact]
    public async Task ConcurrentWritesAreSerialized()
    {
        using var directory = new TemporaryDirectory();
        var logger = new FileAppLogger(directory.Path);

        await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(index => Task.Run(() => logger.Info($"entry-{index}"))));

        string logPath = Path.Combine(directory.Path, "logs", "chghost.log");
        string[] lines = File.ReadAllLines(logPath);
        Assert.Equal(40, lines.Length);
        foreach (int index in Enumerable.Range(0, 40))
            Assert.Contains(lines, line => line.Contains($"entry-{index}", StringComparison.Ordinal));
    }

    [Fact]
    public void LogRotatesAfterExceedingTwoMegabytes()
    {
        using var directory = new TemporaryDirectory();
        var logger = new FileAppLogger(directory.Path);
        logger.Info(new string('x', 2 * 1024 * 1024));
        logger.Info("after-rotation");

        string logPath = Path.Combine(directory.Path, "logs", "chghost.log");
        Assert.True(File.Exists(logPath + ".1"));
        Assert.Contains("after-rotation", File.ReadAllText(logPath), StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
