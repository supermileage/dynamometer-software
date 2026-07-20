// AUTO-GENERATED from tools/message_gen/schema/messages_public.yaml by tools/message_gen/generate.py -- DO NOT EDIT.
// Ranges, categories, units and descriptions come from that schema; each Default comes
// from the parameter's #define in firmware/Core/Inc/Config/config.h. Edit those sources,
// then run tools/message_gen/generate.py (CI verifies the committed file matches).
using Dyno.Core.Messages;

namespace Dyno.Core.SysConfig;

/// <summary>Every runtime-tunable parameter the firmware exposes, in wire-id order.</summary>
public static class SysConfigCatalog
{
    public static IReadOnlyList<SysConfigParameterDef> Parameters { get; } =
        new SysConfigParameterDef[]
        {
            new(
                sysconfig_param_t.SYSCFG_K_P,
                "K_P",
                "PID Controller",
                "",
                "Proportional gain.",
                IsFloat: true,
                Default: 1.0,
                Min: -1000000.0,
                Max: 1000000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_K_I,
                "K_I",
                "PID Controller",
                "",
                "Integral gain.",
                IsFloat: true,
                Default: 1.0,
                Min: -1000000.0,
                Max: 1000000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_K_D,
                "K_D",
                "PID Controller",
                "",
                "Derivative gain.",
                IsFloat: true,
                Default: 1.0,
                Min: -1000000.0,
                Max: 1000000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_PID_MAX_OUTPUT,
                "PID_MAX_OUTPUT",
                "PID Controller",
                "",
                "Normalization bound for the PID output before throttle/brake mixing.",
                IsFloat: true,
                Default: 100.0,
                Min: 1e-06,
                Max: 1000000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_THROTTLE_GAIN,
                "THROTTLE_GAIN",
                "PID Controller",
                "",
                "Gain applied to the throttle half of the PID output mix.",
                IsFloat: true,
                Default: 1.0,
                Min: -1000000.0,
                Max: 1000000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_BRAKE_GAIN,
                "BRAKE_GAIN",
                "PID Controller",
                "",
                "Gain applied to the brake half of the PID output mix.",
                IsFloat: true,
                Default: 1.0,
                Min: -1000000.0,
                Max: 1000000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_HORIZONTAL_BIAS,
                "HORIZONTAL_BIAS",
                "PID Controller",
                "",
                "Horizontal offset of the throttle/brake mixing curve.",
                IsFloat: true,
                Default: 0.0,
                Min: -1000000.0,
                Max: 1000000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_VERTICAL_BIAS,
                "VERTICAL_BIAS",
                "PID Controller",
                "",
                "Vertical offset of the throttle/brake mixing curve.",
                IsFloat: true,
                Default: 0.0,
                Min: -1000000.0,
                Max: 1000000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_MIN_DUTY_CYCLE_PERCENT,
                "MIN_DUTY_CYCLE_PERCENT",
                "BPM",
                "0–1",
                "Lower clamp on the brake PWM duty cycle.",
                IsFloat: true,
                Default: 0.0,
                Min: 0.0,
                Max: 1.0
            ),
            new(
                sysconfig_param_t.SYSCFG_MAX_DUTY_CYCLE_PERCENT,
                "MAX_DUTY_CYCLE_PERCENT",
                "BPM",
                "0–1",
                "Upper clamp on the brake PWM duty cycle.",
                IsFloat: true,
                Default: 0.95,
                Min: 0.0,
                Max: 1.0
            ),
            new(
                sysconfig_param_t.SYSCFG_MAX_FORCE_LBF,
                "MAX_FORCE_LBF",
                "Force Sensor",
                "lbf",
                "Full-scale force of the load cell; scales raw ADC counts to newtons.",
                IsFloat: true,
                Default: 25.0,
                Min: 0.001,
                Max: 100000.0,
                Subsection: "All"
            ),
            new(
                sysconfig_param_t.SYSCFG_SESSIONCONTROLLER_TASK_OSDELAY,
                "SESSIONCONTROLLER_TASK_OSDELAY",
                "Session Controller",
                "ms",
                "Delay (ms) at the end of each loop pass of the task; lower = faster polling, more CPU.",
                IsFloat: false,
                Default: 10.0,
                Min: 1.0,
                Max: 60000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_BPM_TASK_OSDELAY,
                "BPM_TASK_OSDELAY",
                "BPM",
                "ms",
                "Delay (ms) at the end of each loop pass of the task; lower = faster polling, more CPU.",
                IsFloat: false,
                Default: 20.0,
                Min: 1.0,
                Max: 60000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_FORCESENSOR_TASK_OSDELAY,
                "FORCESENSOR_TASK_OSDELAY",
                "Force Sensor",
                "ms",
                "Delay (ms) at the end of each loop pass of the task; lower = faster polling, more CPU.",
                IsFloat: false,
                Default: 1.0,
                Min: 1.0,
                Max: 60000.0,
                Subsection: "All"
            ),
            new(
                sysconfig_param_t.SYSCFG_FORCESENSOR_COMMAND_POLL_OSDELAY,
                "FORCESENSOR_COMMAND_POLL_OSDELAY",
                "Force Sensor",
                "ms",
                "Bounded wait on the enable queue while the sensor is idle, so USB commands still get serviced.",
                IsFloat: false,
                Default: 50.0,
                Min: 1.0,
                Max: 60000.0,
                Subsection: "All"
            ),
            new(
                sysconfig_param_t.SYSCFG_FORCESENSOR_CONVERSION_TIMEOUT_MS,
                "FORCESENSOR_CONVERSION_TIMEOUT_MS",
                "Force Sensor",
                "ms",
                "How long to wait for the ADS1115 conversion-ready alert before abandoning the sample.",
                IsFloat: false,
                Default: 250.0,
                Min: 1.0,
                Max: 60000.0,
                Subsection: "I2C"
            ),
            new(
                sysconfig_param_t.SYSCFG_OPTICAL_ENCODER_TASK_OSDELAY,
                "OPTICAL_ENCODER_TASK_OSDELAY",
                "Optical Encoder",
                "ms",
                "Delay (ms) at the end of each loop pass of the task; lower = faster polling, more CPU.",
                IsFloat: false,
                Default: 10.0,
                Min: 1.0,
                Max: 60000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_NUM_APERTURES,
                "NUM_APERTURES",
                "Optical Encoder",
                "",
                "Apertures on the encoder wheel (tied to the physical 3D-printed apparatus).",
                IsFloat: false,
                Default: 64.0,
                Min: 1.0,
                Max: 100000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_PID_TASK_OSDELAY,
                "PID_TASK_OSDELAY",
                "PID Controller",
                "ms",
                "Delay (ms) at the end of each loop pass of the task; lower = faster polling, more CPU.",
                IsFloat: false,
                Default: 10.0,
                Min: 1.0,
                Max: 60000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_USB_TASK_OSDELAY,
                "USB_TASK_OSDELAY",
                "USB",
                "ms",
                "Delay (ms) at the end of each loop pass of the task; lower = faster polling, more CPU.",
                IsFloat: false,
                Default: 2.0,
                Min: 1.0,
                Max: 60000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_USB_TX_FLUSH_MAX_RETRIES,
                "USB_TX_FLUSH_MAX_RETRIES",
                "USB",
                "attempts",
                "Bounded retries when flushing a full TX buffer before dropping the batch.",
                IsFloat: false,
                Default: 20.0,
                Min: 0.0,
                Max: 1000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_LCD_TASK_OSDELAY,
                "LCD_TASK_OSDELAY",
                "LCD",
                "ms",
                "Delay (ms) at the end of each loop pass of the task; lower = faster polling, more CPU.",
                IsFloat: false,
                Default: 20.0,
                Min: 1.0,
                Max: 60000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_LED_TASK_OSDELAY,
                "LED_TASK_OSDELAY",
                "LED",
                "ms",
                "Blink half-period of the status LED task.",
                IsFloat: false,
                Default: 500.0,
                Min: 1.0,
                Max: 60000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_TASK_WARNING_RETRY_OSDELAY,
                "TASK_WARNING_RETRY_OSDELAY",
                "Tasks & Monitoring",
                "ms",
                "Back-off a task sleeps after reporting a warning before retrying.",
                IsFloat: false,
                Default: 100.0,
                Min: 1.0,
                Max: 60000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_TASK_MONITOR_TASK_OSDELAY,
                "TASK_MONITOR_TASK_OSDELAY",
                "Tasks & Monitoring",
                "ms",
                "How often the task monitor samples every task's state and free stack.",
                IsFloat: false,
                Default: 1000.0,
                Min: 1.0,
                Max: 60000.0
            ),
            new(
                sysconfig_param_t.SYSCFG_ADS1115_RATE,
                "ADS1115_RATE",
                "Force Sensor",
                "",
                "ADS1115 output data rate. Faster rates sample sooner but are noisier.",
                IsFloat: false,
                Default: 6.0,
                Min: 0.0,
                Max: 7.0,
                Subsection: "I2C",
                Options: new SysConfigEnumOption[] { new(0u, "8 SPS"), new(1u, "16 SPS"), new(2u, "32 SPS"), new(3u, "64 SPS"), new(4u, "128 SPS"), new(5u, "250 SPS"), new(6u, "475 SPS"), new(7u, "860 SPS") }
            ),
            new(
                sysconfig_param_t.SYSCFG_ADS1115_GAIN,
                "ADS1115_GAIN",
                "Force Sensor",
                "",
                "ADS1115 programmable-gain amplifier full-scale range. Must exceed the load cell's output span.",
                IsFloat: false,
                Default: 0.0,
                Min: 0.0,
                Max: 7.0,
                Subsection: "I2C",
                Options: new SysConfigEnumOption[] { new(0u, "±6.144 V"), new(1u, "±4.096 V"), new(2u, "±2.048 V"), new(3u, "±1.024 V"), new(4u, "±0.512 V"), new(5u, "±0.256 V"), new(6u, "±0.256 V (B)"), new(7u, "±0.256 V (C)") }
            ),
            new(
                sysconfig_param_t.SYSCFG_ADS1115_MUX,
                "ADS1115_MUX",
                "Force Sensor",
                "",
                "ADS1115 input multiplexer: which pin(s) the conversion measures.",
                IsFloat: false,
                Default: 4.0,
                Min: 0.0,
                Max: 7.0,
                Subsection: "I2C",
                Options: new SysConfigEnumOption[] { new(0u, "AIN0 / AIN1"), new(1u, "AIN0 / AIN3"), new(2u, "AIN1 / AIN3"), new(3u, "AIN2 / AIN3"), new(4u, "AIN0 / GND"), new(5u, "AIN1 / GND"), new(6u, "AIN2 / GND"), new(7u, "AIN3 / GND") }
            ),
            new(
                sysconfig_param_t.SYSCFG_ADS1115_MODE,
                "ADS1115_MODE",
                "Force Sensor",
                "",
                "ADS1115 conversion mode. The read loop triggers each conversion, so single-shot is expected.",
                IsFloat: false,
                Default: 1.0,
                Min: 0.0,
                Max: 1.0,
                Subsection: "I2C",
                Options: new SysConfigEnumOption[] { new(0u, "Continuous"), new(1u, "Single-shot") }
            ),
            new(
                sysconfig_param_t.SYSCFG_ADS1115_COMP_MODE,
                "ADS1115_COMP_MODE",
                "Force Sensor",
                "",
                "ADS1115 comparator mode (drives the ALERT/RDY pin used as conversion-ready).",
                IsFloat: false,
                Default: 0.0,
                Min: 0.0,
                Max: 1.0,
                Subsection: "I2C",
                Options: new SysConfigEnumOption[] { new(0u, "Traditional"), new(1u, "Window") }
            ),
            new(
                sysconfig_param_t.SYSCFG_ADS1115_COMP_POL,
                "ADS1115_COMP_POL",
                "Force Sensor",
                "",
                "ADS1115 ALERT/RDY pin active polarity.",
                IsFloat: false,
                Default: 0.0,
                Min: 0.0,
                Max: 1.0,
                Subsection: "I2C",
                Options: new SysConfigEnumOption[] { new(0u, "Active low"), new(1u, "Active high") }
            ),
            new(
                sysconfig_param_t.SYSCFG_ADS1115_COMP_LAT,
                "ADS1115_COMP_LAT",
                "Force Sensor",
                "",
                "ADS1115 comparator latching of the ALERT/RDY pin.",
                IsFloat: false,
                Default: 0.0,
                Min: 0.0,
                Max: 1.0,
                Subsection: "I2C",
                Options: new SysConfigEnumOption[] { new(0u, "Non-latching"), new(1u, "Latching") }
            ),
            new(
                sysconfig_param_t.SYSCFG_ADS1115_COMP_QUE,
                "ADS1115_COMP_QUE",
                "Force Sensor",
                "",
                "ADS1115 comparator queue: conversions before ALERT asserts (or disable the comparator).",
                IsFloat: false,
                Default: 3.0,
                Min: 0.0,
                Max: 3.0,
                Subsection: "I2C",
                Options: new SysConfigEnumOption[] { new(0u, "Assert after 1"), new(1u, "Assert after 2"), new(2u, "Assert after 4"), new(3u, "Disabled") }
            ),
        };

    /// <summary>Looks up a parameter's definition by wire id.</summary>
    public static SysConfigParameterDef Get(sysconfig_param_t id) => Parameters[(int)id];
}
