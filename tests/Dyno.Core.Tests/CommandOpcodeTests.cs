using Dyno.Core.Messages;
using Dyno.Core.Protocol;
using Xunit;

namespace Dyno.Core.Tests;

public class CommandOpcodeTests
{
    [Fact]
    public void OpcodesAreNamedPerTask()
    {
        // The same number means different things to different tasks — which is the whole reason a
        // bare "opcode = 1" in a log tells a reader nothing.
        Assert.Equal(
            "USB_CMD_SET_SYSCONFIG",
            CommandOpcodes.Name(task_offset_t.TASK_OFFSET_USB_CONTROLLER, 1)
        );
        Assert.Equal(
            "FORCE_SENSOR_CMD_SET_DATA_RATE",
            CommandOpcodes.Name(task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115, 0)
        );
        Assert.Equal(
            "USB_CMD_ACK",
            CommandOpcodes.Name(task_offset_t.TASK_OFFSET_USB_CONTROLLER, 0)
        );
    }

    [Fact]
    public void AnUnknownOpcodeFallsBackToItsNumber()
    {
        // A firmware newer than this host, or a frame that isn't really a frame: say what we have
        // rather than invent a name.
        Assert.Equal(
            "opcode 99",
            CommandOpcodes.Name(task_offset_t.TASK_OFFSET_USB_CONTROLLER, 99)
        );
        Assert.Equal("opcode 1", CommandOpcodes.Name(task_offset_t.TASK_OFFSET_LUMEX_LCD, 1));
    }

    [Fact]
    public void EveryCommandEnumIsAccountedFor()
    {
        // CommandOpcodes.Name has to map task → opcode enum by hand, because the schema records
        // that pairing only in prose. This is the tripwire: give a task commands and the mapping
        // will not learn about it on its own, so fail here rather than print "opcode 3" forever.
        var commandEnums = typeof(usb_controller_command_t)
            .Assembly.GetTypes()
            .Where(t => t.IsEnum && t.Name.Contains("command", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["force_sensor_command_opcode", "usb_controller_command_t"], commandEnums);
    }
}
