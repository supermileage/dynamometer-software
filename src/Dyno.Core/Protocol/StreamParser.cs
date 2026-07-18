using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dyno.Core.Messages;

namespace Dyno.Core.Protocol;

/// <summary>
/// What one desync threw away and where it sat: the bytes skipped to regain alignment (capped at
/// <see cref="SkippedBytesCap"/>; <see cref="BytesDropped"/> is the true total), the header
/// of the last record decoded cleanly before the loss, and the header of the record that proved
/// alignment was back. The skipped bytes are the actual evidence of *what* got cut — a payload
/// arriving without its header looks quite different from a header without its payload.
/// </summary>
public sealed record ResyncDetails(
    int BytesDropped,
    byte[] SkippedBytes,
    usb_msg_header_t? LastGoodHeader,
    usb_msg_header_t NextHeader
)
{
    public const int SkippedBytesCap = 48;
}

/// <summary>
/// Decodes the framed STM32 → PC stream (protocol v5): each record travels as
/// <c>[uint16 SOF][usb_msg_header_t][payload][uint16 crc16]</c> — the same envelope the
/// host→device direction always used. <see cref="Append"/> accumulates bytes and emits one
/// <see cref="DeviceMessage"/> per CRC-valid frame via <see cref="MessageReceived"/>.
/// </summary>
/// <remarks>
/// Not thread-safe and not reentrant: drive it from a single reader.
///
/// The envelope is what makes loss <em>detectable</em> instead of inferred: bytes cut from a
/// record leave a frame whose CRC fails (or whose SOF vanished), and realignment is a scan to
/// the next SOF rather than the old byte-by-byte header-plausibility guessing. A spurious SOF
/// inside payload data is caught the same way — its CRC cannot match — at the cost of rescanning
/// from one byte past it.
/// </remarks>
public sealed class StreamParser
{
    private static readonly int HeaderSize = Unsafe.SizeOf<usb_msg_header_t>();
    private const byte SofLow = unchecked((byte)(MessageConstants.USB_FRAME_SOF & 0xFF));
    private const byte SofHigh = unchecked((byte)((MessageConstants.USB_FRAME_SOF >> 8) & 0xFF));

    /// <summary>Largest payload we believe; a claimed length beyond this marks the SOF as spurious
    /// immediately instead of waiting ~forever for a frame that will never complete.</summary>
    private const int MaxPayload = 256;

    private readonly List<byte> _buffer = new();

    public event Action<DeviceMessage>? MessageReceived;

    /// <summary>Raised once per desync — when alignment is regained — with what was thrown away and
    /// what it sat between. Dropping bytes is the *only* outward sign that this stream lost data: it
    /// has no sequence numbers and no CRC, so a record that arrives short simply runs into the next
    /// one, and everything after it is misaligned until the scan finds a real header again. Whoever
    /// sees this should treat it as a link fault (a device-side ring overflow, most likely).
    ///
    /// Reported on recovery rather than as the bytes go, because bytes arrive in whatever chunks the
    /// serial port hands over: one lost record is re-scanned across several of them, and reporting
    /// per chunk would turn a single fault into a stutter of near-identical warnings.</summary>
    public event Action<ResyncDetails>? Resynced;

    /// <summary>Bytes discarded since the last record decoded cleanly — a desync in progress.</summary>
    private int _dropped;

    /// <summary>The first <see cref="ResyncDetails.SkippedBytesCap"/> of the discarded bytes —
    /// enough to recognize what fragment they were (a payload without its header, a header without
    /// its payload) without unbounded buffering if a desync runs long.</summary>
    private readonly List<byte> _skipped = new();

    /// <summary>Header of the last record decoded cleanly — names what the lost bytes came after.</summary>
    private usb_msg_header_t? _lastGoodHeader;

    public void Append(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);

        int pos = 0;
        int count = _buffer.Count;
        while (count - pos >= 2)
        {
            var bytes = CollectionsMarshal.AsSpan(_buffer);

            // Not at a SOF: this byte belongs to nothing we can decode. Skip it (recorded) and
            // rescan — the same path handles garbage between frames, a frame whose SOF bytes
            // were themselves lost, and the bytes of a frame rejected below.
            if (bytes[pos] != SofLow || bytes[pos + 1] != SofHigh)
            {
                Skip(bytes[pos]);
                pos++;
                continue;
            }

            if (count - pos < 2 + HeaderSize)
            {
                break; // SOF seen; wait for the header
            }
            var header = MemoryMarshal.Read<usb_msg_header_t>(bytes.Slice(pos + 2, HeaderSize));

            if (header.payload_len > MaxPayload)
            {
                Skip(bytes[pos]); // spurious SOF (payload data that happened to match)
                pos++;
                continue;
            }

            int payloadLen = (int)header.payload_len;
            int frameLen = 2 + HeaderSize + payloadLen + 2;
            if (count - pos < frameLen)
            {
                break; // wait for the rest of the frame
            }

            var body = bytes.Slice(pos + 2, HeaderSize + payloadLen);
            ushort storedCrc = MemoryMarshal.Read<ushort>(
                bytes.Slice(pos + 2 + HeaderSize + payloadLen, 2)
            );
            if (UsbFrame.Crc16(body) != storedCrc)
            {
                // A cut-off or corrupted frame — or payload bytes imitating a SOF. Either way
                // these bytes prove nothing; drop one and let the scan find the next real frame.
                Skip(bytes[pos]);
                pos++;
                continue;
            }

            // A CRC-valid frame. Report any loss before the record that proves we recovered from
            // it, which is also the order the two things happened in.
            if (_dropped > 0)
            {
                var details = new ResyncDetails(
                    _dropped,
                    _skipped.ToArray(),
                    _lastGoodHeader,
                    header
                );
                _dropped = 0;
                _skipped.Clear();
                Resynced?.Invoke(details);
            }

            var payload = bytes.Slice(pos + 2 + HeaderSize, payloadLen);
            MessageReceived?.Invoke(Decode(header, payload));
            _lastGoodHeader = header;
            pos += frameLen;
        }

        if (pos > 0)
        {
            _buffer.RemoveRange(0, pos);
        }
    }

    private void Skip(byte value)
    {
        if (_skipped.Count < ResyncDetails.SkippedBytesCap)
        {
            _skipped.Add(value);
        }
        _dropped++;
    }

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
