using Dyno.Core.Messages;

namespace Dyno.Core.Protocol;

/// <summary>A task error/warning unpacked from the 32-bit packed <c>error_code</c>.</summary>
/// <param name="Task">Producing task (the high 16 bits).</param>
/// <param name="Number">Task-local error number (low 15 bits).</param>
/// <param name="IsWarning">True when the warning flag (bit 15) is set.</param>
/// <param name="Raw">The original packed code.</param>
public readonly record struct DecodedError(
    task_offset_t Task,
    uint Number,
    bool IsWarning,
    uint Raw
);

/// <summary>Decodes the packed <c>error_code</c> per the scheme documented in the schema.</summary>
public static class ErrorDecoder
{
    public static DecodedError Decode(uint errorCode) =>
        new(
            Task: (task_offset_t)(errorCode & MessageConstants.TASK_OFFSET_MASK),
            Number: errorCode & MessageConstants.TASK_ERROR_NUM_MASK,
            IsWarning: (errorCode & MessageConstants.WARNING_FLAG) != 0,
            Raw: errorCode
        );

    /// <summary>
    /// Which task's generated enum gives meaning to an error number. The number alone says
    /// nothing — it is task-local, so <c>#1</c> is a different fault for every task — which is why
    /// naming has to go through the offset rather than through a single flat table.
    /// </summary>
    /// <remarks>Tasks absent here have no faults defined in the schema, so there is no enum to map
    /// to; <see cref="Name"/> returns null for them and the caller falls back to the number.
    /// <c>ErrorDecoderTests</c> holds this map to every <c>*_error_ids</c> enum in the generated
    /// file, so a task gaining its first fault code fails a test rather than silently printing
    /// numbers.</remarks>
    private static readonly Dictionary<task_offset_t, Type> FaultEnums = new()
    {
        [task_offset_t.TASK_OFFSET_TASK_MONITOR] = typeof(task_monitor_task_error_ids),
        [task_offset_t.TASK_OFFSET_SESSION_CONTROLLER] = typeof(session_controller_task_error_ids),
        [task_offset_t.TASK_OFFSET_USB_CONTROLLER] = typeof(usb_controller_task_error_ids),
        [task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADC] = typeof(force_sensor_adc_task_error_ids),
        [task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115] = typeof(force_sensor_ads1115_error_ids),
        [task_offset_t.TASK_OFFSET_BPM_CONTROLLER] = typeof(bpm_task_error_ids),
        [task_offset_t.TASK_OFFSET_PID_CONTROLLER] = typeof(pid_controller_task_error_ids),
        [task_offset_t.TASK_OFFSET_LUMEX_LCD] = typeof(lumex_lcd_task_error_ids),
    };

    /// <summary>
    /// The schema's name for a fault, without its <c>ERROR_</c>/<c>WARNING_</c> prefix, or null
    /// when this build has no name for the code. Kept as the source identifier rather than prose
    /// so a line in the event log can be grepped straight back to the firmware that emitted it;
    /// the severity prefix is dropped because the log already marks the line ERR or WARN.
    /// </summary>
    public static string? Name(DecodedError error)
    {
        if (!FaultEnums.TryGetValue(error.Task, out var faults))
        {
            return null;
        }
        // The enum values carry the warning flag, so it has to go back on before the lookup.
        var value = error.Number | (error.IsWarning ? MessageConstants.WARNING_FLAG : 0u);
        var name = Enum.GetName(faults, value);
        return name is null ? null
            : name.StartsWith("WARNING_", StringComparison.Ordinal) ? name["WARNING_".Length..]
            : name.StartsWith("ERROR_", StringComparison.Ordinal) ? name["ERROR_".Length..]
            : name;
    }
}
