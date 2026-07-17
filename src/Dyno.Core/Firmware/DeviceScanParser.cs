using System.Text.RegularExpressions;

namespace Dyno.Core.Firmware;

/// <summary>Which flash argument a scanned device fills when the user picks it.</summary>
public enum FlashTargetField
{
    /// <summary>A probe/DFU serial number → <c>--serial</c>.</summary>
    Serial,

    /// <summary>STM32CubeProgrammer's DFU device index (<c>port=USB&lt;n&gt;</c>) → <c>--index</c>.</summary>
    Index,

    /// <summary>A serial-port path → <c>--port</c>.</summary>
    Port,
}

/// <summary>One connected device the user can flash, as found by a scan.</summary>
/// <param name="Label">A short human name (<c>ST-Link</c>, <c>DFU device</c>, the port's by-id name).</param>
/// <param name="Detail">The identifier, shown in monospace beneath the label.</param>
/// <param name="Field">Which flash argument picking this device sets.</param>
/// <param name="Value">What that argument becomes.</param>
public sealed record FlashTarget(string Label, string Detail, FlashTargetField Field, string Value);

/// <summary>
/// Turns a flashing tool's <c>--list</c> output into a list of pickable devices, so the user clicks
/// the board they mean instead of copying a serial number out of a wall of text.
///
/// Each tool prints its own format, so parsing is per (method, tool). It is deliberately lax: it
/// scavenges the identifiers it recognises and ignores everything else (banners, the "Failed to
/// enter SWD mode" noise st-info prints, version lines), because the raw output is still kept in the
/// Console tab as the ground truth — this list is a convenience over it, not a replacement.
/// </summary>
public static class DeviceScanParser
{
    private static readonly Regex StlinkSerial = new(@"^serial:\s*(\S+)", RegexOptions.IgnoreCase);
    private static readonly Regex StlinkVersion = new(
        @"^version:\s*(\S+)",
        RegexOptions.IgnoreCase
    );
    private static readonly Regex CubeStlinkSn = new(
        @"ST-LINK SN\s*:\s*(\S+)",
        RegexOptions.IgnoreCase
    );
    private static readonly Regex DfuUtilSerial = new(@"serial=""([^""]+)""");
    private static readonly Regex CubeDeviceIndex = new(
        @"Device Index\s*:\s*USB(\d+)",
        RegexOptions.IgnoreCase
    );
    private static readonly Regex CubeSerial = new(
        @"Serial number\s*:\s*(\S+)",
        RegexOptions.IgnoreCase
    );
    private static readonly Regex ByIdLink = new(@"(?<name>[^\s/]+)\s*->\s*\S*/(?<dev>tty\S+)");
    private static readonly Regex DevPath = new(@"^(/dev/tty\S+)$");
    private static readonly Regex ComPort = new(@"\b(COM\d+)\b");

    public static IReadOnlyList<FlashTarget> Parse(
        FlashMethod method,
        string tool,
        IReadOnlyList<string> outputLines
    )
    {
        var lines = outputLines.Select(l => l.Trim()).ToList();
        return (method, tool) switch
        {
            (FlashMethod.Swd, "st-flash") => StLink(lines),
            (FlashMethod.Swd, "cubeprog") => CubeStlink(lines),
            // openocd has no list mode (the script says so and exits); nothing to pick.
            (FlashMethod.Swd, _) => [],
            (FlashMethod.Dfu, "dfu-util") => DfuUtil(lines),
            (FlashMethod.Dfu, "cubeprog") => CubeDfu(lines),
            (FlashMethod.Dfu, _) => [],
            (FlashMethod.Uart, _) => SerialPorts(lines),
            _ => [],
        };
    }

