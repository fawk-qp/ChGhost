using ChGhost.Core.Contracts;
using ChGhost.Core.Engine;
using ChGhost.Core.Models;

namespace ChGhost.Tests;

public class StockfishEngineTests
{
    [Fact]
    public async Task MissingExecutableFailsExplicitly()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "stockfish.exe");
        await using var engine = new StockfishEngine(missingPath, AppSettings.CreateDefaults(), new TestLogger());

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            engine.EnsureRunningAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsObservedDuringProtocolInitialization()
    {
        string commandProcessor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        Assert.True(File.Exists(commandProcessor));
        await using var engine = new StockfishEngine(commandProcessor, AppSettings.CreateDefaults(), new TestLogger());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.EnsureRunningAsync(cancellation.Token));
    }

    [Fact]
    public async Task DisposeAsyncCompletesWithoutAStartedProcess()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "stockfish.exe");
        var engine = new StockfishEngine(missingPath, AppSettings.CreateDefaults(), new TestLogger());

        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class TestLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
