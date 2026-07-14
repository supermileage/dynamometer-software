using System.Diagnostics;

namespace Dyno.Core.Firmware;

/// <summary>
/// Runs a <see cref="ProcessCommand"/> and streams its output line by line as it appears.
///
/// A Docker build takes minutes and a flash takes seconds, and both fail in ways only their own
/// output explains — a missing tool, a board not in the bootloader, a probe someone else has open.
/// So nothing is buffered until exit: the app shows the tool's words, as the tool says them, and
/// leaves them on screen.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Runs to completion and returns the exit code (0 = success). Output and errors are
    /// interleaved into <paramref name="onLine"/> in arrival order, as a user watching a terminal
    /// would see them.</summary>
    /// <exception cref="OperationCanceledException">The caller cancelled. The process is killed
    /// first, along with any children — the build script's real work happens in <c>docker</c> and
    /// <c>cmake</c> beneath it, and killing only the shell would leave those running.</exception>
    public static async Task<int> RunAsync(
        ProcessCommand command,
        Action<string> onLine,
        CancellationToken cancellationToken = default
    )
    {
        var info = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Emit(e.Data);
        process.ErrorDataReceived += (_, e) => Emit(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // The launcher itself is missing (no bash on a bare Windows box, no powershell on
            // Linux). Report it as the tool's own failure rather than throwing: the page's job is
            // to show what went wrong, and this is one of the more likely things to.
            onLine($"ERROR: could not run '{command.FileName}': {ex.Message}");
            return -1;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return process.ExitCode;

        void Emit(string? line)
        {
            if (line is not null)
            {
                onLine(line);
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // It exited between the check and the kill, or the OS refused. Either way the caller is
            // already unwinding on cancellation and there is nothing better to do about it.
        }
    }
}
