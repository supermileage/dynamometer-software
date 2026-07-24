// AUTO-GENERATED from tools/message_gen/schema/messages_public.yaml by tools/message_gen/error_msg_generate.py -- DO NOT EDIT.
// Every error and warning the firmware can report, with the sentence the app shows for it.
// Both the code and its description come from that schema in the firmware/ tree: add a
// fault there, with its `description:`, then run tools/message_gen/error_msg_generate.py
// (CI verifies the committed file matches).

namespace Dyno.Core.Messages;

/// <summary>One fault the firmware can report.</summary>
/// <param name="Code">The packed 32-bit code as it arrives from the board: task offset,
/// warning flag, and the task-local number. Unique across every task, which is what makes
/// it the lookup key -- the number alone is reused by every task that defines one.</param>
/// <param name="Task">The task that reports it.</param>
/// <param name="Name">The schema's name for it, without its <c>ERROR_</c>/<c>WARNING_</c>
/// prefix. Kept as the source identifier rather than turned into prose so a line in the
/// event log greps straight back to the firmware that emitted it.</param>
/// <param name="IsWarning">True when the warning flag is set: something was lost or
/// retried, but the board carries on. False means a task gave up.</param>
/// <param name="Description">What happened and what it means for the run, written for
/// whoever is looking at the rig.</param>
public readonly record struct ErrorMessageDef(
    uint Code,
    task_offset_t Task,
    string Name,
    bool IsWarning,
    string Description
);

