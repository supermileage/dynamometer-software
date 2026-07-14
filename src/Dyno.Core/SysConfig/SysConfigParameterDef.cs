using Dyno.Core.Messages;

namespace Dyno.Core.SysConfig;

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
    double Max
)
{
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
}
