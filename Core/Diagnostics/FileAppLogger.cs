using System.Text;
using System.IO;
using ChGhost.Core.Contracts;

namespace ChGhost.Core.Diagnostics;

public sealed class FileAppLogger : IAppLogger
{
    private const long MaxFileSize = 2 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly string _logPath;

    public FileAppLogger(string? baseDirectory = null)
    {
        string directory = Path.Combine(baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "logs");
        _logPath = Path.Combine(directory, "chghost.log");
    }

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? exception)
    {
        string text = $"{DateTimeOffset.Now:O} [{level}] {message}";
        if (exception != null)
            text += Environment.NewLine + exception;
        text += Environment.NewLine;

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                RotateIfNeeded();
                File.AppendAllText(_logPath, text, Encoding.UTF8);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Trace.WriteLine($"Unable to write application log: {ex}");
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Trace.WriteLine($"Unable to write application log: {ex}");
            }
        }
    }

    private void RotateIfNeeded()
    {
        var file = new FileInfo(_logPath);
        if (!file.Exists || file.Length < MaxFileSize)
            return;

        string previous = _logPath + ".1";
        if (File.Exists(previous))
            File.Delete(previous);
        File.Move(_logPath, previous);
    }
}
