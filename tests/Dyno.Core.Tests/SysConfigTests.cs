using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Dyno.Core;
using Dyno.Core.Messages;
using Dyno.Core.Protocol;
using Dyno.Core.Serial;
using Dyno.Core.SysConfig;
using Xunit;

namespace Dyno.Core.Tests;

public class SysConfigCatalogTests
{
    [Fact]
    public void CatalogCoversEveryWireId_InOrder_WithNoDuplicates()
    {
        // The firmware sizes its store by SYSCFG_PARAM_COUNT; the catalog must describe exactly
        // those ids, in order, or Get(id) would return the wrong parameter's metadata.
        Assert.Equal((int)MessageConstants.SYSCFG_PARAM_COUNT, SysConfigCatalog.Parameters.Count);
        for (var i = 0; i < SysConfigCatalog.Parameters.Count; i++)
        {
            Assert.Equal((sysconfig_param_t)i, SysConfigCatalog.Parameters[i].Id);
        }
    }

    [Fact]
    public void CatalogNamesMatchTheEnumNames()
    {
        // Name is the config.h macro; the wire enum is SYSCFG_ + that macro. Keeping them locked
        // together catches a copy-paste mismatch between catalog rows.
        foreach (var def in SysConfigCatalog.Parameters)
        {
            Assert.Equal("SYSCFG_" + def.Name, def.Id.ToString());
        }
    }

    [Fact]
    public void DefaultsAreWithinTheirOwnRange()
    {
        foreach (var def in SysConfigCatalog.Parameters)
        {
            Assert.True(def.IsValid(def.Default), $"{def.Name} default {def.Default} out of range");
        }
    }

    [Fact]
    public void ValidationRejectsOutOfRangeAndNonIntegerUints()
    {
        var kp = SysConfigCatalog.Get(sysconfig_param_t.SYSCFG_K_P);
        Assert.True(kp.IsValid(-3.5));
        Assert.False(kp.IsValid(double.NaN));
        Assert.False(kp.IsValid(2e6));

        var delay = SysConfigCatalog.Get(sysconfig_param_t.SYSCFG_PID_TASK_OSDELAY);
        Assert.True(delay.IsValid(10));
        Assert.False(delay.IsValid(0), "firmware clamps osDelays to >= 1");
        Assert.False(delay.IsValid(10.5), "uint32 parameters take integers only");
    }

    [Fact]
    public void RawBitsMatchTheWireEncoding()
    {
        var kp = SysConfigCatalog.Get(sysconfig_param_t.SYSCFG_K_P);
        Assert.Equal(BitConverter.SingleToUInt32Bits(2.5f), kp.ToRawBits(2.5));

        var delay = SysConfigCatalog.Get(sysconfig_param_t.SYSCFG_PID_TASK_OSDELAY);
        Assert.Equal(42u, delay.ToRawBits(42));
    }
}

