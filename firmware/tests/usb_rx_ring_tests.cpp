#include <gtest/gtest.h>

#include <cstdint>
#include <numeric>
#include <vector>

#include "Tasks/USB/usb_rx_ring.h"

namespace
{

// One slot is always left empty so a full ring is distinguishable from an empty one.
constexpr size_t kCapacity = USB_CONTROLLER_RX_BUFFER_SIZE - 1;

std::vector<uint8_t> Pattern(size_t n, uint8_t seed = 0)
{
    std::vector<uint8_t> bytes(n);
    std::iota(bytes.begin(), bytes.end(), seed);
    return bytes;
}

void Push(const std::vector<uint8_t>& bytes)
{
    usb_rx_push(bytes.data(), static_cast<uint32_t>(bytes.size()));
}

std::vector<uint8_t> ReadAll()
{
    std::vector<uint8_t> out(usb_rx_available());
    usb_rx_read(out.data(), out.size());
    return out;
}

// The ring is one static instance, exactly as on the board, so each test starts by
// draining whatever the previous one left behind. The indices deliberately keep their
// positions across tests — the ring's behavior must not depend on where they sit.
class UsbRxRingTest : public ::testing::Test
{
protected:
    void SetUp() override
    {
        usb_rx_flush();
        (void)usb_rx_overflowed();
    }
};

TEST_F(UsbRxRingTest, StartsEmpty)
{
    EXPECT_EQ(usb_rx_available(), 0u);
}

TEST_F(UsbRxRingTest, RoundTripsBytesInOrder)
{
    auto in = Pattern(100);
    Push(in);
    EXPECT_EQ(usb_rx_available(), in.size());
    EXPECT_EQ(ReadAll(), in);
    EXPECT_EQ(usb_rx_available(), 0u);
}

TEST_F(UsbRxRingTest, PeekDoesNotConsume)
{
    auto in = Pattern(16);
    Push(in);

    std::vector<uint8_t> peeked(in.size());
    EXPECT_EQ(usb_rx_peek(peeked.data(), peeked.size()), in.size());
    EXPECT_EQ(peeked, in);
    EXPECT_EQ(usb_rx_available(), in.size()); // still all there
    EXPECT_EQ(ReadAll(), in);
}

TEST_F(UsbRxRingTest, PeekReadAndSkipClampToWhatIsAvailable)
{
    Push(Pattern(4));

    uint8_t buf[32];
    EXPECT_EQ(usb_rx_peek(buf, sizeof(buf)), 4u);
    EXPECT_EQ(usb_rx_read(buf, sizeof(buf)), 4u);

    Push(Pattern(4));
    usb_rx_skip(100); // must not underflow into "everything available"
    EXPECT_EQ(usb_rx_available(), 0u);
}

TEST_F(UsbRxRingTest, HoldsExactlyCapacityBytes)
{
    Push(Pattern(kCapacity));
    EXPECT_EQ(usb_rx_available(), kCapacity);
    EXPECT_EQ(usb_rx_overflowed(), 0);
    EXPECT_EQ(ReadAll(), Pattern(kCapacity));
}

TEST_F(UsbRxRingTest, OverflowDropsTheExcessAndLatchesTheFlag)
{
    Push(Pattern(kCapacity + 10));

    // The first kCapacity bytes are kept; the overflow is reported exactly once
    // (read-and-clear), which is what lets the parser resync once per gap.
    EXPECT_EQ(usb_rx_available(), kCapacity);
    EXPECT_EQ(usb_rx_overflowed(), 1);
    EXPECT_EQ(usb_rx_overflowed(), 0);
    EXPECT_EQ(ReadAll(), Pattern(kCapacity));
}

TEST_F(UsbRxRingTest, FlushDiscardsBufferedBytesButNotFutureOnes)
{
    Push(Pattern(50));
    usb_rx_flush();
    EXPECT_EQ(usb_rx_available(), 0u);

    auto fresh = Pattern(8, 200);
    Push(fresh);
    EXPECT_EQ(ReadAll(), fresh);
}

TEST_F(UsbRxRingTest, DataSurvivesWrapAround)
{
    // 5 rounds of 300 bytes cross the 512-byte boundary several times; every byte must
    // come back in order regardless of where the indices sit.
    for (uint8_t round = 0; round < 5; ++round)
    {
        auto in = Pattern(300, static_cast<uint8_t>(round * 7));
        Push(in);
        EXPECT_EQ(ReadAll(), in) << "round " << int(round);
    }
}

TEST_F(UsbRxRingTest, PeekSpansTheWrapPoint)
{
    // Park the indices near the end of the storage array, then buffer bytes that
    // physically straddle it. Peek must reassemble them in logical order.
    Push(Pattern(USB_CONTROLLER_RX_BUFFER_SIZE - 6));
    usb_rx_skip(USB_CONTROLLER_RX_BUFFER_SIZE - 6);

    auto in = Pattern(100, 50);
    Push(in);

    std::vector<uint8_t> peeked(in.size());
    EXPECT_EQ(usb_rx_peek(peeked.data(), peeked.size()), in.size());
    EXPECT_EQ(peeked, in);
    EXPECT_EQ(ReadAll(), in);
}

} // namespace
