#include "Tasks/USB/usb_framer.h"
#include "Tasks/USB/usb_rx_ring.h"

#include <cstring>

bool usb_framer_try_read_frame(
    usb_msg_header_t& header, uint8_t* payload, size_t payloadCapacity, size_t& payloadLen)
{
    constexpr size_t SOF_SIZE = sizeof(uint16_t);
    constexpr size_t HDR_SIZE = sizeof(usb_msg_header_t);
    constexpr size_t CRC_SIZE = sizeof(uint16_t);
    constexpr size_t MIN_FRAME = SOF_SIZE + HDR_SIZE + CRC_SIZE;

    // A ring overflow means bytes were dropped: the stream is desynced and whatever is
    // buffered now straddles the gap. Discard it and resync from fresh bytes rather than
    // risk a torn frame whose (plausible) length field stalls the parser waiting for a
    // completion that never comes. Any command lost this way is recovered by the host's
    // ack-timeout retry (all inbound commands are acknowledged).
    if (usb_rx_overflowed())
    {
        usb_rx_flush();
    }

    while (usb_rx_available() >= MIN_FRAME)
    {
        uint8_t hdrPeek[SOF_SIZE + HDR_SIZE];
        usb_rx_peek(hdrPeek, sizeof(hdrPeek));

        uint16_t sof = static_cast<uint16_t>(hdrPeek[0] | (hdrPeek[1] << 8));
        if (sof != USB_FRAME_SOF)
        {
            usb_rx_skip(1); // byte-wise resync until a start-of-frame marker lines up
            continue;
        }

        usb_msg_header_t candidate;
        memcpy(&candidate, hdrPeek + SOF_SIZE, HDR_SIZE);

        if (candidate.payload_len > USB_RX_MAX_PAYLOAD || candidate.payload_len > payloadCapacity)
        {
            usb_rx_skip(1); // implausible length => spurious SOF, keep resyncing
            continue;
        }

        size_t frameLen = SOF_SIZE + HDR_SIZE + candidate.payload_len + CRC_SIZE;
        if (usb_rx_available() < frameLen)
        {
            return false; // full frame not in the ring yet; retry on the next poll
        }

        uint8_t frame[SOF_SIZE + HDR_SIZE + USB_RX_MAX_PAYLOAD + CRC_SIZE];
        usb_rx_peek(frame, frameLen);

        uint16_t crcRx = static_cast<uint16_t>(frame[frameLen - 2] | (frame[frameLen - 1] << 8));
        uint16_t crcCalc = usb_frame_crc16(frame + SOF_SIZE, HDR_SIZE + candidate.payload_len);
        if (crcRx != crcCalc)
        {
            usb_rx_skip(1); // corrupt / desynced; resync past this SOF
            continue;
        }

        usb_rx_skip(frameLen); // consume only after the frame is fully validated
        header = candidate;
        payloadLen = candidate.payload_len;
        if (payloadLen > 0)
        {
            memcpy(payload, frame + SOF_SIZE + HDR_SIZE, payloadLen);
        }
        return true;
    }

    return false;
}
