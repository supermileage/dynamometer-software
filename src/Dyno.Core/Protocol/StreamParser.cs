using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dyno.Core.Messages;

namespace Dyno.Core.Protocol;

/// <summary>
/// Decodes the <b>unframed</b> STM32 → PC byte stream. The firmware emits a back-to-back
/// concatenation of <c>usb_msg_header_t</c>(12 bytes) + payload, with no SOF/CRC (USB CDC
/// is already reliable). <see cref="Append"/> accumulates bytes and emits one
/// <see cref="DeviceMessage"/> per complete record via <see cref="MessageReceived"/>.
/// </summary>
/// <remarks>
/// Not thread-safe and not reentrant: drive it from a single reader. A plausibility check
/// on the header lets it best-effort resync (drop a byte) should the stream ever desync.
///
/// That check is the only thing standing between a desync and nonsense: with no SOF and no CRC on
/// this direction, any 12 bytes that happen to look like a header <em>are</em> a header, and the
/// parser will then consume a payload's worth of real data behind it. So it is deliberately as
/// strict as the format allows — an inbound-only message type, a payload that fits, and a task
/// offset the firmware actually has — which together reject the overwhelming majority of
/// mid-record windows and get the reader realigned within a few bytes.
/// </remarks>
public sealed class StreamParser
{
    private static readonly int HeaderSize = Unsafe.SizeOf<usb_msg_header_t>();

    /// <summary>Largest payload we believe; bigger ⇒ treat the header as a desync and resync.</summary>
    private const int MaxPayload = 256;

    private readonly List<byte> _buffer = new();

    public event Action<DeviceMessage>? MessageReceived;

    /// <summary>Raised once per desync — when alignment is regained — with the total bytes thrown
    /// away to regain it. Dropping bytes is the *only* outward sign that this stream lost data: it
    /// has no sequence numbers and no CRC, so a record that arrives short simply runs into the next
    /// one, and everything after it is misaligned until the scan finds a real header again. Whoever
    /// sees this should treat it as a link fault (a device-side ring overflow, most likely).
    ///
    /// Reported on recovery rather than as the bytes go, because bytes arrive in whatever chunks the
    /// serial port hands over: one lost record is re-scanned across several of them, and reporting
    /// per chunk would turn a single fault into a stutter of near-identical warnings.</summary>
    public event Action<int>? Resynced;

    /// <summary>Bytes discarded since the last record decoded cleanly — a desync in progress.</summary>
    private int _dropped;

    public void Append(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);

        int pos = 0;
        int count = _buffer.Count;
        while (count - pos >= HeaderSize)
        {
            var bytes = CollectionsMarshal.AsSpan(_buffer);
            var header = MemoryMarshal.Read<usb_msg_header_t>(bytes.Slice(pos, HeaderSize));

            if (!IsPlausible(header))
            {
                pos++; // resync: drop one byte and re-scan
                _dropped++;
                continue;
            }

            int payloadLen = (int)header.payload_len;
            if (count - pos - HeaderSize < payloadLen)
            {
                break; // wait for the rest of the payload
            }

            // Alignment is back. Report the loss before the record that proves we recovered from
            // it, which is also the order the two things happened in.
            if (_dropped > 0)
            {
                var dropped = _dropped;
                _dropped = 0;
                Resynced?.Invoke(dropped);
            }

            var payload = bytes.Slice(pos + HeaderSize, payloadLen);
            MessageReceived?.Invoke(Decode(header, payload));
            pos += HeaderSize + payloadLen;
        }