/// <summary>Every fault this firmware contract defines, in schema order.</summary>
public static class ErrorCatalog
{
    public static IReadOnlyList<ErrorMessageDef> All { get; } =
        new ErrorMessageDef[]
        {
            // session_controller_task_error_ids.ERROR_SESSION_CONTROLLER_TIMESTAMP_TIMER_START_FAILURE
            new(
                0x10000u,
                task_offset_t.TASK_OFFSET_SESSION_CONTROLLER,
                "SESSION_CONTROLLER_TIMESTAMP_TIMER_START_FAILURE",
                false,
                "the hardware timer every sample is stamped from would not start, so the session controller parked itself at boot — no session can run and nothing is timestamped. Reported once per boot; only a reset can retry it"
            ),
            // session_controller_task_error_ids.ERROR_SESSION_CONTROLLER_INVALID_TASK_QUEUE_POINTER
            new(
                0x10001u,
                task_offset_t.TASK_OFFSET_SESSION_CONTROLLER,
                "SESSION_CONTROLLER_INVALID_TASK_QUEUE_POINTER",
                false,
                "one of the queues the session controller drives its tasks through was missing at startup, so it parked itself and commands from this app reach nothing. A firmware build fault, not something a run can cause"
            ),
            // session_controller_task_error_ids.ERROR_SESSION_CONTROLLER_INVALID_UART1_MUTEX_POINTER
            new(
                0x10002u,
                task_offset_t.TASK_OFFSET_SESSION_CONTROLLER,
                "SESSION_CONTROLLER_INVALID_UART1_MUTEX_POINTER",
                false,
                "the UART1 mutex was missing at startup and the session controller parked itself. A firmware build fault, not something a run can cause"
            ),
            // bpm_task_error_ids.ERROR_BPM_PWM_START_FAILURE
            new(
                0x70000u,
                task_offset_t.TASK_OFFSET_BPM_CONTROLLER,
                "BPM_PWM_START_FAILURE",
                false,
                "the brake's PWM timer refused to start, so the brake did not engage when the session asked for it. The task parks itself rather than keep driving blind, so the brake stays dead until the board is reset"
            ),
            // bpm_task_error_ids.ERROR_BPM_PWM_STOP_FAILURE
            new(
                0x70001u,
                task_offset_t.TASK_OFFSET_BPM_CONTROLLER,
                "BPM_PWM_STOP_FAILURE",
                false,
                "the brake's PWM timer refused to stop, so the brake may still be driven after the session ended. The task parks itself; treat the brake as live until the board is reset"
            ),
            // lumex_lcd_task_error_ids.ERROR_LUMEX_LCD_TIMER_START_FAILURE
            new(
                0x90000u,
                task_offset_t.TASK_OFFSET_LUMEX_LCD,
                "LUMEX_LCD_TIMER_START_FAILURE",
                false,
                "the timer that clocks the on-board LCD would not start, so the display is blank or frozen. Nothing streamed to this app is affected — only the readout on the rig itself"
            ),
            // task_monitor_task_error_ids.ERROR_TASK_MONITOR_INVALID_THREAD_ID_POINTER
            new(
                0x0u,
                task_offset_t.TASK_OFFSET_TASK_MONITOR,
                "TASK_MONITOR_INVALID_THREAD_ID_POINTER",
                false,
                "the task monitor was handed a missing thread handle at startup and parked itself, so the per-task stack and state table stays empty for this session. A firmware build fault; the rest of the board runs normally"
            ),
            // pid_controller_task_error_ids.WARNING_PID_CONTROLLER_MESSAGE_QUEUE_FULL
            new(
                0x88000u,
                task_offset_t.TASK_OFFSET_PID_CONTROLLER,
                "PID_CONTROLLER_MESSAGE_QUEUE_FULL",
                true,
                "the PID controller could not hand its new duty cycle to the brake task, whose queue was full, so the brake is still running on the previous value. It backs off and retries; a steady run of these means the brake task is not keeping up"
            ),
            // usb_controller_task_error_ids.WARNING_USB_TX_BATCH_DROPPED
            new(
                0x28000u,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                "USB_TX_BATCH_DROPPED",
                true,
                "the board gave up flushing a batch of telemetry and threw those samples away — its USB transmit path is saturated. Reported at most once a second while it is happening, so one line can stand for many lost batches"
            ),
            // usb_controller_task_error_ids.WARNING_USB_OPTICAL_ENCODER_BUFFER_OVERFLOW
            new(
                0x28001u,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                "USB_OPTICAL_ENCODER_BUFFER_OVERFLOW",
                true,
                "the optical encoder filled its buffer faster than the USB task drained it, so encoder samples were overwritten before they could be sent and this run has a gap in speed and acceleration"
            ),
            // usb_controller_task_error_ids.WARNING_USB_FORCE_SENSOR_BUFFER_OVERFLOW
            new(
                0x28002u,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                "USB_FORCE_SENSOR_BUFFER_OVERFLOW",
                true,
                "the force sensor filled its buffer faster than the USB task drained it, so force samples were overwritten before they could be sent and this run has a gap in force and torque"
            ),
            // usb_controller_task_error_ids.WARNING_USB_BPM_BUFFER_OVERFLOW
            new(
                0x28003u,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                "USB_BPM_BUFFER_OVERFLOW",
                true,
                "the brake task filled its buffer faster than the USB task drained it, so duty-cycle samples were overwritten before they could be sent and this run has a gap in what the brake was doing"
            ),
            // usb_controller_task_error_ids.WARNING_USB_TASK_ERROR_BUFFER_OVERFLOW
            new(
                0x28004u,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                "USB_TASK_ERROR_BUFFER_OVERFLOW",
                true,
                "faults were reported faster than the USB task could send them and the oldest were overwritten, so some errors and warnings from this board are missing from this log entirely — most likely the ones from before this app connected"
            ),
            // force_sensor_adc_task_error_ids.ERROR_FORCE_SENSOR_ADC_START_FAILURE
            new(
                0x50000u,
                task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADC,
                "FORCE_SENSOR_ADC_START_FAILURE",
                false,
                "the on-chip ADC would not start a conversion, so the force sensor task stopped reading. Force telemetry ends at this timestamp and does not come back without a reset"
            ),
            // force_sensor_ads1115_error_ids.ERROR_FORCE_SENSOR_ADS1115_INIT_FAILURE
            new(
                0x60000u,
                task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
                "FORCE_SENSOR_ADS1115_INIT_FAILURE",
                false,
                "the ADS1115 force sensor did not answer over I2C at startup — most often it is simply not plugged in — so the task parked itself. Reported once per boot: connecting the sensor afterwards will not bring it back, the board has to be reset with it attached"
            ),
            // force_sensor_ads1115_error_ids.WARNING_FORCE_SENSOR_ADS1115_TRIGGER_CONVERSION_FAILURE
            new(
                0x68000u,
                task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
                "FORCE_SENSOR_ADS1115_TRIGGER_CONVERSION_FAILURE",
                true,
                "the ADS1115 did not accept the command to start a conversion, so that reading is lost. The task backs off and retries; a steady run of these points at the I2C wiring"
            ),
            // force_sensor_ads1115_error_ids.WARNING_FORCE_SENSOR_ADS1115_GET_CONVERSION_FAILURE
            new(
                0x68001u,
                task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
                "FORCE_SENSOR_ADS1115_GET_CONVERSION_FAILURE",
                true,
                "the ADS1115 either never signalled its conversion ready or would not hand the result over, so that reading is lost. The task backs off and retries"
            ),
            // force_sensor_ads1115_error_ids.WARNING_FORCE_SENSOR_ADS1115_CONFIG_FAILURE
            new(
                0x68002u,
                task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
                "FORCE_SENSOR_ADS1115_CONFIG_FAILURE",
                true,
                "a gain, sample-rate or mode change could not be written to the ADS1115, so it is still sampling on its previous settings and the force readings do not yet reflect what the Config page shows. The task backs off and retries"
            ),
        };

    private static readonly Dictionary<uint, ErrorMessageDef> ByCode = All.ToDictionary(f =>
        f.Code
    );

    /// <summary>The catalog entry for a packed error code, or null when this build has
    /// never heard of it -- a board running newer firmware than the app. Callers fall back
    /// to printing the raw number, which is still true and still greppable.</summary>
    public static ErrorMessageDef? Find(uint code) =>
        ByCode.TryGetValue(code, out var def) ? def : null;
}
