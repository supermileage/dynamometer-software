using System.Text.RegularExpressions;

namespace Dyno.Core.Firmware;

/// <summary>How a define's value should be edited.</summary>
public enum ConfigValueKind
{
    /// <summary>Free-form C token or expression (e.g. <c>ADS1115_RATE_475</c>, <c>16 + 1</c>).</summary>
    Text,

    /// <summary>Numeric literal, possibly with a C suffix (<c>100u</c>, <c>0.95f</c>).</summary>
    Number,

    /// <summary>An on/off toggle: <c>true</c>/<c>false</c>, or <c>1</c>/<c>0</c> in files whose
    /// binary defines are switches (debug.h's peripheral/task enables).</summary>
    Bool,
}

/// <summary>One editable <c>#define</c> from a firmware config header.</summary>
public sealed class ConfigDefine
{
    public required string Name { get; init; }

    /// <summary>Section the define sits under, taken from the standalone comment that heads its
    /// group in the file (e.g. <c>Main PID controller parameters</c>). Empty when the define has
    /// no section comment above it.</summary>
    public required string Category { get; init; }

    /// <summary>Comment text attached directly to this define: the comment block on the lines
    /// immediately above it (no blank line between) plus any trailing same-line comment.</summary>
    public required string Description { get; init; }

    public required ConfigValueKind Kind { get; init; }

    /// <summary>Zero-based line of the define within the file, for write-back.</summary>
    public required int LineIndex { get; init; }

    public string Value { get; internal set; } = string.Empty;
}

/// <summary>
/// Parses a firmware config header (<c>config.h</c> / <c>debug.h</c>) into its editable
/// <c>#define</c>s and writes edited values back without disturbing anything else in the file —
/// comments, blank lines, alignment and include guards all survive a round trip.
///
/// The grouping convention mirrors how these headers are actually written: a comment block
/// separated from the code above by a blank line is a section header ("category") for the defines
/// that follow, while a comment block glued directly under a define describes the <em>next</em>
/// define. Banner lines (<c>// ===== GPIO =====</c>) name their section by the banner text.
/// </summary>
public sealed class FirmwareConfigFile
{
    // Numeric literal with optional C suffix: 5, -1, 3.3f, 100u, 0.95F.
    private static readonly Regex NumberPattern = new(
        @"^[+-]?(\d+\.?\d*|\.\d+)[fFuUlL]{0,3}$",
        RegexOptions.Compiled
    );

    private readonly string[] _lines;
    private readonly string _newline;
    private readonly List<ConfigDefine> _defines;
    private readonly Dictionary<string, ConfigDefine> _byName;

    public string FileName { get; }
    public IReadOnlyList<ConfigDefine> Defines => _defines;

    private FirmwareConfigFile(
        string fileName,
        string[] lines,
        string newline,
        List<ConfigDefine> defines
    )
    {
        FileName = fileName;
        _lines = lines;
        _newline = newline;
        _defines = defines;
        _byName = defines.ToDictionary(d => d.Name);
    }

    /// <param name="fileName">Display name only (e.g. <c>config.h</c>); no I/O happens here.</param>
    /// <param name="content">The header's full text.</param>
    /// <param name="binaryTogglesAreBool">Treat bare <c>0</c>/<c>1</c> values as on/off switches.
    /// True for debug.h, where every define is an enable flag; false for config.h, where a 0 or 1
    /// would be an ordinary quantity.</param>
    public static FirmwareConfigFile Parse(
        string fileName,
        string content,
        bool binaryTogglesAreBool = false
    )
    {
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        var defines = new List<ConfigDefine>();
        var category = string.Empty;
        // Comment block being accumulated, and whether it opened directly under a define
        // (attached => description of the next define) or after a blank line (standalone =>
        // section header for the defines that follow).
        var pendingComments = new List<string>();
        var pendingIsAttached = false;
        var previousLineWasDefine = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length == 0)
            {
                // A blank line ends "directly under a define", so any comment that follows is a
                // section header, not a description. A standalone block already accumulated stays
                // pending: it still heads whatever defines come next.
                previousLineWasDefine = false;
                if (pendingIsAttached)
                {
                    // An attached comment with a blank line before the next define describes
                    // nothing that follows it; drop it rather than mislabel the next group.
                    pendingComments.Clear();
                    pendingIsAttached = false;
                }
                continue;
            }

            if (trimmed.StartsWith("//"))
            {
                if (pendingComments.Count == 0)
                {
                    pendingIsAttached = previousLineWasDefine;
                }
                pendingComments.Add(trimmed.TrimStart('/').Trim());
                previousLineWasDefine = false;
                continue;
            }