        if (pos > 0)
        {
            _buffer.RemoveRange(0, pos);
        }
    }

    /// <summary>The only task offsets the firmware can stamp on a frame. They are sparse
    /// (0, 0x10000, 0x20000 …), which makes them a strong check against a header that is really
    /// just a window onto the middle of some other record.</summary>
    private static readonly HashSet<task_offset_t> KnownTasks = Enum.GetValues<task_offset_t>()
        .ToHashSet();

    /// <summary>
    /// Whether these 12 bytes can be a header at all. Everything the format pins down is checked,
    /// because nothing else will: an inbound-only message type, a task the firmware has, a payload
    /// that fits, and — for the records we know — the exact size that record must be.
    ///
    /// That last check is what a byte-window struggles to fake. Without it, zero-filled bytes sail
    /// through: <c>TASK_OFFSET_TASK_MONITOR</c> is 0, so any run of zeros in the right place is a
    /// "valid" task, and a stray 4 nearby is a "valid" STREAM type. The size requirement is the
    /// difference between resyncing quietly and handing the app a message the device never sent.
    /// </summary>
    private static bool IsPlausible(in usb_msg_header_t h) =>
        h.payload_len <= MaxPayload
        && IsInboundType(h.msg_type)
        && KnownTasks.Contains(h.task_offset)
        && (RecordSize(h) is not int size || size == h.payload_len);

    /// <summary>The exact payload size of the record this header claims to be, or null for a
    /// (type, task) pair we have no decoder for — those we let through, and they surface as an
    /// <see cref="UnknownMessage"/> rather than being mistaken for a desync. Sizes are frozen per
    /// protocol version (the firmware static-asserts them and the handshake refuses a version it
    /// doesn't share), so an exact match is a safe demand rather than a brittle one.</summary>
    private static int? RecordSize(in usb_msg_header_t h) =>
        (h.msg_type, h.task_offset) switch
        {
            (usb_msg_type_t.USB_MSG_RESPONSE, _) => Unsafe.SizeOf<usb_response_data_t>(),
            (usb_msg_type_t.USB_MSG_ERROR or usb_msg_type_t.USB_MSG_WARNING, _) =>
                Unsafe.SizeOf<task_error_data>(),
            (usb_msg_type_t.USB_MSG_EVENT, task_offset_t.TASK_OFFSET_USB_CONTROLLER) =>
                Unsafe.SizeOf<usb_device_ready_event>(),
            (usb_msg_type_t.USB_MSG_EVENT, task_offset_t.TASK_OFFSET_SESSION_CONTROLLER) =>
                Unsafe.SizeOf<session_state_event>(),
            (usb_msg_type_t.USB_MSG_STREAM, task_offset_t.TASK_OFFSET_OPTICAL_ENCODER) =>
                Unsafe.SizeOf<optical_encoder_output_data>(),
            (
                usb_msg_type_t.USB_MSG_STREAM,
                task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADC
                    or task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115
            ) => Unsafe.SizeOf<forcesensor_output_data>(),
            (usb_msg_type_t.USB_MSG_STREAM, task_offset_t.TASK_OFFSET_BPM_CONTROLLER) =>
                Unsafe.SizeOf<bpm_output_data>(),
            (usb_msg_type_t.USB_MSG_STREAM, task_offset_t.TASK_OFFSET_SESSION_CONTROLLER) =>
                Unsafe.SizeOf<session_controller_output_data>(),
            (usb_msg_type_t.USB_MSG_STREAM, task_offset_t.TASK_OFFSET_TASK_MONITOR) =>
                Unsafe.SizeOf<task_monitor_output_data>(),
            _ => null,
        };

    /// <summary>Message types the device sends to the host. <c>COMMAND</c>/<c>CONFIG</c> (host→device)
    /// and <c>INVALID</c> never arrive inbound, so treating them as implausible tightens resync.</summary>
    private static bool IsInboundType(usb_msg_type_t type) =>
        type
            is usb_msg_type_t.USB_MSG_RESPONSE
                or usb_msg_type_t.USB_MSG_EVENT
                or usb_msg_type_t.USB_MSG_STREAM
                or usb_msg_type_t.USB_MSG_STATUS
                or usb_msg_type_t.USB_MSG_ERROR
                or usb_msg_type_t.USB_MSG_WARNING;

    private static DeviceMessage Decode(in usb_msg_header_t h, ReadOnlySpan<byte> payload) =>
        h.msg_type switch
        {
            usb_msg_type_t.USB_MSG_STREAM => DecodeStream(h, payload),
            usb_msg_type_t.USB_MSG_ERROR or usb_msg_type_t.USB_MSG_WARNING =>
                TryRead<task_error_data>(payload, out var e)
                    ? new DeviceFault(ErrorDecoder.Decode(e.error_code), e.timestamp)
                    : Unknown(h, payload),
            usb_msg_type_t.USB_MSG_RESPONSE => TryRead<usb_response_data_t>(payload, out var r)
                ? new CommandResponse(h.task_offset, r)
                : Unknown(h, payload),
            usb_msg_type_t.USB_MSG_EVENT => DecodeEvent(h, payload),
            _ => Unknown(h, payload),
        };

    private static DeviceMessage DecodeEvent(in usb_msg_header_t h, ReadOnlySpan<byte> payload) =>
        h.task_offset switch
        {
            task_offset_t.TASK_OFFSET_USB_CONTROLLER
                when TryRead<usb_device_ready_event>(payload, out var d) => new DeviceReady(d),
            task_offset_t.TASK_OFFSET_SESSION_CONTROLLER
                when TryRead<session_state_event>(payload, out var d) => new SessionState(d),
            _ => Unknown(h, payload),
        };

    private static DeviceMessage DecodeStream(in usb_msg_header_t h, ReadOnlySpan<byte> p) =>
        h.task_offset switch
        {
            task_offset_t.TASK_OFFSET_OPTICAL_ENCODER
                when TryRead<optical_encoder_output_data>(p, out var d) => new OpticalEncoderSample(
                d
            ),
            (
                task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADC
                or task_offset_t.TASK_OFFSET_FORCE_SENSOR_ADS1115
            ) when TryRead<forcesensor_output_data>(p, out var d) => new ForceSensorSample(
                h.task_offset,
                d
            ),
            task_offset_t.TASK_OFFSET_BPM_CONTROLLER when TryRead<bpm_output_data>(p, out var d) =>
                new BpmSample(d),
            task_offset_t.TASK_OFFSET_SESSION_CONTROLLER
                when TryRead<session_controller_output_data>(p, out var d) =>
                new SessionControllerSample(d),
            task_offset_t.TASK_OFFSET_TASK_MONITOR
                when TryRead<task_monitor_output_data>(p, out var d) => new TaskMonitorSample(d),
            _ => Unknown(h, p),
        };

    /// <summary>Reads a struct only when the payload length matches its size exactly.</summary>
    private static bool TryRead<T>(ReadOnlySpan<byte> payload, out T value)
        where T : struct
    {
        if (payload.Length == Unsafe.SizeOf<T>())
        {
            value = MemoryMarshal.Read<T>(payload);
            return true;
        }
        value = default;
        return false;
    }

    private static UnknownMessage Unknown(in usb_msg_header_t h, ReadOnlySpan<byte> p) =>
        new(h, p.ToArray());
}
