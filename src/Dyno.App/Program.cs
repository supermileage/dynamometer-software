using Avalonia;
using Dyno.Core.Diagnostics;

namespace Dyno.App;

internal static class Program
{
    // Avalonia configuration; don't use any Avalonia, third-party APIs or any SynchronizationContext-reliant
    // code before AppMain is called: things aren't initialized yet and stuff will break.
    [STAThread]
    public static int Main(string[] args)
    {
        // TEMP DIAGNOSTIC (16-byte head-loss investigation): replay a DYNO_RAW_CAPTURE recording
        // and exit, without starting the UI. Handled before AppBuilder so it stays a plain
        // console run — it reads a file and prints, and needs no windowing at all.
        if (args is ["--replay", var path, ..])
        {
            return Replay(path);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static int Replay(string path)
    {
        try
        {
            var report = RawCaptureReplay.Replay(RawCapture.Read(path));
            Emit(path, RawCaptureReplay.Format(report));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Emit(path, $"cannot replay {path}: {ex.Message}{Environment.NewLine}");
            return 1;
        }
    }

    /// <summary>Writes the report to stdout <em>and</em> to a file beside the capture.
    /// This project is a <c>WinExe</c>, so on Windows it is a GUI-subsystem process with no console
    /// attached: launched from a shell, everything written to stdout is discarded and the command
    /// looks like it silently did nothing. Running it as `dotnet Dyno.App.dll` does get a console
    /// (the host owns it), but a diagnostic that only works when invoked one particular way is a
    /// diagnostic that will be reported as broken. The file always survives.</summary>
    private static void Emit(string capturePath, string report)
    {
        Console.Write(report);
        try
        {
            string destination = capturePath + ".report.txt";
            File.WriteAllText(destination, report);
            Console.WriteLine($"(also written to {destination})");
        }
        catch (IOException)
        {
            // stdout may well be all we have; if the sidecar cannot be written, it is not worth
            // failing a read-only diagnostic over.
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
