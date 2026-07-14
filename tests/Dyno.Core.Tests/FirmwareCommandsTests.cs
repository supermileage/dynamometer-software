using Dyno.Core.Firmware;
using Xunit;

namespace Dyno.Core.Tests;

public class FirmwareCommandsTests
{
    private const string Dir = "/repo/firmware";

    private static string Line(ProcessCommand command) => command.DisplayLine;

    [Fact]
    public void Build_RunsTheDockerScript_ForTheChosenConfiguration()
    {
        var command = FirmwareCommands.Build(Dir, FirmwareBuild.Debug, false, windows: false);

        Assert.Equal("bash", command.FileName);
        Assert.Equal(Dir, command.WorkingDirectory);
        Assert.Equal(["Scripts/build-docker.sh", "Debug"], command.Arguments);
    }

    [Fact]
    public void Build_OnlyRebuildsTheImageWhenAsked()
    {
        Assert.DoesNotContain(
            "--rebuild",
            FirmwareCommands.Build(Dir, FirmwareBuild.Release, false, windows: false).Arguments
        );
        Assert.Contains(
            "--rebuild",
            FirmwareCommands.Build(Dir, FirmwareBuild.Release, true, windows: false).Arguments
        );
    }

    [Fact]
    public void Build_OnWindows_DrivesThePowerShellScript()
    {
        var command = FirmwareCommands.Build(Dir, FirmwareBuild.Release, true, windows: true);

        Assert.Equal("powershell", command.FileName);
        Assert.Contains(Path.Combine("Scripts", "build-docker.ps1"), command.Arguments);
        Assert.Contains("-Rebuild", command.Arguments);
        // Nothing on a user's machine should be able to stop the build: the scripts are ours.
        Assert.Contains("Bypass", command.Arguments);
    }

    [Fact]
    public void EachMethodOffersOnlyTheToolsThatCanDriveIt()
    {
        Assert.Equal(
            ["st-flash", "openocd", "cubeprog"],
            FirmwareCommands.ToolsFor(FlashMethod.Swd)
        );
        Assert.Equal(["dfu-util", "cubeprog"], FirmwareCommands.ToolsFor(FlashMethod.Dfu));
        Assert.Equal(["stm32flash", "cubeprog"], FirmwareCommands.ToolsFor(FlashMethod.Uart));
    }

    [Fact]
    public void TheOpenSourceToolIsOfferedFirst_ForEveryMethod()
    {
        // cubeprog is the only tool here that needs an ST account, so it is never the default.
        Assert.All(
            Enum.GetValues<FlashMethod>(),
            method => Assert.NotEqual("cubeprog", FirmwareCommands.ToolsFor(method)[0])
        );
    }

    [Fact]
    public void OnlySwdSkipsTheBootloader()
    {
        Assert.False(FirmwareCommands.NeedsBootloader(FlashMethod.Swd));
        Assert.True(FirmwareCommands.NeedsBootloader(FlashMethod.Dfu));
        Assert.True(FirmwareCommands.NeedsBootloader(FlashMethod.Uart));
    }

    [Fact]
    public void Flash_NamesTheBuild_TheMethodAndTheTool()
    {
        var command = FirmwareCommands.Flash(
            Dir,
            new FlashRequest(FlashMethod.Swd, "st-flash", FirmwareBuild.Debug),
            windows: false
        );

        Assert.Equal("bash Scripts/flash.sh Debug swd --tool st-flash", Line(command));
    }

    [Fact]
    public void Flash_PassesTheSerial_ToTheMethodsThatSelectByIt()
    {
        Assert.Contains(
            "--serial 0670FF",
            Line(
                FirmwareCommands.Flash(
                    Dir,
                    new FlashRequest(FlashMethod.Swd, "st-flash", Serial: "0670FF"),
                    windows: false
                )
            )
        );
        Assert.Contains(
            "--serial 0670FF",
            Line(
                FirmwareCommands.Flash(
                    Dir,
                    new FlashRequest(FlashMethod.Dfu, "dfu-util", Serial: "0670FF"),
                    windows: false
                )
            )
        );
    }

    [Fact]
    public void Flash_PassesOnlyWhatTheChosenCombinationReads()
    {
        // A port means nothing to an SWD flash and a serial means nothing to a UART one. The script
        // would ignore them; passing them anyway would leave the user believing they took effect.
        var swd = Line(
            FirmwareCommands.Flash(
                Dir,
                new FlashRequest(
                    FlashMethod.Swd,
                    "st-flash",
                    Serial: "0670FF",
                    Port: "/dev/ttyUSB0",
                    Baud: "9600",
                    Index: "2"
                ),
                windows: false
            )
        );
        Assert.DoesNotContain("--port", swd);
        Assert.DoesNotContain("--baud", swd);
        Assert.DoesNotContain("--index", swd);

        var uart = Line(
            FirmwareCommands.Flash(
                Dir,
                new FlashRequest(
                    FlashMethod.Uart,
                    "stm32flash",
                    Serial: "0670FF",
                    Port: "/dev/ttyUSB1",
                    Baud: "57600"
                ),
                windows: false
            )
        );
        Assert.Contains("--port /dev/ttyUSB1 --baud 57600", uart);
        Assert.DoesNotContain("--serial", uart);
    }

    [Fact]
    public void Flash_PassesTheDfuIndex_OnlyToTheToolThatHasTheNotion()
    {
        // port=USB<n> is STM32CubeProgrammer's way of picking a DFU device; dfu-util has no index
        // at all and selects by serial.
        Assert.Contains(
            "--index 2",
            Line(
                FirmwareCommands.Flash(
                    Dir,
                    new FlashRequest(FlashMethod.Dfu, "cubeprog", Index: "2"),
                    windows: false
                )
            )
        );
        Assert.DoesNotContain(
            "--index",
            Line(
                FirmwareCommands.Flash(
                    Dir,
                    new FlashRequest(FlashMethod.Dfu, "dfu-util", Index: "2"),
                    windows: false
                )
            )
        );
    }

    [Fact]
    public void Flash_RefusesAToolThatCannotDriveTheMethod()
    {
        // The script would refuse it too, but only after the user pressed Flash on a board they may
        // have just put into the bootloader by hand.
        Assert.Throws<ArgumentException>(() =>
            FirmwareCommands.Flash(
                Dir,
                new FlashRequest(FlashMethod.Dfu, "st-flash"),
                windows: false
            )
        );
    }

    [Fact]
    public void Flash_OnWindows_UsesTheScriptsNamedParameters()
    {
        var command = FirmwareCommands.Flash(
            Dir,
            new FlashRequest(FlashMethod.Uart, "stm32flash", Port: "COM5", Baud: "115200"),
            windows: true
        );

        Assert.Equal("powershell", command.FileName);
        Assert.Contains("-Method uart -Tool stm32flash -Port COM5 -Baud 115200", Line(command));
    }

    [Fact]
    public void Scan_AsksTheChosenToolWhatItCanSee()
    {
        Assert.Equal(
            "bash Scripts/flash.sh dfu --tool dfu-util --list",
            Line(FirmwareCommands.ListDevices(Dir, FlashMethod.Dfu, "dfu-util", windows: false))
        );
    }

    [Fact]
    public void Scan_ForUart_ListsPortsWithoutATool()
    {
        // Serial ports are the OS's business, not a flashing tool's — the script just enumerates
        // them, and passing a tool would be answering a question nobody asked.
        Assert.Equal(
            "bash Scripts/flash.sh uart --list",
            Line(FirmwareCommands.ListDevices(Dir, FlashMethod.Uart, "stm32flash", windows: false))
        );
    }
}