            if (TryParseDefine(trimmed, out var name, out var value, out var trailingComment))
            {
                var description = string.Empty;
                if (pendingComments.Count > 0)
                {
                    if (pendingIsAttached)
                    {
                        description = string.Join(" ", pendingComments);
                    }
                    else
                    {
                        category = CategoryName(pendingComments);
                    }
                    pendingComments.Clear();
                    pendingIsAttached = false;
                }
                if (trailingComment.Length > 0)
                {
                    description =
                        description.Length > 0
                            ? $"{description} {trailingComment}"
                            : trailingComment;
                }

                defines.Add(
                    new ConfigDefine
                    {
                        Name = name,
                        Category = category,
                        Description = description,
                        Kind = ClassifyValue(value, binaryTogglesAreBool),
                        LineIndex = i,
                        Value = value,
                    }
                );
                previousLineWasDefine = true;
                continue;
            }

            // Anything else (#ifndef/#endif/#include, the valueless include-guard #define):
            // comments hanging around it belong to the preprocessor scaffolding, not to settings.
            pendingComments.Clear();
            pendingIsAttached = false;
            previousLineWasDefine = false;
        }

        return new FirmwareConfigFile(fileName, lines, newline, defines);
    }

    /// <summary>Replaces the value of <paramref name="name"/> in the stored text, leaving the
    /// rest of its line (indentation, trailing comment) untouched. False if the define is unknown
    /// or the new value is empty/multi-line.</summary>
    public bool TrySetValue(string name, string newValue)
    {
        newValue = newValue.Trim();
        if (
            newValue.Length == 0
            || newValue.Contains('\n')
            || newValue.Contains("//")
            || newValue.Contains("/*")
            || !_byName.TryGetValue(name, out var define)
        )
        {
            return false;
        }

        var line = _lines[define.LineIndex];
        var nameStart = line.IndexOf(define.Name, StringComparison.Ordinal);
        var valueStart = nameStart + define.Name.Length;
        while (valueStart < line.Length && char.IsWhiteSpace(line[valueStart]))
        {
            valueStart++;
        }

        // The value runs to the trailing comment (if any), minus right-hand padding.
        var regionEnd = line.IndexOf("//", valueStart, StringComparison.Ordinal);
        if (regionEnd < 0)
        {
            regionEnd = line.IndexOf("/*", valueStart, StringComparison.Ordinal);
        }
        if (regionEnd < 0)
        {
            regionEnd = line.Length;
        }
        var valueEnd = regionEnd;
        while (valueEnd > valueStart && char.IsWhiteSpace(line[valueEnd - 1]))
        {
            valueEnd--;
        }

        _lines[define.LineIndex] = line[..valueStart] + newValue + line[valueEnd..];
        define.Value = newValue;
        return true;
    }

    /// <summary>The file's full text with any edits applied.</summary>
    public string ToText() => string.Join(_newline, _lines);

    private static bool TryParseDefine(
        string trimmedLine,
        out string name,
        out string value,
        out string trailingComment
    )
    {
        name = string.Empty;
        value = string.Empty;
        trailingComment = string.Empty;

        const string prefix = "#define";
        if (!trimmedLine.StartsWith(prefix))
        {
            return false;
        }

        var rest = trimmedLine[prefix.Length..].TrimStart();
        var nameEnd = 0;
        while (nameEnd < rest.Length && !char.IsWhiteSpace(rest[nameEnd]))
        {
            nameEnd++;
        }
        name = rest[..nameEnd];
        // Function-like macros aren't single settable values.
        if (name.Length == 0 || name.Contains('('))
        {
            return false;
        }

        var remainder = rest[nameEnd..];
        var commentIndex = remainder.IndexOf("//", StringComparison.Ordinal);
        if (commentIndex >= 0)
        {
            trailingComment = remainder[(commentIndex + 2)..].Trim();
            remainder = remainder[..commentIndex];
        }
        value = remainder.Trim();

        // A valueless #define is an include guard or feature marker, not an editable setting.
        return value.Length > 0;
    }

    private static ConfigValueKind ClassifyValue(string value, bool binaryTogglesAreBool) =>
        value switch
        {
            "true" or "false" => ConfigValueKind.Bool,
            "0" or "1" when binaryTogglesAreBool => ConfigValueKind.Bool,
            _ when NumberPattern.IsMatch(value) => ConfigValueKind.Number,
            _ => ConfigValueKind.Text,
        };

    /// <summary>Names a section from its comment block: the banner line if the block has one
    /// (<c>===== GPIO =====</c> → <c>GPIO</c>), otherwise the first line.</summary>
    private static string CategoryName(List<string> commentLines)
    {
        var banner = commentLines.LastOrDefault(l => l.Contains("====="));
        var raw = banner ?? commentLines[0];
        return raw.Trim('=', ' ', '-');
    }
}
