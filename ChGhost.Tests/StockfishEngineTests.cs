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
    public async Task CancellationDuringActiveSearchStopsEngineAndNextSearchSucceeds()
    {
        string? enginePath = FindStockfishExecutable();
        if (enginePath is null)
            return;

        const string fen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
        await using var engine = new StockfishEngine(enginePath, AppSettings.CreateDefaults(), new TestLogger());
        await engine.EnsureRunningAsync(CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        Task<IReadOnlyList<CandidateMove>> search = engine.GetTopMovesAsync(fen, 30_000, 2, cancellation.Token);
        await Task.Delay(800);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await search;
        });

        IReadOnlyList<CandidateMove> moves = await engine
            .GetTopMovesAsync(fen, 200, 2, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(moves);
    }

    [Fact]
    public async Task DisposeAsyncCompletesWithoutAStartedProcess()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "stockfish.exe");
        var engine = new StockfishEngine(missingPath, AppSettings.CreateDefaults(), new TestLogger());

        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static string? FindStockfishExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "Release_Build", "stockfish.exe");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    private sealed class TestLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
