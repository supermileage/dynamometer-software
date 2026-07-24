namespace Dyno.Core.Serial;

/// <summary>
/// A name for a serial device that outlives the device node it currently points at.
/// </summary>
/// <remarks>
/// A USB board that reboots — a reflash, a reset, a re-plug — is enumerated afresh, and Linux gives
/// the new interface the lowest free ttyACM number rather than the one it had. A link that was on
/// /dev/ttyACM0 comes back on /dev/ttyACM1, and code holding the old node name is holding the name
/// of nothing. udev's persistent symlinks are the way back: they are keyed on the USB descriptors
/// (and, where the firmware reports one, the serial number), so one board keeps one path across
/// every re-enumeration.
///
/// Two sources, in <see cref="DefaultSources"/> order:
/// <list type="bullet">
/// <item><c>/dev/serial/by-id/</c> — udev makes these for USB serial devices out of the box, and
/// they carry the serial number, so they tell two identical boards apart. Preferred.</item>
/// <item><c>/dev/dyno</c> — this project's own rule (<c>scripts/udev/99-dyno-cdc.rules</c>), which
/// matches on VID/PID alone. It cannot distinguish two dyno boards, so it is the fallback.</item>
/// </list>
///
/// Windows has no equivalent — a COM number is the only name a port has — so nothing resolves and
/// callers get null. Following a board that comes back as a different COM port is therefore not
/// something this can do; the caller falls back to watching for the original name to reappear.
/// </remarks>
public sealed record PortAlias(string Path)
{
    /// <summary>Where aliases are looked for, most discriminating first. An entry that is a
    /// directory contributes every link in it; anything else is taken as a single link.</summary>
    public static readonly string[] DefaultSources = ["/dev/serial/by-id", "/dev/dyno"];

    /// <summary>
    /// The stable alias currently pointing at <paramref name="node"/>, or null if there is none —
    /// on Windows, or on a Linux box whose udev made no link for this device.
    /// </summary>
    public static PortAlias? For(string node, params string[] sources)
    {
        if (Resolve(node) is not { } target)
        {
            return null;
        }

        foreach (var candidate in Expand(sources.Length == 0 ? DefaultSources : sources))
        {
            // Resolving to the same node is what makes something an alias for it. The node itself
            // trivially satisfies that and is exactly what we are trying to stop depending on.
            if (
                !string.Equals(candidate, node, StringComparison.Ordinal)
                && string.Equals(Resolve(candidate), target, StringComparison.Ordinal)
            )
            {
                return new PortAlias(candidate);
            }
        }
        return null;
    }

    /// <summary>The device node this alias points at right now, or null while the board is off the
    /// bus — which is the question a reconnect watcher is really asking.</summary>
    public string? CurrentNode() => Resolve(Path);

    private static IEnumerable<string> Expand(IReadOnlyList<string> sources)
    {
        foreach (var source in sources)
        {
            if (!Directory.Exists(source))
            {
                yield return source;
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            // A composite device exposes several interfaces (-if00, -if02, …). Ordering makes the
            // pick reproducible instead of dependent on directory order.
            Array.Sort(entries, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                yield return entry;
            }
        }
    }

    /// <summary>The final target of a symlink chain, the path itself if it is not a link, or null
    /// if nothing is there — including the dangling-link case, which is precisely how a removed
    /// device presents.</summary>
    private static string? Resolve(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            // A null target means the path was not a link at all, so it is its own answer. A
            // non-null one is only an answer if something is actually at the end of it: File.Exists
            // above is satisfied by the link itself, dangling or not, and a link left behind by a
            // device that has gone is exactly the case this has to report as absent.
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return target is null ? path
                : target.Exists ? target.FullName
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
