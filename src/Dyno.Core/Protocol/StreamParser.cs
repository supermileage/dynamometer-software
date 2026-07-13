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
/// </remarks>
public sealed class StreamParser
{
    private static readonly int HeaderSize = Unsafe.SizeOf<usb_msg_header_t>();

    /// <summary>Largest payload we believe; bigger ⇒ treat the header as a desync and resync.</summary>
    private const int MaxPayload = 256;

    private readonly List<byte> _buffer = new();

    public event Action<DeviceMessage>? MessageReceived;

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
                continue;
            }

            int payloadLen = (int)header.payload_len;
            if (count - pos - HeaderSize < payloadLen)
            {
                break; // wait for the rest of the payload
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

    private static bool IsPlausible(in usb_msg_header_t h) =>
        h.payload_len <= MaxPayload && IsInboundType(h.msg_type);

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