public class SysConfigStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"dyno-sysconfig-test-{Guid.NewGuid():N}.db"
    );

    public void Dispose()
    {
        // Pooled SQLite connections can hold the file briefly; clearing pools is implicit in
        // Dispose of the store, so a plain delete suffices here.
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void SavedValuesRoundTrip_AndSurviveReopen()
    {
        using (var store = new SysConfigStore(_dbPath))
        {
            store.Save(sysconfig_param_t.SYSCFG_K_P, "K_P", 2.5);
            store.Save(sysconfig_param_t.SYSCFG_PID_TASK_OSDELAY, "PID_TASK_OSDELAY", 20);
            store.Save(sysconfig_param_t.SYSCFG_K_P, "K_P", 3.75); // overwrite
        }

        // A fresh store on the same file sees what the first one wrote: that is the persistence
        // the device relies on across its own reboots.
        using var reopened = new SysConfigStore(_dbPath);
        var values = reopened.LoadAll();
        Assert.Equal(2, values.Count);
        Assert.Equal(3.75, values[sysconfig_param_t.SYSCFG_K_P]);
        Assert.Equal(20, values[sysconfig_param_t.SYSCFG_PID_TASK_OSDELAY]);
    }

    [Fact]
    public void RemoveForgetsAValue()
    {
        using var store = new SysConfigStore(_dbPath);
        store.Save(sysconfig_param_t.SYSCFG_K_I, "K_I", 0.5);
        store.Remove(sysconfig_param_t.SYSCFG_K_I);
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void EmptyStoreLoadsEmpty()
    {
        using var store = new SysConfigStore(_dbPath);
        Assert.Empty(store.LoadAll());
    }
}

public class SysConfigDeviceCommandTests
{
    /// <summary>Minimal in-memory serial: captures host→device writes and lets the test answer.</summary>
    private sealed class FakeSerial : ISerialConnection
    {
        private readonly Pipe _deviceToHost = new();

        public string PortName => "FAKE";
        public bool IsOpen { get; private set; }
        public Stream BaseStream { get; }
        public Action<byte[]>? OnWrite;

        public FakeSerial() => BaseStream = _deviceToHost.Reader.AsStream();

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(ReadOnlySpan<byte> data) => OnWrite?.Invoke(data.ToArray());

        public void DeviceSend(ReadOnlySpan<byte> data) =>
            _deviceToHost.Writer.WriteAsync(data.ToArray()).AsTask().GetAwaiter().GetResult();

        public void Dispose() { }
    }

    [Fact]
    public async Task SetSysConfigParam_SendsConfigFrame_WithIdAndValueBits()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        usb_msg_header_t? sentHeader = null;
        usb_cmd_header_t? sentCmd = null;
        byte[]? sentBody = null;

        serial.OnWrite = frame =>
        {
            var span = frame.AsSpan();
            var header = MemoryMarshal.Read<usb_msg_header_t>(
                span.Slice(sizeof(ushort), UsbFrame.HeaderSize)
            );
            var cmd = MemoryMarshal.Read<usb_cmd_header_t>(
                span.Slice(sizeof(ushort) + UsbFrame.HeaderSize, UsbFrame.CommandHeaderSize)
            );
            sentHeader = header;
            sentCmd = cmd;
            sentBody = span.Slice(
                    sizeof(ushort) + UsbFrame.HeaderSize + UsbFrame.CommandHeaderSize,
                    (int)header.payload_len - UsbFrame.CommandHeaderSize
                )
                .ToArray();

            // Ack like the firmware's USB task would, so the await completes.
            serial.DeviceSend(
                Wire.Message(
                    usb_msg_type_t.USB_MSG_RESPONSE,
                    task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                    new usb_response_data_t
                    {
                        opcode = cmd.opcode,
                        msg_id = cmd.msg_id,
                        status = (uint)usb_response_status_t.USB_RSP_OK,
                    }
                )
            );
        };

        client.Start();
        uint kpBits = BitConverter.SingleToUInt32Bits(2.5f);
        var response = await client.SetSysConfigParamAsync(sysconfig_param_t.SYSCFG_K_P, kpBits);

        Assert.Equal((uint)usb_response_status_t.USB_RSP_OK, response.status);
        Assert.Equal(usb_msg_type_t.USB_MSG_CONFIG, sentHeader!.Value.msg_type);
        Assert.Equal(task_offset_t.TASK_OFFSET_USB_CONTROLLER, sentHeader.Value.task_offset);
        Assert.Equal((ushort)usb_controller_command_t.USB_CMD_SET_SYSCONFIG, sentCmd!.Value.opcode);

        // Body layout is sysconfig_set_param_body: u16 id then the 32 value bits, little-endian.
        Assert.Equal(6, sentBody!.Length);
        Assert.Equal((ushort)sysconfig_param_t.SYSCFG_K_P, BitConverter.ToUInt16(sentBody, 0));
        Assert.Equal(kpBits, BitConverter.ToUInt32(sentBody, 2));
    }

    [Fact]
    public async Task SetSysConfigParam_RejectedByFirmware_Throws()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        serial.OnWrite = frame =>
        {
            var span = frame.AsSpan();
            var cmd = MemoryMarshal.Read<usb_cmd_header_t>(
                span.Slice(sizeof(ushort) + UsbFrame.HeaderSize, UsbFrame.CommandHeaderSize)
            );
            serial.DeviceSend(
                Wire.Message(
                    usb_msg_type_t.USB_MSG_RESPONSE,
                    task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                    new usb_response_data_t
                    {
                        opcode = cmd.opcode,
                        msg_id = cmd.msg_id,
                        status = (uint)usb_response_status_t.USB_RSP_MALFORMED,
                    }
                )
            );
        };

        client.Start();
        var ex = await Assert.ThrowsAsync<DeviceCommandException>(() =>
            client.SetSysConfigParamAsync(sysconfig_param_t.SYSCFG_PID_TASK_OSDELAY, 0)
        );
        Assert.Equal(usb_response_status_t.USB_RSP_MALFORMED, ex.Status);
    }
}
