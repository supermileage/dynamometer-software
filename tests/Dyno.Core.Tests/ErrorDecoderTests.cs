using Dyno.Core.Messages;
using Dyno.Core.Protocol;
using Xunit;

namespace Dyno.Core.Tests;

public class ErrorDecoderTests
{
    [Fact]
    public void Decode_Warning_SetsFlagAndTaskAndNumber()
    {
        uint code =
            (uint)task_offset_t.TASK_OFFSET_PID_CONTROLLER
            | (uint)pid_controller_task_error_ids.WARNING_PID_CONTROLLER_MESSAGE_QUEUE_FULL;

        var decoded = ErrorDecoder.Decode(code);

        Assert.Equal(task_offset_t.TASK_OFFSET_PID_CONTROLLER, decoded.Task);
        Assert.True(decoded.IsWarning);
        Assert.Equal(0u, decoded.Number); // the warning enum is just the flag; no local number
        Assert.Equal(code, decoded.Raw);
    }

    [Fact]
    public void Decode_Error_ClearsWarningFlagAndKeepsNumber()
    {
        uint code =
            (uint)task_offset_t.TASK_OFFSET_BPM_CONTROLLER
            | (uint)bpm_task_error_ids.ERROR_BPM_PWM_STOP_FAILURE;

        var decoded = ErrorDecoder.Decode(code);

        Assert.Equal(task_offset_t.TASK_OFFSET_BPM_CONTROLLER, decoded.Task);
        Assert.False(decoded.IsWarning);
        Assert.Equal(1u, decoded.Number); // ERROR_BPM_PWM_STOP_FAILURE == 1
    }
}
