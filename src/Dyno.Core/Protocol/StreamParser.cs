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
/// One CDC transfer that did not add up, as measured against the <see cref="usb_tx_batch_trailer"/>
/// closing it: what the device said it sent (<paramref name="DeclaredBytes"/>) against what actually
/// arrived (<paramref name="ObservedBytes"/>), and how many transfers went missing outright
/// (<paramref name="MissingTransfers"/>, from a gap in the device's sequence).
/// </summary>
/// <remarks>
/// This is what separates the two halves of the link. A resync says bytes were lost; it cannot say
/// whether the device failed to send them or the host failed to receive them. A short transfer
/// (<c>ObservedBytes &lt; DeclaredBytes</c>, sequence contiguous) means the device framed those bytes
/// and handed them to the CDC driver, and they did not land — the loss is below the firmware. A
/// sequence gap means a whole transfer the driver accepted never arrived. Both numbers adding up
/// while a resync still fires means the loss is inside a record the device itself framed wrong.
///
/// <see cref="ObservedBytes"/> counts every byte that arrived between the two trailers, decoded and
/// skipped alike: the question is what reached the host, not what it could make sense of.
/// </remarks>
public sealed record BatchAccounting(
    uint Sequence,
    uint DeclaredBytes,
    long ObservedBytes,
    uint MissingTransfers
)
{
    /// <summary>Bytes the device sent that never arrived (negative would mean bytes appeared from
    /// nowhere — a framing bug on our side, not a lossy link). Meaningful only when
    /// <see cref="MissingTransfers"/> is 0; across a gap the count spans transfers we never saw.</summary>
    public long Shortfall => DeclaredBytes - ObservedBytes;
}

/// <summary>
/// Decodes the framed STM32 → PC stream (protocol v7): each record travels as
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
///
/// Since v7 every CDC transfer closes with a <see cref="usb_tx_batch_trailer"/>, which this class
/// consumes rather than publishes: it is framing, like the SOF and CRC around it. It carries the
/// byte count the device handed to its USB driver, so the bytes arriving between two trailers can
/// be weighed against what was sent — see <see cref="BatchMisaccounted"/>. That is what makes the
/// loss *attributable*: the envelope detects it, the trailer says which side of the link it
/// happened on.
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

    /// <summary>Raised for each CDC transfer whose bytes did not add up against the trailer closing
    /// it. Silent while the link is healthy, so a capture that shows resyncs but no accounting
    /// failures is itself the answer: the bytes all arrived and the fault is in what the device
    /// framed, not in what reached us. See <see cref="BatchAccounting"/>.</summary>
    public event Action<BatchAccounting>? BatchMisaccounted;

    /// <summary>Bytes discarded since the last record decoded cleanly — a desync in progress.</summary>
    private int _dropped;

    /// <summary>The first <see cref="ResyncDetails.SkippedBytesCap"/> of the discarded bytes —
    /// enough to recognize what fragment they were (a payload without its header, a header without
    /// its payload) without unbounded buffering if a desync runs long.</summary>
    private readonly List<byte> _skipped = new();

    /// <summary>Header of the last record decoded cleanly — names what the lost bytes came after.</summary>
    private usb_msg_header_t? _lastGoodHeader;

    /// <summary>Bytes that have arrived since the last <see cref="usb_tx_batch_trailer"/> — decoded
    /// and skipped alike — to be weighed against the next trailer's declared length.</summary>
    private long _bytesSinceTrailer;

    /// <summary>Sequence of the last trailer seen, once one has been. Until then there is no
    /// baseline: the first trailer closes a transfer we joined partway through, so its byte count
    /// means nothing and neither does its distance from a previous sequence we never saw.</summary>
    private uint? _lastBatchSeq;

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

            // The trailer is framing, not telemetry: it is consumed here and never published. It
            // closes the transfer it describes, so its own bytes are part of the count it is
            // weighed against — hence the accounting before the counter resets.
            if (IsBatchTrailer(header) && TryRead<usb_tx_batch_trailer>(payload, out var trailer))
            {
                AccountForBatch(trailer, _bytesSinceTrailer + frameLen);
                _bytesSinceTrailer = 0;
            }
            else
            {
                MessageReceived?.Invoke(Decode(header, payload));
                _bytesSinceTrailer += frameLen;
            }

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
        // Skipped bytes still *arrived*, and the trailer's length is what the device *sent*, so
        // they belong in the comparison. Leaving them out would turn every resync into a phantom
        // shortfall and hide the one case worth seeing: bytes that never reached us at all.
        _bytesSinceTrailer++;
    }

    private static bool IsBatchTrailer(in usb_msg_header_t h) =>
        h.msg_type == usb_msg_type_t.USB_MSG_STATUS
        && h.task_offset == task_offset_t.TASK_OFFSET_USB_CONTROLLER;

    private void AccountForBatch(in usb_tx_batch_trailer trailer, long observed)
    {
        // Sequence arithmetic is unsigned and deliberately wrapping: the counter is a uint32 on the
        // device and rolls over after ~4 billion transfers, which at a few hundred a second is days
        // of continuous streaming — reachable, and not worth a spurious "4 billion transfers lost".
        uint missing = _lastBatchSeq is { } previous
            ? unchecked(trailer.batch_seq - previous - 1)
            : 0;

        // No baseline yet: this trailer closes a transfer we joined partway through, so its byte
        // count is short by however much preceded us and would report as loss that never happened.
        bool haveBaseline = _lastBatchSeq is not null;
        _lastBatchSeq = trailer.batch_seq;

        if (!haveBaseline || (missing == 0 && observed == trailer.batch_len))
        {
            return;
        }

        BatchMisaccounted?.Invoke(
            new BatchAccounting(trailer.batch_seq, trailer.batch_len, observed, missing)
        );
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
