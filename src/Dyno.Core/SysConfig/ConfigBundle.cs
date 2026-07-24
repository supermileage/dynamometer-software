using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dyno.Core.SysConfig;

/// <summary>
/// Every setting the SysConfig page holds, in one portable object: the runtime parameters pushed to
/// the device, the PC constants this app derives with, and the compile-time overrides saved for the
/// next build. This is what an exported <c>.json</c> contains and what an imported one is read into.
/// </summary>
/// <remarks>
/// Names are the keys — <c>K_P</c>, <c>GEAR_RATIO</c>, <c>USB_TX_BUFFER_SIZE</c> — not wire ids or
/// positions, so a file stays readable and stays valid across a schema change that renumbers ids.
/// The consequence is that a name this build does not know is simply reported and skipped rather
/// than silently landing on whatever parameter now sits at that number.
/// </remarks>
public sealed record ConfigBundle(
    IReadOnlyDictionary<string, double> Runtime,
    IReadOnlyDictionary<string, double> PcConstants,
    IReadOnlyDictionary<string, string> CompileTime
)
{
    public static ConfigBundle Empty { get; } =
        new(
            new Dictionary<string, double>(),
            new Dictionary<string, double>(),
            new Dictionary<string, string>()
        );

    /// <summary>How many settings the bundle carries, across all three sections.</summary>
    public int Count => Runtime.Count + PcConstants.Count + CompileTime.Count;
}

/// <summary>A parsed file plus everything about it that was not usable.</summary>
/// <param name="Bundle">The settings that read cleanly.</param>
/// <param name="Problems">Entries that did not: a value of the wrong type, a section that is not an
/// object. Each is a sentence for the event log. Skipping such an entry leaves that setting absent
/// from <paramref name="Bundle"/>, so the import reports it as missing too — which is what it is
/// from the page's point of view.</param>
public sealed record ConfigBundleReadResult(ConfigBundle Bundle, IReadOnlyList<string> Problems);

/// <summary>Reads and writes <see cref="ConfigBundle"/> as JSON.</summary>
/// <remarks>
/// Deliberately hand-rolled over <c>JsonNode</c> rather than deserialized into a DTO: the file is
/// something a person may write by hand or paste together, so a single bad entry must cost that
/// entry rather than the whole import. A DTO binder throws on the first type mismatch and takes
/// every other setting in the file down with it.
/// </remarks>
public static class ConfigBundleJson
{
    /// <summary>Bumped only if the shape changes in a way an older reader would misread. Written
    /// into every file; a file without it is read as version 1, since that is what the first
    /// hand-written files will look like.</summary>
    public const int FormatVersion = 1;

    private const string VersionKey = "formatVersion";
    private const string RuntimeKey = "runtime";
    private const string PcConstantsKey = "pcConstants";
    private const string CompileTimeKey = "compileTime";

    public static string Write(ConfigBundle bundle)
    {
        var runtime = new JsonObject();
        foreach (var (name, value) in bundle.Runtime.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            runtime[name] = value;
        }

        var pc = new JsonObject();
        foreach (
            var (name, value) in bundle.PcConstants.OrderBy(p => p.Key, StringComparer.Ordinal)
        )
        {
            pc[name] = value;
        }

        // Compile-time values stay strings: they are C tokens, not numbers ("16 + 1", "true",
        // "0.95f"). Writing them as JSON numbers would lose the suffix that makes them valid C.
        var compile = new JsonObject();
        foreach (
            var (name, value) in bundle.CompileTime.OrderBy(p => p.Key, StringComparer.Ordinal)
        )
        {
            compile[name] = value;
        }

        var root = new JsonObject
        {
            [VersionKey] = FormatVersion,
            [RuntimeKey] = runtime,
            [PcConstantsKey] = pc,
            [CompileTimeKey] = compile,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Parses a file. Throws only when the document as a whole is unusable; anything
    /// narrower is reported in <see cref="ConfigBundleReadResult.Problems"/> and skipped.</summary>
    /// <exception cref="InvalidDataException">The text is not JSON, or its root is not an object.
    /// </exception>
    public static ConfigBundleReadResult Read(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"not valid JSON: {ex.Message}", ex);
        }

        if (root is not JsonObject obj)
        {
            throw new InvalidDataException(
                "the file's top level is not a JSON object — expected one with "
                    + $"\"{RuntimeKey}\", \"{PcConstantsKey}\" and \"{CompileTimeKey}\" sections"
            );
        }

        var problems = new List<string>();
        if (
            obj[VersionKey] is JsonValue version
            && version.TryGetValue(out int fileVersion)
            && fileVersion > FormatVersion
        )
        {
            problems.Add(
                $"the file says it is format version {fileVersion} and this build reads "
                    + $"{FormatVersion} — anything it adds was ignored"
            );
        }

        return new ConfigBundleReadResult(
            new ConfigBundle(
                ReadNumbers(obj, RuntimeKey, problems),
                ReadNumbers(obj, PcConstantsKey, problems),
                ReadStrings(obj, CompileTimeKey, problems)
            ),
            problems
        );
    }

    private static Dictionary<string, double> ReadNumbers(
        JsonObject root,
        string section,
        List<string> problems
    )
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, node) in Section(root, section, problems))
        {
            if (node is JsonValue value && value.TryGetValue(out double number))
            {
                values[name] = number;
            }
            else
            {
                problems.Add($"{section}.{name} is not a number — ignored");
            }
        }
        return values;
    }

    private static Dictionary<string, string> ReadStrings(
        JsonObject root,
        string section,
        List<string> problems
    )
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, node) in Section(root, section, problems))
        {
            // A number or bool is accepted and kept verbatim: a hand-written file is likely to say
            // 512 or true rather than "512", and both are what the header would contain anyway.
            if (node is JsonValue value)
            {
                values[name] = value.ToString();
            }
            else
            {
                problems.Add($"{section}.{name} is not a single value — ignored");
            }
        }
        return values;
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Section(
        JsonObject root,
        string section,
        List<string> problems
    )
    {
        if (root[section] is null)
        {
            return []; // an absent section is not a problem; every setting in it reports as missing
        }
        if (root[section] is not JsonObject entries)
        {
            problems.Add($"\"{section}\" is not a JSON object — the whole section was ignored");
            return [];
        }
        return entries;
    }
}
