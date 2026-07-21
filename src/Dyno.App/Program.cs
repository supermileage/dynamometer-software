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
            Console.Write(RawCaptureReplay.Format(report));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"cannot replay {path}: {ex.Message}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
