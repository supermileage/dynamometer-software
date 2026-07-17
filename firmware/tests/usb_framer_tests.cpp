#include <gtest/gtest.h>

#include <cstdint>
#include <cstring>
#include <vector>

#include "Tasks/USB/usb_framer.h"
#include "Tasks/USB/usb_rx_ring.h"

namespace
{

// Builds the exact envelope the host sends: [SOF][usb_msg_header_t][payload][crc16],
// little-endian, CRC over header+payload — the same layout UsbFrame.BuildCommandFrame
// produces on the C# side.
std::vector<uint8_t> FrameBytes(
    usb_msg_type_t type, task_offset_t task, const std::vector<uint8_t>& payload)
{
    usb_msg_header_t header{};
    header.msg_type = type;
    header.task_offset = task;
    header.payload_len = static_cast<uint32_t>(payload.size());

    std::vector<uint8_t> frame;
    frame.push_back(static_cast<uint8_t>(USB_FRAME_SOF & 0xFFu));
    frame.push_back(static_cast<uint8_t>(USB_FRAME_SOF >> 8));
    const auto* headerBytes = reinterpret_cast<const uint8_t*>(&header);
    frame.insert(frame.end(), headerBytes, headerBytes + sizeof(header));
    frame.insert(frame.end(), payload.begin(), payload.end());

    uint16_t crc = usb_frame_crc16(frame.data() + 2, sizeof(header) + payload.size());
    frame.push_back(static_cast<uint8_t>(crc & 0xFFu));
    frame.push_back(static_cast<uint8_t>(crc >> 8));
    return frame;
}

void Push(const std::vector<uint8_t>& bytes)
{
    usb_rx_push(bytes.data(), static_cast<uint32_t>(bytes.size()));
}

struct Parsed
{
    bool ok = false;
    usb_msg_header_t header{};
    std::vector<uint8_t> payload;
};

Parsed TryRead(size_t capacity = USB_RX_MAX_PAYLOAD)
{
    Parsed result;
    uint8_t buffer[USB_RX_MAX_PAYLOAD];
    size_t length = 0;
    result.ok = usb_framer_try_read_frame(result.header, buffer, capacity, length);
    if (result.ok)
    {
        result.payload.assign(buffer, buffer + length);
    }
    return result;
}

class UsbFramerTest : public ::testing::Test
{
protected:
    void SetUp() override
    {
        usb_rx_flush();
        (void)usb_rx_overflowed();
    }
};

TEST_F(UsbFramerTest, ParsesAWholeFrame)
{
    std::vector<uint8_t> payload = {1, 2, 3, 4};
    Push(FrameBytes(USB_MSG_COMMAND, TASK_OFFSET_USB_CONTROLLER, payload));

    auto frame = TryRead();
    ASSERT_TRUE(frame.ok);
    EXPECT_EQ(frame.header.msg_type, USB_MSG_COMMAND);
    EXPECT_EQ(frame.header.task_offset, TASK_OFFSET_USB_CONTROLLER);
    EXPECT_EQ(frame.payload, payload);

    EXPECT_FALSE(TryRead().ok); // and nothing left behind
}

TEST_F(UsbFramerTest, ParsesAZeroPayloadFrame)
{
    Push(FrameBytes(USB_MSG_COMMAND, TASK_OFFSET_USB_CONTROLLER, {}));

    auto frame = TryRead();
    ASSERT_TRUE(frame.ok);
    EXPECT_EQ(frame.header.payload_len, 0u);
    EXPECT_TRUE(frame.payload.empty());
}

TEST_F(UsbFramerTest, ParsesAMaxPayloadFrame)
{
    std::vector<uint8_t> payload(USB_RX_MAX_PAYLOAD);
    for (size_t i = 0; i < payload.size(); ++i)
    {
        payload[i] = static_cast<uint8_t>(i);
    }
    Push(FrameBytes(USB_MSG_CONFIG, TASK_OFFSET_USB_CONTROLLER, payload));

    auto frame = TryRead();
    ASSERT_TRUE(frame.ok);
    EXPECT_EQ(frame.payload, payload);
}

TEST_F(UsbFramerTest, WaitsForAFrameArrivingByteByByte)
{
    // USB CDC hands bytes over in arbitrary chunks; a frame must never be consumed (or
    // mis-parsed) until its last byte is in.
    auto bytes = FrameBytes(USB_MSG_COMMAND, TASK_OFFSET_USB_CONTROLLER, {7, 8, 9});
    for (size_t i = 0; i + 1 < bytes.size(); ++i)
    {
        Push({bytes[i]});
        EXPECT_FALSE(TryRead().ok) << "after byte " << i;
    }

    Push({bytes.back()});
    auto frame = TryRead();
    ASSERT_TRUE(frame.ok);
    EXPECT_EQ(frame.payload, (std::vector<uint8_t>{7, 8, 9}));
}

TEST_F(UsbFramerTest, ResyncsPastLeadingGarbage)
{
    Push({0x00, 0xFF, 0x13, 0x37, 0x00, 0x00, 0x21});
    Push(FrameBytes(USB_MSG_COMMAND, TASK_OFFSET_USB_CONTROLLER, {42}));

    auto frame = TryRead();
    ASSERT_TRUE(frame.ok);
    EXPECT_EQ(frame.payload, std::vector<uint8_t>{42});
}

TEST_F(UsbFramerTest, SofBytesInsideAPayloadDoNotSplitTheFrame)
{
    // The SOF marker's own bytes appearing as payload data must not be taken for the
    // start of a new frame while the enclosing frame validates.
    std::vector<uint8_t> payload = {0x5A, 0xA5, 0x5A, 0xA5, 1, 2};
    Push(FrameBytes(USB_MSG_COMMAND, TASK_OFFSET_USB_CONTROLLER, payload));

    auto frame = TryRead();
    ASSERT_TRUE(frame.ok);
    EXPECT_EQ(frame.payload, payload);
    EXPECT_FALSE(TryRead().ok);
}

TEST_F(UsbFramerTest, SkipsACorruptFrameAndFindsTheNextOne)
{
    auto corrupt = FrameBytes(USB_MSG_COMMAND, TASK_OFFSET_USB_CONTROLLER, {1, 2, 3});
    corrupt[corrupt.size() - 3] ^= 0xFF; // flip a payload byte: CRC no longer matches
    Push(corrupt);
    Push(FrameBytes(USB_MSG_COMMAND, TASK_OFFSET_USB_CONTROLLER, {9, 8, 7}));

    auto frame = TryRead();
    ASSERT_TRUE(frame.ok);
    EXPECT_EQ(frame.payload, (std::vector<uint8_t>{9, 8, 7}));
}

TEST_F(UsbFramerTest, TreatsAnOversizedLengthAsASpuriousSof)
{
    // A header claiming more than USB_RX_MAX_PAYLOAD must be resynced past even when its
    // CRC is internally consistent — otherwise it would stall the parser waiting for a
    // completion that never comes.
    usb_msg_header_t header{};
    header.msg_type = USB_MSG_COMMAND;
    header.task_offset = TASK_OFFSET_USB_CONTROLLER;
    header.payload_len = USB_RX_MAX_PAYLOAD + 1;

    std::vector<uint8_t> bogus = {
        static_cast<uint8_t>(USB_FRAME_SOF & 0xFFu),
        static_cast<uint8_t>(USB_FRAME_SOF >> 8),
    };
    const auto* headerBytes = reinterpret_cast<const uint8_t*>(&header);
    bogus.insert(bogus.end(), headerBytes, headerBytes + sizeof(header));
    uint16_t crc = usb_frame_crc16(bogus.data() + 2, sizeof(header));
    bogus.push_back(static_cast<uint8_t>(crc & 0xFFu));
    bogus.push_back(static_cast<uint8_t>(crc >> 8));

    Push(bogus);
    Push(FrameBytes(USB_MSG_COMMAND, TASK_OFFSET_USB_CONTROLLER, {5}));

    auto frame = TryRead();
    ASSERT_TRUE(frame.ok);
    EXPECT_EQ(frame.payload, std::vector<uint8_t>{5});
}

TEST_F(UsbFramerTest, RespectsTheCallersPayloadCapacity)
{
    Push(FrameBytes(USB_MSG_COMMAND, TASK_OFFSET_USB_CONTROLLER, std::vector<uint8_t>(64, 1)));

    // A frame larger than the caller's buffer is skipped as implausible, never copied.
    EXPECT_FALSE(TryRead(/*capacity=*/16).ok);
}

TEST_F(UsbFramerTest, DrainsBackToBackFrames)
{
    Push(FrameBytes(USB_MSG_COMMAND, TASK_OFFSET_USB_CONTROLLER, {1}));
    Push(FrameBytes(USB_MSG_CONFIG, TASK_OFFSET_USB_CONTROLLER, {2}));

    auto first = TryRead();
    ASSERT_TRUE(first.ok);
    EXPECT_EQ(first.header.msg_type, USB_MSG_COMMAND);
    EXPECT_EQ(first.payload, std::vector<uint8_t>{1});

    auto second = TryRead();
    ASSERT_TRUE(second.ok);
    EXPECT_EQ(second.header.msg_type, USB_MSG_CONFIG);
    EXPECT_EQ(second.payload, std::vector<uint8_t>{2});

    EXPECT_FALSE(TryRead().ok);
}

TEST_F(UsbFramerTest, FlushesTheBacklogAfterAnOverflowAndRecoversOnFreshBytes)
{
    // A buffered frame followed by an overflow: everything before the gap is suspect, so
    // the parser must discard it all rather than parse a frame that straddles the loss.
    Push(FrameBytes(USB_MSG_COMMAND, TASK_OFFSET_USB_CONTROLLER, {1, 2, 3}));
    Push(std::vector<uint8_t>(USB_CONTROLLER_RX_BUFFER_SIZE + 64, 0x00)); // force overflow

    EXPECT_FALSE(TryRead().ok); // overflow seen -> flush; nothing parseable remains

    Push(FrameBytes(USB_MSG_COMMAND, TASK_OFFSET_USB_CONTROLLER, {4, 5, 6}));
    auto frame = TryRead();
    ASSERT_TRUE(frame.ok);
    EXPECT_EQ(frame.payload, (std::vector<uint8_t>{4, 5, 6}));
}

} // namespace
