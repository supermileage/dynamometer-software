namespace Dyno.Core;

/// <summary>
/// Sample rate for the force-sensor ADC (ADS1115), in samples per second. The backing value is
/// the on-wire rate code (0..7) sent as the single body byte of a
/// <c>FORCE_SENSOR_CMD_SET_DATA_RATE</c> command. The code→SPS mapping is fixed by the ADS1115
/// silicon (the datasheet DR field), not by firmware policy, so it is safe to name the rates
/// host-side; only the choice of which code to send is the firmware's concern.
/// </summary>
public enum ForceSensorSampleRate : byte
{
    Sps8 = 0,
    Sps16 = 1,
    Sps32 = 2,
    Sps64 = 3,
    Sps128 = 4, // ADS1115 power-on default
    Sps250 = 5,
    Sps475 = 6,
    Sps860 = 7,
}

public static class ForceSensorSampleRateExtensions
{
    /// <summary>Samples per second the rate represents.</summary>
    public static int SamplesPerSecond(this ForceSensorSampleRate rate) =>
        rate switch
        {
            ForceSensorSampleRate.Sps8 => 8,
            ForceSensorSampleRate.Sps16 => 16,
            ForceSensorSampleRate.Sps32 => 32,
            ForceSensorSampleRate.Sps64 => 64,
            ForceSensorSampleRate.Sps128 => 128,
            ForceSensorSampleRate.Sps250 => 250,
            ForceSensorSampleRate.Sps475 => 475,
            ForceSensorSampleRate.Sps860 => 860,
            _ => 0,
        };

    /// <summary>Human-readable label, e.g. <c>"128 SPS"</c>.</summary>
    public static string ToLabel(this ForceSensorSampleRate rate) =>
        $"{rate.SamplesPerSecond()} SPS";
}
