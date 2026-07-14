namespace Dyno.App.Services;

/// <summary>
/// Finds the firmware config headers on disk. The app lives in the same mono-repo as the
/// firmware, so both are located by walking up from where the app runs until the repo's
/// <c>firmware/Core/Inc/Config</c> directory appears. The <c>DYNO_FIRMWARE_CONFIG_DIR</c>
/// environment variable overrides the search for an app run outside the repo tree.
/// </summary>
public static class FirmwareConfigLocator
{
    public const string OverrideVariable = "DYNO_FIRMWARE_CONFIG_DIR";

    private static readonly string RelativeConfigDir = Path.Combine(
        "firmware",
        "Core",
        "Inc",
        "Config"
    );

    /// <summary>Absolute path of the directory holding config.h/debug.h, or null when the app is
    /// running somewhere the repo can't be seen from.</summary>
    public static string? FindConfigDirectory()
    {
        if (Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } overridden)
        {
            return File.Exists(Path.Combine(overridden, "config.h")) ? overridden : null;
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, RelativeConfigDir);
                if (File.Exists(Path.Combine(candidate, "config.h")))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}
