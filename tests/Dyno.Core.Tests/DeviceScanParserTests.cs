using Dyno.Core.Firmware;
using Xunit;

namespace Dyno.Core.Tests;

public class DeviceScanParserTests
{
    private static IReadOnlyList<FlashTarget> Parse(
        FlashMethod method,
        string tool,
        string output
    ) =>
        DeviceScanParser.Parse(
            method,
            tool,
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        );

    [Fact]
    public void StFlash_PicksTheProbeSerial_PastTheNoise()
    {
        // Verbatim from st-info --probe on the ST-Link attached to this machine, "Failed to enter
        // SWD mode" line and all.
        var target = Assert.Single(
            Parse(
                FlashMethod.Swd,
                "st-flash",
                """
                Failed to enter SWD mode
                Found 1 stlink programmers
                  version:    V2J46S7
                  serial:     B55B5A1A000000008CA4F301
                  flash:      0 (pagesize: 0)
                  sram:       0
                """
            )
        );

        Assert.Equal(FlashTargetField.Serial, target.Field);
        Assert.Equal("B55B5A1A000000008CA4F301", target.Value);
        Assert.Contains("V2J46S7", target.Label); // the version rides along as a label
        // hla-serial (if present) must not be mistaken for the serial.
    }

    [Fact]
    public void StFlash_DoesNotMistakeHlaSerialForTheSerial()
    {
        var target = Assert.Single(
            Parse(
                FlashMethod.Swd,
                "st-flash",
                """
                Found 1 stlink programmers
                  serial:     066FFF303435554157105348
                  hla-serial: "\x06\x6f..."
                  flash:      2097152
                """
            )
        );
        Assert.Equal("066FFF303435554157105348", target.Value);
    }

    [Fact]
    public void StFlash_FindsEveryProbe_WhenSeveralAreAttached()
    {
        var targets = Parse(
            FlashMethod.Swd,
            "st-flash",
            """
            Found 2 stlink programmers
              version:    V2J46S7
              serial:     AAAA
              version:    V3J13
              serial:     BBBB
            """
        );
        Assert.Equal(["AAAA", "BBBB"], targets.Select(t => t.Value));
    }

    [Fact]
    public void CubeProg_Swd_ReadsTheStLinkSn()
    {
        var target = Assert.Single(
            Parse(
                FlashMethod.Swd,
                "cubeprog",
                """
                -------- Connected ST-LINK Probes List --------
                ST-Link Probe 0 :
                   ST-LINK SN  : 0670FF485550755187121723
                   ST-LINK FW  : V2J39M27
                -----------------------------------------------
                """
            )
        );
        Assert.Equal(FlashTargetField.Serial, target.Field);
        Assert.Equal("0670FF485550755187121723", target.Value);
    }

    [Fact]
    public void DfuUtil_TakesOneSerialPerDevice_NotOnePerAltSetting()
    {
        // dfu-util lists every alt setting; they share a serial and are one board.
        var targets = Parse(
            FlashMethod.Dfu,
            "dfu-util",
            """
            Found DFU: [0483:df11] ver=2200, devnum=4, cfg=1, intf=0, alt=0, name="@Internal Flash", serial="200364500000"
            Found DFU: [0483:df11] ver=2200, devnum=4, cfg=1, intf=0, alt=1, name="@Option Bytes", serial="200364500000"
            """
        );
        var target = Assert.Single(targets);
        Assert.Equal(FlashTargetField.Serial, target.Field);
        Assert.Equal("200364500000", target.Value);
    }

    [Fact]
    public void CubeProg_Dfu_SelectsByIndex_AndShowsTheSerial()
    {
        var target = Assert.Single(
            Parse(
                FlashMethod.Dfu,
                "cubeprog",
                """
                =====  DFU Interface   =====
                Device Index           : USB1
                USB Bus Number         : 1
                Product ID             : 0xDF11
                Serial number          : 200364500000
                """
            )
        );
        // cubeprog addresses a DFU device by port=USB<n>, so a click fills the index, not the serial.
        Assert.Equal(FlashTargetField.Index, target.Field);
        Assert.Equal("1", target.Value);
        Assert.Contains("200364500000", target.Detail);
    }

    [Fact]
    public void Uart_ReadsPortsFromByIdSymlinks()
    {
        var targets = Parse(
            FlashMethod.Uart,
            "stm32flash",
            """
            Serial ports:
            total 0
            lrwxrwxrwx 1 root root 13 Jul 14 20:00 usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0 -> ../../ttyUSB0
            lrwxrwxrwx 1 root root 13 Jul 14 20:00 usb-STMicro_STLink -> ../../ttyACM0
            """
        );
        Assert.Equal([FlashTargetField.Port, FlashTargetField.Port], targets.Select(t => t.Field));
        Assert.Equal(["/dev/ttyUSB0", "/dev/ttyACM0"], targets.Select(t => t.Value));
        // The human-readable by-id name is kept as the label.
        Assert.Contains("FTDI", targets[0].Label);
    }

    [Fact]
    public void Uart_FallsBackToPlainDevicePaths()
    {
        var targets = Parse(
            FlashMethod.Uart,
            "stm32flash",
            """
            Serial ports:
            /dev/ttyACM0
            /dev/ttyUSB0
            """
        );
        Assert.Equal(["/dev/ttyACM0", "/dev/ttyUSB0"], targets.Select(t => t.Value));
    }

    [Fact]
    public void NothingConnected_YieldsNoTargets()
    {
        Assert.Empty(
            Parse(
                FlashMethod.Uart,
                "stm32flash",
                """
                Serial ports:
                  (none found)
                """
            )
        );
        Assert.Empty(Parse(FlashMethod.Swd, "st-flash", "Found 0 stlink programmers"));
    }

    [Fact]
    public void Openocd_HasNoListMode_SoNothingIsOffered()
    {
        Assert.Empty(
            Parse(
                FlashMethod.Swd,
                "openocd",
                "openocd has no list mode; use: flash.sh swd --tool st-flash --list"
            )
        );
    }
}
