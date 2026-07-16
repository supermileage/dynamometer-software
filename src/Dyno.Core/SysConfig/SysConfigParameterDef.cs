using System.Globalization;
using Dyno.Core.Messages;

namespace Dyno.Core.SysConfig;

/// <summary>One selectable code of an <c>enum</c>-typed parameter: the raw value the firmware
/// stores and the human label the UI shows for it (e.g. code 6 → "475 SPS").</summary>
public sealed record SysConfigEnumOption(uint Value, string Label);

/// <summary>Host-side metadata for one runtime-tunable firmware parameter: what the wire id
/// means, how to edit it, and the firmware's boot default and accepted range.</summary>
/// <remarks>Rows live in the generated <see cref="SysConfigCatalog"/> (from the YAML schema's
/// sysconfig_params section plus config.h for the defaults) — this record only carries the
/// behavior. A value outside the range here would be rejected by the firmware
/// (USB_RSP_MALFORMED), so the host validates against the same bounds before sending.</remarks>
public sealed record SysConfigParameterDef(
    sysconfig_param_t Id,
    string Name,
    string Category,
    string Unit,
    string Description,
    bool IsFloat,
    double Default,
    double Min,
    double Max,
    string Subsection = "",
    IReadOnlyList<SysConfigEnumOption>? Options = null
)
{
    /// <summary>Whether this parameter belongs to a labelled sub-group of its category (e.g. the
    /// force sensor's I2C vs ADC sensing paths). When empty it sits directly under the category.
    /// </summary>
    public bool HasSubsection => Subsection.Length > 0;

    /// <summary>True when this parameter is an enumeration: its value is one of <see cref="Options"/>
    /// (a uint32 code), which the UI presents as a labelled dropdown rather than a number box.
    /// </summary>
    public bool IsEnum => Options is { Count: > 0 };

    /// <summary>True when <paramref name="value"/> is representable and inside the firmware's
    /// accepted range for this parameter.</summary>
    public bool IsValid(double value) =>
        double.IsFinite(value)
        && value >= Min
        && value <= Max
        && (IsFloat || (value == Math.Floor(value)));

    /// <summary>The 32 bits sent as <c>sysconfig_set_param_body.raw_value</c>: IEEE-754 bits for
    /// float parameters, the plain integer for uint32 ones.</summary>
    public uint ToRawBits(double value) =>
        IsFloat ? BitConverter.SingleToUInt32Bits((float)value) : (uint)value;

    /// <summary>The value those 32 bits stand for — the inverse of <see cref="ToRawBits"/>. Lets a
    /// caller holding only the wire form (the body of a write already sent) say what it means.</summary>
    public double FromRawBits(uint bits) => IsFloat ? BitConverter.UInt32BitsToSingle(bits) : bits;

    /// <summary>The value as a person reads it: no exponent, no trailing zeros, and never the
    /// current culture's decimal comma (these numbers are also what goes on the wire).</summary>
    public string Format(double value) =>
        value.ToString(IsFloat ? "0.######" : "0", CultureInfo.InvariantCulture);

    /// <summary>This parameter and value in words, e.g. <c>"K_P = 2.5"</c> or
    /// <c>"PID_TASK_OSDELAY = 10 ms"</c> — what a log line shows instead of a wire id.</summary>
    public string Describe(double value) =>
        $"{Name} = {Format(value)}{(Unit.Length > 0 ? $" {Unit}" : string.Empty)}";
}
