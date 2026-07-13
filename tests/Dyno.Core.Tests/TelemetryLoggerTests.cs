using System.Globalization;
using Dyno.Core;
using Dyno.Core.Messages;
using Dyno.Core.Protocol;
using Xunit;

namespace Dyno.Core.Tests;

public class TelemetryLoggerTests
{
    private static (TelemetryLogger logger, StringWriter sink) NewLogger()
    {
        var sink = new StringWriter { NewLine = "\n" };
        return (new TelemetryLogger(sink), sink);
    }

    private static string[] Lines(StringWriter sink) =>
        sink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void WritesHeader_OnConstruction()
    {
        var (_, sink) = NewLogger();
        Assert.Equal(TelemetryLogger.Header, Assert.Single(Lines(sink)));
    }

    [Fact]
    public void WritesRow_PerMeasurementSample_InTheRightColumns()
    {
        var (logger, sink) = NewLogger();

        logger.Log(
            new OpticalEncoderSample(
                new optical_encoder_output_data
                {
                    timestamp = 100,
                    angular_velocity = 12.5f,
                    angular_acceleration = -0.25f,
                    raw_value = 7,
                }
            )
        );
        logger.Log(
            new ForceSensorSample(
                task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115,
                new forcesensor_output_data
                {
                    timestamp = 101,
                    force = 42.0f,
                    raw_value = 9,
                }
            )
        );
        logger.Log(
            new BpmSample(
                new bpm_output_data
                {
                    timestamp = 102,
                    duty_cycle = 0.75f,
                    raw_value = 0,
                }
            )
        );

        string[] lines = Lines(sink);
        Assert.Equal(4, lines.Length); // header + three rows

        // header order: host_time,device_ts,source,angular_velocity,angular_acceleration,force,duty_cycle,raw_value
        var encoder = lines[1].Split(',');
        Assert.Equal("100", encoder[1]);
        Assert.Equal("OPTICAL_ENCODER", encoder[2]);
        Assert.Equal(12.5f.ToString("R", CultureInfo.InvariantCulture), encoder[3]);
        Assert.Equal((-0.25f).ToString("R", CultureInfo.InvariantCulture), encoder[4]);
        Assert.Equal("", encoder[5]); // force blank
        Assert.Equal("", encoder[6]); // duty blank
        Assert.Equal("7", encoder[7]);

        var force = lines[2].Split(',');
        Assert.Equal("FORCE_SENSOR_ADS1115", force[2]);
        Assert.Equal("", force[3]); // velocity blank
        Assert.Equal(42.0f.ToString("R", CultureInfo.InvariantCulture), force[5]);

        var bpm = lines[3].Split(',');
        Assert.Equal("BPM_CONTROLLER", bpm[2]);
        Assert.Equal(0.75f.ToString("R", CultureInfo.InvariantCulture), bpm[6]);
    }

    [Fact]
    public void Ignores_NonMeasurementMessages()
    {
        var (logger, sink) = NewLogger();

        logger.Log(
            new TaskMonitorSample(
                new task_monitor_output_data
                {
                    timestamp = 1,
                    task_offset = task_offset_t.TASK_OFFSET_BPM_CONTROLLER,
                    task_state = 2,
                    free_bytes = 512,
                }
            )
        );
        logger.Log(
            new DeviceFault(new DecodedError(task_offset_t.TASK_OFFSET_NO_TASK, 3, false, 3), 5)
        );
        logger.Log(
            new UnknownMessage(
                new usb_msg_header_t
                {
                    msg_type = usb_msg_type_t.USB_MSG_STATUS,
                    task_offset = task_offset_t.TASK_OFFSET_TASK_MONITOR,
                    payload_len = 0,
                },
                []
            )
        );

        // Only the header was written; none of the above are measurement data.
        Assert.Equal(TelemetryLogger.Header, Assert.Single(Lines(sink)));
    }
}
