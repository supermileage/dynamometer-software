using System.IO.Pipelines;
using System.Runtime.InteropServices;
using Dyno.Core;
using Dyno.Core.Messages;
using Dyno.Core.Protocol;
using Dyno.Core.Serial;
using Dyno.Core.SysConfig;
using Microsoft.Data.Sqlite;
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
    public void RawBitsRoundTrip_ForBothParameterKinds()
    {
        // A write goes out as 32 bits and its ack echoes nothing but an opcode, so the value has to
        // be recoverable from the bits alone to be reported back to the user.
        var kp = SysConfigCatalog.Get(sysconfig_param_t.SYSCFG_K_P);
        Assert.Equal(2.5, kp.FromRawBits(kp.ToRawBits(2.5)));

        var delay = SysConfigCatalog.Get(sysconfig_param_t.SYSCFG_PID_TASK_OSDELAY);
        Assert.Equal(10.0, delay.FromRawBits(delay.ToRawBits(10)));
    }

    [Fact]
    public void DescribeNamesTheParameterItsValueAndUnit()
    {
        // What the event log prints in place of "opcode=1 id=13".
        Assert.Equal("K_P = 2.5", SysConfigCatalog.Get(sysconfig_param_t.SYSCFG_K_P).Describe(2.5));
        Assert.Equal(
            "PID_TASK_OSDELAY = 10 ms",
            SysConfigCatalog.Get(sysconfig_param_t.SYSCFG_PID_TASK_OSDELAY).Describe(10)
        );
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
        Assert.False(kp.IsValid(kp.Max * 2));

        var delay = SysConfigCatalog.Get(sysconfig_param_t.SYSCFG_PID_TASK_OSDELAY);
        Assert.True(delay.IsValid(10));
        Assert.True(
            delay.IsValid(delay.Min),
            "the bottom of a range is reachable on purpose: a 0 ms delay is a task that stops "
                + "yielding, which the range allows because it is representable"
        );
        Assert.False(delay.IsValid(delay.Max + 1));
        Assert.False(delay.IsValid(10.5), "uint32 parameters take integers only");
    }

    [Fact]
    public void IntegerRangesAreWholeTypeWidths()
    {
        // Each integer parameter's range is the full range of the type the value logically is --
        // a millisecond delay is a uint16_t, a retry count a uint8_t -- rather than what its
        // consumer would find sensible. So any bound here that is not a type's width is a
        // judgement call that has crept back into the schema. Enums are exempt: theirs is the
        // last option code, which the generators derive from the option list.
        double[] typeWidths = [byte.MaxValue, ushort.MaxValue, uint.MaxValue];
        foreach (var def in SysConfigCatalog.Parameters.Where(p => !p.IsFloat && !p.IsEnum))
        {
            Assert.Equal(0, def.Min);
            Assert.Contains(def.Max, typeWidths);
        }
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

public class SysConfigDeviceMirrorTests
{
    private static readonly IReadOnlyDictionary<sysconfig_param_t, double> NoOverrides =
        new Dictionary<sysconfig_param_t, double>();

    [Fact]
    public void AFreshDevice_IsOwedTheWholeCatalog_DefaultsIncluded()
    {
        // The board's store is RAM with no flash behind it, and a board that stayed powered while
        // the host restarted is still holding the *last* session's values — so a parameter the user
        // never overrode still has to be written, or it keeps whatever it was left at.
        var mirror = new SysConfigDeviceMirror();

        var outstanding = mirror.Outstanding(NoOverrides);

        Assert.Equal(SysConfigCatalog.Parameters.Count, outstanding.Count);
        Assert.All(outstanding, o => Assert.Equal(o.Def.Default, o.Value));
    }

    [Fact]
    public void OverridesAreWantedInPlaceOfDefaults()
    {
        var mirror = new SysConfigDeviceMirror();
        var wanted = new Dictionary<sysconfig_param_t, double>
        {
            [sysconfig_param_t.SYSCFG_K_P] = 2.5,
        };

        var kp = Assert.Single(
            mirror.Outstanding(wanted),
            o => o.Def.Id == sysconfig_param_t.SYSCFG_K_P
        );
        Assert.Equal(2.5, kp.Value);
    }

    [Fact]
    public void OnceConfirmed_OnlyAChangedValueIsOwed()
    {
        var mirror = new SysConfigDeviceMirror();
        var wanted = SysConfigCatalog.Parameters.ToDictionary(d => d.Id, d => d.Default);
        foreach (var (def, value) in mirror.Outstanding(wanted))
        {
            mirror.Confirm(def.Id, value);
        }

        // Nothing changed: the device is up to date and a save must not re-send the catalog.
        Assert.Empty(mirror.Outstanding(wanted));

        // One value edited: exactly that parameter goes out.
        wanted[sysconfig_param_t.SYSCFG_K_P] = 2.5;
        var owed = Assert.Single(mirror.Outstanding(wanted));
        Assert.Equal(sysconfig_param_t.SYSCFG_K_P, owed.Def.Id);
        Assert.Equal(2.5, owed.Value);
    }

    [Fact]
    public void AWriteThatWasNeverConfirmed_StaysOwed()
    {
        var mirror = new SysConfigDeviceMirror();
        var wanted = SysConfigCatalog.Parameters.ToDictionary(d => d.Id, d => d.Default);
        foreach (var (def, value) in mirror.Outstanding(wanted))
        {
            // The device never acked K_P — so it must not be marked as holding it.
            if (def.Id != sysconfig_param_t.SYSCFG_K_P)
            {
                mirror.Confirm(def.Id, value);
            }
        }

        var owed = Assert.Single(mirror.Outstanding(wanted));
        Assert.Equal(sysconfig_param_t.SYSCFG_K_P, owed.Def.Id);
    }

    [Fact]
    public void Forget_OwesTheWholeCatalogAgain()
    {
        var mirror = new SysConfigDeviceMirror();
        var wanted = SysConfigCatalog.Parameters.ToDictionary(d => d.Id, d => d.Default);
        foreach (var (def, value) in mirror.Outstanding(wanted))
        {
            mirror.Confirm(def.Id, value);
        }

        // The link dropped and came back — which is exactly what a board reset looks like from here.
        mirror.Forget();

        Assert.Equal(SysConfigCatalog.Parameters.Count, mirror.Outstanding(wanted).Count);
        Assert.Equal(0, mirror.ConfirmedCount);
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
        // The store opens its connection with Pooling=False, so disposing it really releases the
        // file handle — without that, this delete throws "file in use" on Windows, where an open
        // file cannot be unlinked the way it can on Linux.
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    /// <summary>
    /// Wire ids are positional, so removing a parameter shifts every id after it. A database
    /// written under the old numbering must not be read back by raw id — that would hand each
    /// parameter its neighbour's value with nothing to show anything was wrong. The store re-keys
    /// on the stored name when it opens.
    /// </summary>
    [Fact]
    public void ValuesSavedUnderStaleIdsAreRekeyedByName()
    {
        // Write K_P's value under a deliberately wrong id, as a pre-renumbering file would hold it.
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE sysconfig (
                    param_id   INTEGER PRIMARY KEY,
                    name       TEXT NOT NULL,
                    value      REAL NOT NULL,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO sysconfig VALUES (99, 'K_P', 4.25, '2026-01-01T00:00:00Z');
                """;
            create.ExecuteNonQuery();
        }

        using var store = new SysConfigStore(_dbPath);
        var values = store.LoadAll();

        Assert.Equal(4.25, values[sysconfig_param_t.SYSCFG_K_P]);
        Assert.DoesNotContain((sysconfig_param_t)99, values.Keys);
    }

    /// <summary>A saved value whose parameter the catalog no longer has is dropped rather than
    /// carried onto whichever id now sits in that slot.</summary>
    [Fact]
    public void ValuesForWithdrawnParametersAreForgotten()
    {
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE sysconfig (
                    param_id   INTEGER PRIMARY KEY,
                    name       TEXT NOT NULL,
                    value      REAL NOT NULL,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO sysconfig VALUES (0, 'MOMENT_OF_INERTIA_KG_M2', 7.5, '2026-01-01T00:00:00Z');
                INSERT INTO sysconfig VALUES (1, 'K_P', 1.5, '2026-01-01T00:00:00Z');
                """;
            create.ExecuteNonQuery();
        }

        using var store = new SysConfigStore(_dbPath);
        var values = store.LoadAll();

        // The inertia moved to the app's PC constants; K_P keeps its value at its current id.
        Assert.Equal(1.5, values[sysconfig_param_t.SYSCFG_K_P]);
        Assert.DoesNotContain(7.5, values.Values);
    }

    [Fact]
    public void PcConstantsRoundTripAndAreSeparateFromRuntimeValues()
    {
        using (var store = new SysConfigStore(_dbPath))
        {
            store.SavePcConstant("GEAR_RATIO", 3.5);
            store.SavePcConstant("MOMENT_OF_INERTIA_KG_M2", 0.125);
            store.SavePcConstant("GEAR_RATIO", 4.0); // overwrite
            store.Save(sysconfig_param_t.SYSCFG_K_P, "K_P", 2.0);
        }

        using var reopened = new SysConfigStore(_dbPath);
        var constants = reopened.LoadAllPcConstants();

        Assert.Equal(4.0, constants["GEAR_RATIO"]);
        Assert.Equal(0.125, constants["MOMENT_OF_INERTIA_KG_M2"]);
        // Host-only: nothing about them leaks into what gets pushed to the device.
        Assert.Equal(2.0, reopened.LoadAll()[sysconfig_param_t.SYSCFG_K_P]);
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
        Assert.Empty(store.LoadAllCompileTime());
    }

    [Fact]
    public void CompileTimeValuesRoundTrip_AndSurviveReopen()
    {
        using (var store = new SysConfigStore(_dbPath))
        {
            // Kept as text: a #define is a C token, not a number.
            store.SaveCompileTime("ADS1115_SAMPLE_SPEED", "config.h", "ADS1115_RATE_860");
            store.SaveCompileTime("STM32_PERIPHERAL_TIM2_ENABLE", "debug.h", "1");
            store.SaveCompileTime("ADS1115_SAMPLE_SPEED", "config.h", "ADS1115_RATE_475"); // overwrite
        }

        using var reopened = new SysConfigStore(_dbPath);
        var values = reopened.LoadAllCompileTime();
        Assert.Equal(2, values.Count);
        Assert.Equal("ADS1115_RATE_475", values["ADS1115_SAMPLE_SPEED"]);
        Assert.Equal("1", values["STM32_PERIPHERAL_TIM2_ENABLE"]);
    }

    [Fact]
    public void RemoveCompileTimeForgetsASetting()
    {
        using var store = new SysConfigStore(_dbPath);
        store.SaveCompileTime("NUM_APERTURES", "config.h", "128");
        store.RemoveCompileTime("NUM_APERTURES");
        Assert.Empty(store.LoadAllCompileTime());
    }

    [Fact]
    public void TheTwoKindsOfSettingDoNotCollide()
    {
        // Same name, different tables: the runtime K_P is a value the device is told, the
        // compile-time one would be the board's boot default. Nothing should conflate them.
        using var store = new SysConfigStore(_dbPath);
        store.Save(sysconfig_param_t.SYSCFG_K_P, "K_P", 2.5);
        store.SaveCompileTime("K_P", "config.h", "1.0f");

        Assert.Equal(2.5, store.LoadAll()[sysconfig_param_t.SYSCFG_K_P]);
        Assert.Equal("1.0f", store.LoadAllCompileTime()["K_P"]);

        store.RemoveCompileTime("K_P");
        Assert.Equal(2.5, store.LoadAll()[sysconfig_param_t.SYSCFG_K_P]);
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
    public async Task SetSysConfigParam_Ack_CarriesTheParameterAndValueItAnswers()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        var acks = new List<CommandResponse>();
        client.MessageReceived += m =>
        {
            if (m is CommandResponse r)
            {
                lock (acks)
                {
                    acks.Add(r);
                }
            }
        };

        AckWhatever(serial, usb_response_status_t.USB_RSP_OK);
        client.Start();
        await client.SetSysConfigParamAsync(
            sysconfig_param_t.SYSCFG_K_P,
            BitConverter.SingleToUInt32Bits(2.5f)
        );

        // The RESPONSE frame says only "opcode 1, msg_id N, OK" — on its own that cannot tell a
        // reader that K_P is now 2.5. The client pairs it back to the write it acks.
        var ack = Assert.Single(acks);
        Assert.Equal("sysconfig K_P = 2.5", ack.Request);
    }

    [Fact]
    public async Task SetSysConfigParam_AnnouncesTheWrite_AndDoesNotRepeatItPerRetry()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        var sent = new List<string>();
        var failed = new List<string>();
        client.CommandSent += d => sent.Add(d);
        client.CommandFailed += (d, _) => failed.Add(d);

        AckWhatever(serial, usb_response_status_t.USB_RSP_OK);
        client.Start();
        await client.SetSysConfigParamAsync(
            sysconfig_param_t.SYSCFG_K_P,
            BitConverter.SingleToUInt32Bits(2.5f)
        );

        Assert.Equal("sysconfig K_P = 2.5", Assert.Single(sent));
        Assert.Empty(failed);
    }

    [Fact]
    public async Task SetSysConfigParam_Unannounced_IsWrittenButNotNarrated()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        var sent = new List<string>();
        var acks = new List<CommandResponse>();
        client.CommandSent += d => sent.Add(d);
        client.MessageReceived += m =>
        {
            if (m is CommandResponse r)
            {
                acks.Add(r);
            }
        };

        AckWhatever(serial, usb_response_status_t.USB_RSP_OK);
        client.Start();
        var response = await client.SetSysConfigParamAsync(
            sysconfig_param_t.SYSCFG_K_P,
            BitConverter.SingleToUInt32Bits(2.5f),
            announce: false
        );

        // The restore that runs on every connect writes all 27 parameters; narrating each one would
        // bury the log. The write still happens — it just reports itself as a batch, upstream.
        Assert.Equal((uint)usb_response_status_t.USB_RSP_OK, response.status);
        Assert.Empty(sent);

        // Its ack has no description, but it is still an ack for a command we sent: a consumer must
        // be able to tell that from a reply that matched nothing, or it will report the whole quiet
        // restore as 27 stray frames.
        var ack = Assert.Single(acks);
        Assert.Null(ack.Request);
        Assert.True(ack.Matched);
    }

    [Fact]
    public async Task Ack_IsPublishedToSubscribers_BeforeTheSenderResumes()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        var published = false;
        client.MessageReceived += m => published |= m is CommandResponse;

        AckWhatever(serial, usb_response_status_t.USB_RSP_OK);
        client.Start();
        await client.SetSysConfigParamAsync(
            sysconfig_param_t.SYSCFG_K_P,
            BitConverter.SingleToUInt32Bits(2.5f)
        );

        // The caller resumes to send (and announce) its next write, so a reply still in flight when
        // it does would be logged behind that announcement — an ack reading as the next parameter's.
        // Applying a page of edits is exactly that: one write after another down the list.
        Assert.True(published, "the reply must reach subscribers before its sender continues");
    }

    [Fact]
    public async Task SetSysConfigParam_NeverAcked_IsReportedAsUnanswered()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);
        client.CommandTimeout = TimeSpan.FromMilliseconds(50);

        var sent = new List<string>();
        var failures = new List<(string Request, Exception Error)>();
        client.CommandSent += d => sent.Add(d);
        client.CommandFailed += (d, ex) => failures.Add((d, ex));

        serial.OnWrite = _ => { }; // the device hears it and says nothing
        client.Start();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.SetSysConfigParamAsync(
                sysconfig_param_t.SYSCFG_K_P,
                BitConverter.SingleToUInt32Bits(2.5f)
            )
        );

        // The write is announced once and its silence reported once, however many retries it took:
        // a write that vanishes must still leave a trace, or the log's silence would look like the
        // silence of a value that applied cleanly.
        Assert.Equal("sysconfig K_P = 2.5", Assert.Single(sent));
        var failure = Assert.Single(failures);
        Assert.Equal("sysconfig K_P = 2.5", failure.Request);
        Assert.IsType<TimeoutException>(failure.Error);
    }

    [Fact]
    public async Task SetSysConfigParam_Rejected_IsNotAlsoReportedAsUnanswered()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        var failed = new List<string>();
        client.CommandFailed += (d, _) => failed.Add(d);

        AckWhatever(serial, usb_response_status_t.USB_RSP_MALFORMED);
        client.Start();
        await Assert.ThrowsAsync<DeviceCommandException>(() =>
            client.SetSysConfigParamAsync(sysconfig_param_t.SYSCFG_K_P, 0)
        );

        // The device answered — with a refusal. Raising CommandFailed too would have the log claim
        // it both refused the value and never heard of it.
        Assert.Empty(failed);
    }

    [Fact]
    public async Task Response_ThatMatchesNoRequest_DescribesNothing()
    {
        using var serial = new FakeSerial();
        using var client = new DeviceClient(serial);

        var acks = new List<CommandResponse>();
        var seen = new TaskCompletionSource();
        client.MessageReceived += m =>
        {
            if (m is CommandResponse r)
            {
                acks.Add(r);
                seen.TrySetResult();
            }
        };

        client.Start();
        // A reply to a command this host never sent (a duplicate ack, or one that outlived its
        // command's timeout): there is no request to name, and the client must not invent one.
        serial.DeviceSend(
            Wire.Message(
                usb_msg_type_t.USB_MSG_RESPONSE,
                task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                new usb_response_data_t
                {
                    opcode = (ushort)usb_controller_command_t.USB_CMD_SET_SYSCONFIG,
                    msg_id = 9999,
                    status = (uint)usb_response_status_t.USB_RSP_OK,
                }
            )
        );

        await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var ack = Assert.Single(acks);
        Assert.Null(ack.Request);
        Assert.False(ack.Matched);
    }

    /// <summary>Acks whatever the host writes, echoing its opcode and msg_id like the firmware.</summary>
    private static void AckWhatever(FakeSerial serial, usb_response_status_t status) =>
        serial.OnWrite = frame =>
        {
            var cmd = MemoryMarshal.Read<usb_cmd_header_t>(
                frame
                    .AsSpan()
                    .Slice(sizeof(ushort) + UsbFrame.HeaderSize, UsbFrame.CommandHeaderSize)
            );
            serial.DeviceSend(
                Wire.Message(
                    usb_msg_type_t.USB_MSG_RESPONSE,
                    task_offset_t.TASK_OFFSET_USB_CONTROLLER,
                    new usb_response_data_t
                    {
                        opcode = cmd.opcode,
                        msg_id = cmd.msg_id,
                        status = (uint)status,
                    }
                )
            );
        };

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
