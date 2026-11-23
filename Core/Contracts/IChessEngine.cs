using ChGhost.Core.Models;

namespace ChGhost.Core.Contracts;

public interface IChessEngine : IAsyncDisposable
{
    Task EnsureRunningAsync(CancellationToken cancellationToken);
    Task NewGameAsync(CancellationToken cancellationToken);
    Task<string> GetBestMoveAsync(string fen, int moveTimeMs, CancellationToken cancellationToken);
    Task<IReadOnlyList<CandidateMove>> GetTopMovesAsync(string fen, int moveTimeMs, int multiPv, CancellationToken cancellationToken = default);
    Task UnloadIfIdleAsync(TimeSpan maxIdle);
    Task RestartAsync(CancellationToken cancellationToken);
}
