namespace Dyno.Core.Firmware;

/// <summary>Which build the scripts produce and flash. Both default to Release, so a build and a
/// flash with no arguments line up.</summary>
public enum FirmwareBuild
{
    Debug,
    Release,
}

/// <summary>How the firmware image reaches the board. Each is a different physical connection, and
/// each accepts a different set of tools — see <see cref="FirmwareCommands.ToolsFor"/>.</summary>
public enum FlashMethod
{
    /// <summary>SWD through an ST-Link probe. The only method that needs no bootloader dance.</summary>
    Swd,

    /// <summary>USB DFU through the chip's ROM bootloader — a USB cable and BOOT0 high.</summary>
    Dfu,

    /// <summary>UART through the ROM bootloader — a USB-serial adapter and BOOT0 high.</summary>
    Uart,
}

/// <summary>Everything <c>flash.sh</c> / <c>flash.ps1</c> needs. Which of the optional fields matter
/// depends on the method (and, for <see cref="Index"/>, on the tool) — <see cref="FirmwareCommands"/>
/// passes on only the ones the chosen combination actually reads.</summary>
public sealed record FlashRequest(
    FlashMethod Method,
    string Tool,
    FirmwareBuild Build = FirmwareBuild.Release,
    string? Serial = null,
    string? Index = null,
    string? Port = null,
    string? Baud = null
);