    /// <summary>st-info --probe / st-flash --list: a "version:" line then a "serial:" line per probe.</summary>
    private static List<FlashTarget> StLink(List<string> lines)
    {
        var targets = new List<FlashTarget>();
        string? version = null;
        foreach (var line in lines)
        {
            if (StlinkVersion.Match(line) is { Success: true } v)
            {
                version = v.Groups[1].Value;
            }
            else if (StlinkSerial.Match(line) is { Success: true } s)
            {
                var serial = s.Groups[1].Value;
                var label = version is null ? "ST-Link probe" : $"ST-Link probe ({version})";
                targets.Add(new FlashTarget(label, serial, FlashTargetField.Serial, serial));
                version = null;
            }
        }
        return Dedupe(targets);
    }

    /// <summary>STM32CubeProgrammer -l: one "ST-LINK SN : …" line per probe.</summary>
    private static List<FlashTarget> CubeStlink(List<string> lines) =>
        Dedupe(
            lines
                .Select(l => CubeStlinkSn.Match(l))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .Select(sn => new FlashTarget("ST-Link probe", sn, FlashTargetField.Serial, sn))
                .ToList()
        );

    /// <summary>dfu-util -l: a "Found DFU:" line per alt-setting, all carrying the same serial="…".</summary>
    private static List<FlashTarget> DfuUtil(List<string> lines) =>
        Dedupe(
            lines
                .Select(l => DfuUtilSerial.Match(l))
                .Where(m => m.Success && m.Groups[1].Value is not ("" or "UNKNOWN"))
                .Select(m => m.Groups[1].Value)
                .Select(sn => new FlashTarget(
                    "DFU device",
                    $"serial {sn}",
                    FlashTargetField.Serial,
                    sn
                ))
                .ToList()
        );

    /// <summary>STM32CubeProgrammer -l usb: a "Device Index : USBn" and a "Serial number : …" per device.</summary>
    private static List<FlashTarget> CubeDfu(List<string> lines)
    {
        var targets = new List<FlashTarget>();
        string? index = null;
        foreach (var line in lines)
        {
            if (CubeDeviceIndex.Match(line) is { Success: true } i)
            {
                // A new device block. If the previous one had no serial we still keep it — the
                // index alone is enough to flash cubeprog.
                if (index is not null)
                {
                    targets.Add(Cube(index, null));
                }
                index = i.Groups[1].Value;
            }
            else if (index is not null && CubeSerial.Match(line) is { Success: true } s)
            {
                targets.Add(Cube(index, s.Groups[1].Value));
                index = null;
            }
        }
        if (index is not null)
        {
            targets.Add(Cube(index, null));
        }
        return Dedupe(targets);

        static FlashTarget Cube(string index, string? serial) =>
            new(
                $"DFU device (USB{index})",
                serial is null ? $"index {index}" : $"serial {serial}",
                FlashTargetField.Index,
                index
            );
    }

    /// <summary>flash.sh's UART listing: `ls -l /dev/serial/by-id` symlinks, a plain `ls` of
    /// /dev/tty* paths, or COM ports on Windows.</summary>
    private static List<FlashTarget> SerialPorts(List<string> lines)
    {
        var targets = new List<FlashTarget>();
        foreach (var line in lines)
        {
            if (ByIdLink.Match(line) is { Success: true } link)
            {
                var dev = "/dev/" + link.Groups["dev"].Value;
                targets.Add(
                    new FlashTarget(link.Groups["name"].Value, dev, FlashTargetField.Port, dev)
                );
            }
            else if (DevPath.Match(line) is { Success: true } path)
            {
                var dev = path.Groups[1].Value;
                targets.Add(
                    new FlashTarget(Path.GetFileName(dev), dev, FlashTargetField.Port, dev)
                );
            }
            else if (ComPort.Match(line) is { Success: true } com)
            {
                var port = com.Groups[1].Value;
                targets.Add(new FlashTarget(port, port, FlashTargetField.Port, port));
            }
        }
        return Dedupe(targets);
    }

    private static List<FlashTarget> Dedupe(List<FlashTarget> targets) =>
        targets.GroupBy(t => (t.Field, t.Value)).Select(g => g.First()).ToList();
}
