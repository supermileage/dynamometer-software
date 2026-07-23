using System.Reflection;
using Dyno.Core.Messages;
using Dyno.Core.Protocol;
using Xunit;

namespace Dyno.Core.Tests;

public class ErrorDecoderTests
{
    /// <summary>Every fault the firmware can send must come out of the decoder with its schema
    /// name. Driven off the generated enums rather than a list kept here, so a code added to the
    /// YAML is covered the moment it is generated — the failure this guards against is a new fault
    /// reaching the event log as a bare number nobody can look up.</summary>
    [Theory]
    [MemberData(nameof(EveryFaultCode))]
    public void Name_ResolvesEveryGeneratedFaultCode(task_offset_t task, uint code, string expected)
    {
        var decoded = ErrorDecoder.Decode((uint)task | code);

        Assert.Equal(expected, ErrorDecoder.Name(decoded));
    }

    [Fact]
    public void Name_IsNullForACodeThisBuildDoesNotKnow()
    {
        // A board running newer firmware than the app: the number is real, the name is not here.
        var decoded = ErrorDecoder.Decode((uint)task_offset_t.TASK_OFFSET_BPM_CONTROLLER | 4999u);

        Assert.Null(ErrorDecoder.Name(decoded));
    }

    /// <summary>(task offset, packed code, expected name) for every value of every
    /// <c>*_error_ids</c> enum in the generated message contract.</summary>
    public static TheoryData<task_offset_t, uint, string> EveryFaultCode()
    {
        var data = new TheoryData<task_offset_t, uint, string>();
        var faultEnums = typeof(MessageConstants)
            .Assembly.GetTypes()
            .Where(t => t.IsEnum && t.Name.EndsWith("error_ids", StringComparison.Ordinal));

        foreach (var faults in faultEnums)
        {
            // The enum's task is the one whose name prefixes it: bpm_task_error_ids ->
            // TASK_OFFSET_BPM_CONTROLLER. Matched on the longest task name the enum starts with,
            // so FORCE_SENSOR_ADS1115 is not claimed by a shorter FORCE_SENSOR.
            var stem = faults.Name.Replace("_task_error_ids", "").Replace("_error_ids", "");
            var task = Enum.GetValues<task_offset_t>()
                .Where(t =>
                    stem.StartsWith(
                        Friendly(t).ToLowerInvariant().Replace("_controller", ""),
                        StringComparison.Ordinal
                    )
                )
                .OrderByDescending(t => Friendly(t).Length)
                .First();

            foreach (var name in Enum.GetNames(faults))
            {
                var code = (uint)Convert.ChangeType(Enum.Parse(faults, name), typeof(uint))!;
                var expected = name.StartsWith("WARNING_", StringComparison.Ordinal)
                    ? name["WARNING_".Length..]
                    : name["ERROR_".Length..];
                data.Add(task, code, expected);
            }
        }
        return data;
    }

    private static string Friendly(task_offset_t offset) =>
        offset.ToString().Replace("TASK_OFFSET_", string.Empty);

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
