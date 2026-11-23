using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using ChGhost.Core.Contracts;
using ChGhost.Core.Models;
using ChGhost.Core.Native;

namespace ChGhost.Core.Engine;

public sealed class StockfishEngine : IChessEngine
{
    private readonly string _executablePath;
    private readonly AppSettings _settings;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private Process? _process;
    private ChildProcessJob? _job;
    private Channel<string>? _output;
    private Task? _outputReader;
    private bool _disposed;

    public StockfishEngine(string executablePath, AppSettings settings, IAppLogger logger)
    {
        _executablePath = executablePath;
        _settings = settings.Normalize();
        _logger = logger;
    }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_process is { HasExited: false })
                return;
            await EnsureRunningCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsEngineFailure(ex))
        {
            _logger.Error("Unable to ensure Stockfish is running.", ex);
            throw;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task NewGameAsync(CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureRunningCoreAsync(cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync("ucinewgame", cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync("isready", cancellationToken).ConfigureAwait(false);
            await WaitForLineAsync(line => line == "readyok", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsEngineFailure(ex))
        {
            _logger.Error("Stockfish failed while starting a new game.", ex);
            await StopProcessAsync(graceful: true).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<string> GetBestMoveAsync(string fen, int moveTimeMs, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fen);
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureRunningCoreAsync(cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync($"position fen {fen}", cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync($"go movetime {Math.Clamp(moveTimeMs, 1, 60_000)}", cancellationToken).ConfigureAwait(false);
            string response = await WaitForLineAsync(line => line.StartsWith("bestmove ", StringComparison.Ordinal), cancellationToken)
                .ConfigureAwait(false);
            string[] parts = response.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                throw new InvalidDataException($"Invalid Stockfish response: '{response}'.");
            return parts[1];
        }
        catch (Exception ex) when (IsEngineFailure(ex))
        {
            _logger.Error("Stockfish failed while calculating a move.", ex);
            await StopProcessAsync(graceful: true).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopProcessAsync(graceful: true).ConfigureAwait(false);
            await StartProcessAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _commandLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopProcessAsync(graceful: true).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task EnsureRunningCoreAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
            return;
        if (_process != null || _job != null)
            await StopProcessAsync(graceful: false).ConfigureAwait(false);
        await StartProcessAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StartProcessAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_executablePath))
            throw new FileNotFoundException("Stockfish executable was not found.", _executablePath);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                WorkingDirectory = Path.GetDirectoryName(_executablePath)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        ChildProcessJob? job = null;
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Stockfish process did not start.");
            job = new ChildProcessJob();
            job.Assign(process);
            _process = process;
            _job = job;
            _output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            _outputReader = ReadOutputAsync(process, _output.Writer);
            await WriteCommandAsync("uci", cancellationToken).ConfigureAwait(false);
            await WaitForLineAsync(line => line == "uciok", cancellationToken).ConfigureAwait(false);

            await WriteCommandAsync($"setoption name Threads value {_settings.EffectiveEngineThreads}", cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync($"setoption name Hash value {_settings.EngineHashMb}", cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync("setoption name MultiPV value 1", cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync("setoption name Move Overhead value 15", cancellationToken).ConfigureAwait(false);
            if (_settings.FullStrength)
            {
                await WriteCommandAsync("setoption name UCI_LimitStrength value false", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteCommandAsync("setoption name UCI_LimitStrength value true", cancellationToken).ConfigureAwait(false);
                await WriteCommandAsync($"setoption name UCI_Elo value {_settings.TargetElo}", cancellationToken).ConfigureAwait(false);
            }

            await WriteCommandAsync("ucinewgame", cancellationToken).ConfigureAwait(false);
            await WriteCommandAsync("isready", cancellationToken).ConfigureAwait(false);
            await WaitForLineAsync(line => line == "readyok", cancellationToken).ConfigureAwait(false);
            _logger.Info($"Stockfish started with {_settings.EffectiveEngineThreads} thread(s), {_settings.EngineHashMb} MB hash.");
        }
        catch
        {
            if (_process == process)
                await StopProcessAsync(graceful: true).ConfigureAwait(false);
            else
            {
                job?.Dispose();
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception cleanupException) when (cleanupException is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _logger.Error("Unable to clean up Stockfish after startup failure.", cleanupException);
                }
                finally
                {
                    process.Dispose();
                }
            }
            throw;
        }
    }

    private async Task ReadOutputAsync(Process process, ChannelWriter<string> writer)
    {
        try
        {
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                    break;
                await writer.WriteAsync(line).ConfigureAwait(false);
            }
            writer.TryComplete(process.HasExited
                ? new InvalidOperationException($"Stockfish exited with code {process.ExitCode}.")
                : null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.Error("Error while reading Stockfish output.", ex);
            writer.TryComplete(ex);
        }
    }

    private async Task WriteCommandAsync(string command, CancellationToken cancellationToken)
    {
        Process? process = _process;
        if (process == null || process.HasExited)
            throw new InvalidOperationException("Stockfish is not running.");
        await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> WaitForLineAsync(Func<string, bool> predicate, CancellationToken cancellationToken)
    {
        ChannelReader<string> reader = _output?.Reader ?? throw new InvalidOperationException("Stockfish output is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_settings.CommandTimeoutMs);
        try
        {
            while (true)
            {
                string line = await reader.ReadAsync(timeout.Token)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromMilliseconds(_settings.CommandTimeoutMs), cancellationToken)
                    .ConfigureAwait(false);
                if (predicate(line))
                    return line;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Stockfish did not respond within {_settings.CommandTimeoutMs} ms.");
        }
        catch (ChannelClosedException ex)
        {
            throw new InvalidOperationException("Stockfish closed its output stream.", ex);
        }
    }

    private async Task StopProcessAsync(bool graceful)
    {
        Process? process = _process;
        ChildProcessJob? job = _job;
        Task? outputReader = _outputReader;
        _process = null;
        _job = null;
        _outputReader = null;
        _output = null;

        if (process == null)
        {
            job?.Dispose();
            return;
        }

        try
        {
            if (!process.HasExited && graceful)
            {
                await process.StandardInput.WriteLineAsync("quit").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.Warn("Stockfish did not exit after quit; terminating the process.");
                }
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or ObjectDisposedException)
        {
            _logger.Error("Error while stopping Stockfish.", ex);
        }
        finally
        {
            job?.Dispose();
            process.Dispose();
            if (outputReader != null)
            {
                try
                {
                    await outputReader.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    _logger.Error("Timed out while closing the Stockfish output reader.", ex);
                }
            }
        }
    }

    private static bool IsEngineFailure(Exception exception) =>
        exception is TimeoutException or IOException or InvalidOperationException or InvalidDataException
            or System.ComponentModel.Win32Exception or ChannelClosedException;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