/// <summary>A process to run, as the app would have typed it at a shell.</summary>
public sealed record ProcessCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory
)
{
    /// <summary>The command as one line, for echoing into the output log — so what the app did is
    /// always something the user could have run themselves, and can paste into a bug report.</summary>
    public string DisplayLine =>
        string.Join(
            ' ',
            new[] { FileName }.Concat(Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
        );
}

/// <summary>
/// Builds the exact <c>Scripts/</c> invocations for building and flashing the firmware. The scripts
/// are the single source of truth for how this board is programmed — they encode the tool matrix,
/// the ROM-bootloader rules and the "which build tree is newer" logic — so the app drives them
/// rather than reimplementing any of it, and a user who prefers a terminal runs the same commands.
/// </summary>
public static class FirmwareCommands
{
    /// <summary>The tools each method accepts, in the order the page offers them: the open-source,
    /// no-account tool first, since STM32CubeProgrammer needs an ST login to even download.</summary>
    public static IReadOnlyList<string> ToolsFor(FlashMethod method) =>
        method switch
        {
            FlashMethod.Swd => ["st-flash", "openocd", "cubeprog"],
            FlashMethod.Dfu => ["dfu-util", "cubeprog"],
            FlashMethod.Uart => ["stm32flash", "cubeprog"],
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

    /// <summary>True when the method goes through the chip's ROM bootloader, which the user must
    /// enter by hand (BOOT0 high, then reset) before anything can be written.</summary>
    public static bool NeedsBootloader(FlashMethod method) => method is not FlashMethod.Swd;

    /// <summary>Build in the pinned Docker toolchain image — byte-for-byte the environment CI uses.
    /// <paramref name="rebuildImage"/> refreshes the image itself, which is only needed after the
    /// Dockerfile changes.</summary>
    public static ProcessCommand Build(
        string firmwareDirectory,
        FirmwareBuild build,
        bool rebuildImage,
        bool? windows = null
    )
    {
        if (OnWindows(windows))
        {
            List<string> args = ["-Config", build.ToString()];
            if (rebuildImage)
            {
                args.Add("-Rebuild");
            }
            return PowerShell(firmwareDirectory, "build-docker.ps1", args);
        }

        List<string> shArgs = [build.ToString()];
        if (rebuildImage)
        {
            shArgs.Add("--rebuild");
        }
        return Bash(firmwareDirectory, "build-docker.sh", shArgs);
    }

    /// <summary>Flash an already-built image. The script finds the newer of the Docker and native
    /// build trees itself, so the app does not have to care which one produced the firmware.</summary>
    public static ProcessCommand Flash(
        string firmwareDirectory,
        FlashRequest request,
        bool? windows = null
    )
    {
        if (!ToolsFor(request.Method).Contains(request.Tool))
        {
            throw new ArgumentException(
                $"'{request.Tool}' cannot flash over {Name(request.Method)} "
                    + $"(use one of: {string.Join(", ", ToolsFor(request.Method))}).",
                nameof(request)
            );
        }

        return OnWindows(windows)
            ? PowerShell(
                firmwareDirectory,
                "flash.ps1",
                [
                    "-Config",
                    request.Build.ToString(),
                    "-Method",
                    Name(request.Method),
                    "-Tool",
                    request.Tool,
                    .. Options(request, "-Serial", "-Index", "-Port", "-Baud"),
                ]
            )
            : Bash(
                firmwareDirectory,
                "flash.sh",
                [
                    request.Build.ToString(),
                    Name(request.Method),
                    "--tool",
                    request.Tool,
                    .. Options(request, "--serial", "--index", "--port", "--baud"),
                ]
            );
    }

    /// <summary>Enumerate what is actually plugged in: probes, DFU devices, or serial ports. The
    /// answer is what the user pastes into the serial/port box, so the page can offer it before a
    /// flash rather than after one fails.</summary>
    public static ProcessCommand ListDevices(
        string firmwareDirectory,
        FlashMethod method,
        string? tool = null,
        bool? windows = null
    )
    {
        // UART listing is the script's own `ls /dev/serial/by-id` — it takes no tool, and passing
        // one to the others just narrows *which* enumerator runs.
        var withTool = tool is { Length: > 0 } && method is not FlashMethod.Uart;

        return OnWindows(windows)
            ? PowerShell(
                firmwareDirectory,
                "flash.ps1",
                ["-Method", Name(method), .. withTool ? new[] { "-Tool", tool! } : [], "-List"]
            )
            : Bash(
                firmwareDirectory,
                "flash.sh",
                [Name(method), .. withTool ? new[] { "--tool", tool! } : [], "--list"]
            );
    }

    /// <summary>The device-selection arguments this method/tool combination actually reads. Passing
    /// a port to an SWD flash, or a serial number to a UART one, would be noise at best — the script
    /// would ignore it, and the user would be left believing it had an effect.</summary>
    private static List<string> Options(
        FlashRequest request,
        string serialFlag,
        string indexFlag,
        string portFlag,
        string baudFlag
    )
    {
        var args = new List<string>();
        switch (request.Method)
        {
            case FlashMethod.Swd:
                Add(serialFlag, request.Serial);
                break;

            case FlashMethod.Dfu:
                Add(serialFlag, request.Serial);
                // Only cubeprog addresses a DFU device by index (port=USB<n>); dfu-util has no such
                // notion and takes the serial instead.
                if (request.Tool == "cubeprog")
                {
                    Add(indexFlag, request.Index);
                }
                break;

            case FlashMethod.Uart:
                Add(portFlag, request.Port);
                Add(baudFlag, request.Baud);
                break;
        }
        return args;

        void Add(string flag, string? value)
        {
            if (value is { Length: > 0 })
            {
                args.Add(flag);
                args.Add(value.Trim());
            }
        }
    }

    private static string Name(FlashMethod method) => method.ToString().ToLowerInvariant();

    private static bool OnWindows(bool? windows) => windows ?? OperatingSystem.IsWindows();

    // The script paths below use a literal '/' rather than Path.Combine: bash reads a
    // backslash as an escape character (so the Windows separator would garble the path
    // outright), PowerShell accepts '/' on every platform, and a fixed separator keeps the
    // echoed DisplayLine identical wherever the command runs.

    /// <summary>Run through <c>bash</c> explicitly rather than executing the script: a fresh clone
    /// (or a checkout on a filesystem with no exec bit) leaves the scripts non-executable, and
    /// "permission denied" is a poor first experience.</summary>
    private static ProcessCommand Bash(
        string firmwareDirectory,
        string script,
        List<string> args
    ) => new("bash", [$"Scripts/{script}", .. args], firmwareDirectory);

    private static ProcessCommand PowerShell(
        string firmwareDirectory,
        string script,
        List<string> args
    ) =>
        new(
            "powershell",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $"Scripts/{script}", .. args],
            firmwareDirectory
        );
}
