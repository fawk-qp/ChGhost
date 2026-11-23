namespace ChGhost.Core.Contracts;

public interface IChessEngine : IAsyncDisposable
{
    Task EnsureRunningAsync(CancellationToken cancellationToken);
    Task NewGameAsync(CancellationToken cancellationToken);
    Task<string> GetBestMoveAsync(string fen, int moveTimeMs, CancellationToken cancellationToken);
    Task RestartAsync(CancellationToken cancellationToken);
}
