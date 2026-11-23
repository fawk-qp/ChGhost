using System.Diagnostics;
using ChGhost.Core.Native;

namespace ChGhost.Tests;

public class ChildProcessJobTests
{
    [Fact]
    public void DisposingJobTerminatesAssignedChildProcess()
    {
        string commandProcessor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        using Process child = Process.Start(new ProcessStartInfo
        {
            FileName = commandProcessor,
            Arguments = "/c ping 127.0.0.1 -n 30 > nul",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        try
        {
            using (var job = new ChildProcessJob())
                job.Assign(child);

            Assert.True(child.WaitForExit(5000), "Closing the job should terminate the child process.");
        }
        finally
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        }
    }
}
