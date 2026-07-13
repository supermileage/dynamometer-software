namespace Dyno.Core;

/// <summary>
/// The state of a firmware task, as reported in <c>task_monitor_output_data.task_state</c>.
/// <para>
/// The backing values are CMSIS-RTOS2's <c>osThreadState_t</c> — the firmware's TaskMonitor sends
/// <c>osThreadGetState()</c> cast to an int. They are <b>not</b> FreeRTOS's <c>eTaskState</c>,
/// which numbers its states differently (there, 0 is Running, not Inactive), so decoding one as
/// the other silently mislabels every row. The values are fixed by the CMSIS-RTOS2 API rather than
/// by firmware policy, which is what makes it safe to name them host-side.
/// </para>
/// </summary>
public enum ThreadState
{
    Error = -1,
    Inactive = 0,
    Ready = 1,
    Running = 2,
    Blocked = 3,
    Terminated = 4,
}

public static class ThreadStateExtensions
{
    /// <summary>
    /// Human-readable label for a raw <c>task_state</c>. A value outside the enum is shown as-is
    /// (e.g. <c>"? (7)"</c>) rather than guessed at: an unknown state most likely means the
    /// firmware's RTOS reports something this host does not know about, and inventing a name for it
    /// would hide exactly that.
    /// </summary>
    public static string ToLabel(int state) =>
        Enum.IsDefined(typeof(ThreadState), state)
            ? ((ThreadState)state).ToString()
            : $"? ({state})";
}
