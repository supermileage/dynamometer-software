using Dyno.Core.Messages;

namespace Dyno.Core.SysConfig;

/// <summary>Host-side metadata for one runtime-tunable firmware parameter: what the wire id
/// means, how to edit it, and the firmware's boot default and accepted range.</summary>
/// <remarks>Kept in sync by hand with two firmware files: the boot defaults come from
/// <c>Core/Inc/Config/config.h</c> and the ranges from the table in
/// <c>Core/Src/Config/sysconfig.c</c>. A value outside the range here would be rejected by the
/// firmware (USB_RSP_MALFORMED), so the host validates against the same bounds before sending.</remarks>
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

/// <summary>Every runtime-tunable parameter the firmware exposes, in wire-id order.</summary>
public static class SysConfigCatalog
{
    private const uint MsMax = 60000; // firmware's OSDELAY_MAX: caps delays at one minute
    private const string OsDelayNote =
        "Delay (ms) at the end of each loop pass of the task; lower = faster polling, more CPU.";

    public static IReadOnlyList<SysConfigParameterDef> Parameters { get; } =
        new SysConfigParameterDef[]
        {
            new(
                sysconfig_param_t.SYSCFG_DISTANCE_FROM_FORCE_SENSOR_TO_CENTER_OF_SHAFT_M,
                "DISTANCE_FROM_FORCE_SENSOR_TO_CENTER_OF_SHAFT_M",
                "Mechanical",
                "m",
                "Lever arm from the force sensor to the shaft center, used in the torque calculation.",
                IsFloat: true,
                Default: 1.0,
                Min: 1e-6,
                Max: 1000
            ),
            new(
                sysconfig_param_t.SYSCFG_MOMENT_OF_INERTIA_KG_M2,
                "MOMENT_OF_INERTIA_KG_M2",
                "Mechanical",
                "kg·m²",
                "Rotating assembly's moment of inertia, used in the torque calculation.",
                IsFloat: true,
                Default: 1.0,
                Min: 1e-9,
                Max: 1e6
            ),
            new(
                sysconfig_param_t.SYSCFG_K_P,
                "K_P",
                "PID Controller",
                "",
                "Proportional gain.",
                IsFloat: true,
                Default: 1.0,
                Min: -1e6,
                Max: 1e6
            ),
            new(
                sysconfig_param_t.SYSCFG_K_I,
                "K_I",
                "PID Controller",
                "",
                "Integral gain.",
                IsFloat: true,
                Default: 1.0,
                Min: -1e6,
                Max: 1e6
            ),
            new(
                sysconfig_param_t.SYSCFG_K_D,
                "K_D",
                "PID Controller",
                "",
                "Derivative gain.",
                IsFloat: true,
                Default: 1.0,
                Min: -1e6,
                Max: 1e6
            ),
            new(
                sysconfig_param_t.SYSCFG_PID_MAX_OUTPUT,
                "PID_MAX_OUTPUT",
                "PID Controller",
                "",
                "Normalization bound for the PID output before throttle/brake mixing.",
                IsFloat: true,
                Default: 100.0,
                Min: 1e-6,
                Max: 1e6
            ),
            new(
                sysconfig_param_t.SYSCFG_THROTTLE_GAIN,
                "THROTTLE_GAIN",
                "PID Controller",
                "",
                "Gain applied to the throttle half of the PID output mix.",
                IsFloat: true,
                Default: 1.0,
                Min: -1e6,
                Max: 1e6
            ),
            new(
                sysconfig_param_t.SYSCFG_BRAKE_GAIN,
                "BRAKE_GAIN",
                "PID Controller",
                "",
                "Gain applied to the brake half of the PID output mix.",
                IsFloat: true,
                Default: 1.0,
                Min: -1e6,
                Max: 1e6
            ),
            new(
                sysconfig_param_t.SYSCFG_HORIZONTAL_BIAS,
                "HORIZONTAL_BIAS",
                "PID Controller",
                "",
                "Horizontal offset of the throttle/brake mixing curve.",
                IsFloat: true,
                Default: 0.0,
                Min: -1e6,
                Max: 1e6
            ),
            new(
                sysconfig_param_t.SYSCFG_VERTICAL_BIAS,
                "VERTICAL_BIAS",
                "PID Controller",
                "",
                "Vertical offset of the throttle/brake mixing curve.",
                IsFloat: true,
                Default: 0.0,
                Min: -1e6,
                Max: 1e6
            ),
            new(
                sysconfig_param_t.SYSCFG_MIN_DUTY_CYCLE_PERCENT,
                "MIN_DUTY_CYCLE_PERCENT",
                "BPM",
                "0–1",
                "Lower clamp on the brake PWM duty cycle.",
                IsFloat: true,
                Default: 0.0,
                Min: 0,
                Max: 1
            ),
            new(
                sysconfig_param_t.SYSCFG_MAX_DUTY_CYCLE_PERCENT,
                "MAX_DUTY_CYCLE_PERCENT",
                "BPM",
                "0–1",
                "Upper clamp on the brake PWM duty cycle.",
                IsFloat: true,
                Default: 0.95,
                Min: 0,
                Max: 1
            ),
            new(
                sysconfig_param_t.SYSCFG_MAX_FORCE_LBF,
                "MAX_FORCE_LBF",
                "Force Sensor",
                "lbf",
                "Full-scale force of the load cell; scales raw ADC counts to newtons.",
                IsFloat: true,
                Default: 25.0,
                Min: 1e-3,
                Max: 1e5
            ),
            new(
                sysconfig_param_t.SYSCFG_SESSIONCONTROLLER_TASK_OSDELAY,
                "SESSIONCONTROLLER_TASK_OSDELAY",
                "Session Controller",
                "ms",
                OsDelayNote,
                IsFloat: false,
                Default: 5,
                Min: 1,
                Max: MsMax
            ),
            new(
                sysconfig_param_t.SYSCFG_BPM_TASK_OSDELAY,
                "BPM_TASK_OSDELAY",
                "BPM",
                "ms",
                OsDelayNote,
                IsFloat: false,
                Default: 3,
                Min: 1,
                Max: MsMax
            ),
            new(
                sysconfig_param_t.SYSCFG_FORCESENSOR_TASK_OSDELAY,
                "FORCESENSOR_TASK_OSDELAY",
                "Force Sensor",
                "ms",
                OsDelayNote,
                IsFloat: false,
                Default: 1,
                Min: 1,
                Max: MsMax
            ),
            new(
                sysconfig_param_t.SYSCFG_FORCESENSOR_COMMAND_POLL_OSDELAY,
                "FORCESENSOR_COMMAND_POLL_OSDELAY",
                "Force Sensor",
                "ms",
                "Bounded wait on the enable queue while the sensor is idle, so USB commands still get serviced.",
                IsFloat: false,
                Default: 50,
                Min: 1,
                Max: MsMax
            ),
            new(
                sysconfig_param_t.SYSCFG_FORCESENSOR_CONVERSION_TIMEOUT_MS,
                "FORCESENSOR_CONVERSION_TIMEOUT_MS",
                "Force Sensor",
                "ms",
                "How long to wait for the ADS1115 conversion-ready alert before abandoning the sample.",
                IsFloat: false,
                Default: 250,
                Min: 1,
                Max: MsMax
            ),
            new(
                sysconfig_param_t.SYSCFG_OPTICAL_ENCODER_TASK_OSDELAY,
                "OPTICAL_ENCODER_TASK_OSDELAY",
                "Optical Encoder",
                "ms",
                OsDelayNote,
                IsFloat: false,
                Default: 2,
                Min: 1,
                Max: MsMax
            ),
            new(
                sysconfig_param_t.SYSCFG_NUM_APERTURES,
                "NUM_APERTURES",
                "Optical Encoder",
                "",
                "Apertures on the encoder wheel (tied to the physical 3D-printed apparatus).",
                IsFloat: false,
                Default: 64,
                Min: 1,
                Max: 100000
            ),
            new(
                sysconfig_param_t.SYSCFG_PID_TASK_OSDELAY,
                "PID_TASK_OSDELAY",
                "PID Controller",
                "ms",
                OsDelayNote,
                IsFloat: false,
                Default: 10,
                Min: 1,
                Max: MsMax
            ),
            new(
                sysconfig_param_t.SYSCFG_USB_TASK_OSDELAY,
                "USB_TASK_OSDELAY",
                "USB",
                "ms",
                OsDelayNote,
                IsFloat: false,
                Default: 5,
                Min: 1,
                Max: MsMax
            ),
            new(
                sysconfig_param_t.SYSCFG_USB_TX_FLUSH_MAX_RETRIES,
                "USB_TX_FLUSH_MAX_RETRIES",
                "USB",
                "attempts",
                "Bounded retries when flushing a full TX buffer before dropping the batch.",
                IsFloat: false,
                Default: 5,
                Min: 0,
                Max: 1000
            ),
            new(
                sysconfig_param_t.SYSCFG_LCD_TASK_OSDELAY,
                "LCD_TASK_OSDELAY",
                "LCD",
                "ms",
                OsDelayNote,
                IsFloat: false,
                Default: 20,
                Min: 1,
                Max: MsMax
            ),
            new(
                sysconfig_param_t.SYSCFG_LED_TASK_OSDELAY,
                "LED_TASK_OSDELAY",
                "LED",
                "ms",
                "Blink half-period of the status LED task.",
                IsFloat: false,
                Default: 500,
                Min: 1,
                Max: MsMax
            ),
            new(
                sysconfig_param_t.SYSCFG_TASK_WARNING_RETRY_OSDELAY,
                "TASK_WARNING_RETRY_OSDELAY",
                "Tasks & Monitoring",
                "ms",
                "Back-off a task sleeps after reporting a warning before retrying.",
                IsFloat: false,
                Default: 100,
                Min: 1,
                Max: MsMax
            ),
            new(
                sysconfig_param_t.SYSCFG_TASK_MONITOR_TASK_OSDELAY,
                "TASK_MONITOR_TASK_OSDELAY",
                "Tasks & Monitoring",
                "ms",
                "How often the task monitor samples every task's state and free stack.",
                IsFloat: false,
                Default: 1000,
                Min: 1,
                Max: MsMax
            ),
        };

    /// <summary>Looks up a parameter's definition by wire id.</summary>
    public static SysConfigParameterDef Get(sysconfig_param_t id) => Parameters[(int)id];
}
