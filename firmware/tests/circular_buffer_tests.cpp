// The circular buffer pair, exercised on the host. The case that matters most here is the
// multi-writer one: the task-error buffer is written by every task, each constructing its own
// CircularBufferWriter over one shared global index. A writer that initialized that index would
// discard whatever earlier-starting tasks had already logged -- and since tasks construct their
// writers as they start, the losses land exactly on boot-time errors, the ones worth seeing.

#include <gtest/gtest.h>

#include "CircularBufferReader.hpp"
#include "CircularBufferWriter.hpp"

namespace
{
constexpr size_t kSize = 8;

struct Sample
{
    uint32_t timestamp;
    uint32_t value;

    bool operator==(const Sample&) const = default;
};

// Mirrors circular_buffers.c: the storage and its index are globals owned by the buffer, not by
// any one writer, and the index starts at zero by static initialization.
struct Shared
{
    Sample buffer[kSize]{};
    size_t writerIndex = 0;
};
}

TEST(CircularBufferTest, ConstructingAWriterDoesNotDisturbTheSharedIndex)
{
    Shared shared;
    CircularBufferWriter<Sample> first(shared.buffer, &shared.writerIndex, kSize);
    first.WriteElementAndIncrementIndex({1, 100});
    first.WriteElementAndIncrementIndex({2, 200});
    ASSERT_EQ(shared.writerIndex, 2u);

    // A second task starting up and writing to the same buffer -- the task-error case.
    CircularBufferWriter<Sample> second(shared.buffer, &shared.writerIndex, kSize);
    EXPECT_EQ(shared.writerIndex, 2u) << "a late-starting writer rewound the shared index";

    second.WriteElementAndIncrementIndex({3, 300});

    // A reader that attaches afterwards still sees all three, in order.
    CircularBufferReader<Sample> reader(shared.buffer, &shared.writerIndex, kSize);
    Sample out{};
    ASSERT_TRUE(reader.GetElementAndIncrementIndex(out));
    EXPECT_EQ(out, (Sample{1, 100}));
    ASSERT_TRUE(reader.GetElementAndIncrementIndex(out));
    EXPECT_EQ(out, (Sample{2, 200}));
    ASSERT_TRUE(reader.GetElementAndIncrementIndex(out));
    EXPECT_EQ(out, (Sample{3, 300}));
    EXPECT_FALSE(reader.HasData());
}

// The scenario as it actually plays out on the board: a task logs an error at boot, several other
// tasks start afterwards and build their own writers, and the host handshakes only much later.
TEST(CircularBufferTest, ABootTimeErrorSurvivesEveryOtherTaskStartingUp)
{
    Shared shared;
    CircularBufferWriter<Sample> failingTask(shared.buffer, &shared.writerIndex, kSize);
    failingTask.WriteElementAndIncrementIndex({7, 42});

    for (int task = 0; task < 5; ++task)
    {
        CircularBufferWriter<Sample> other(shared.buffer, &shared.writerIndex, kSize);
    }

    CircularBufferReader<Sample> reader(shared.buffer, &shared.writerIndex, kSize);
    ASSERT_TRUE(reader.HasData()) << "the boot-time error was erased before the host could read it";
    Sample out{};
    ASSERT_TRUE(reader.GetElementAndIncrementIndex(out));
    EXPECT_EQ(out, (Sample{7, 42}));
}

TEST(CircularBufferTest, AFreshReaderSeesNothingUntilSomethingIsWritten)
{
    Shared shared;
    CircularBufferReader<Sample> reader(shared.buffer, &shared.writerIndex, kSize);
    EXPECT_FALSE(reader.HasData());

    Sample out{};
    EXPECT_FALSE(reader.GetElementAndIncrementIndex(out));
}

TEST(CircularBufferTest, IndicesWrapAndDataSurvivesManyLaps)
{
    Shared shared;
    CircularBufferWriter<Sample> writer(shared.buffer, &shared.writerIndex, kSize);
    CircularBufferReader<Sample> reader(shared.buffer, &shared.writerIndex, kSize);

    // Several laps around the buffer, draining as a consumer task would.
    for (uint32_t i = 0; i < kSize * 3; ++i)
    {
        writer.WriteElementAndIncrementIndex({i, i * 10});

        Sample out{};
        ASSERT_TRUE(reader.GetElementAndIncrementIndex(out)) << "lost sample " << i;
        EXPECT_EQ(out, (Sample{i, i * 10}));
    }
    EXPECT_FALSE(reader.HasData());
}

// The case this design used to lose outright. With indices that wrapped, writing exactly `size`
// elements put the writer back on an untouched reader; "has data" is that comparison, so a full
// buffer reported empty and every element in it was dropped without a trace. It is the error
// buffer's realistic shape -- a board logging faults while no host is attached to drain them --
// which is why it is worth a test rather than a note. Counting writes instead makes full and empty
// `size` apart, so the whole capacity is usable.
TEST(CircularBufferTest, ABufferFilledToExactlyCapacityIsNotMistakenForEmpty)
{
    Shared shared;
    CircularBufferWriter<Sample> writer(shared.buffer, &shared.writerIndex, kSize);
    CircularBufferReader<Sample> reader(shared.buffer, &shared.writerIndex, kSize);

    for (uint32_t i = 0; i < kSize; ++i)
    {
        writer.WriteElementAndIncrementIndex({i, i * 10});
    }
    EXPECT_EQ(shared.writerIndex, kSize) << "the index counts writes; it does not wrap";
    ASSERT_TRUE(reader.HasData()) << "a full buffer read as an empty one";

    for (uint32_t i = 0; i < kSize; ++i)
    {
        Sample out{};
        ASSERT_TRUE(reader.GetElementAndIncrementIndex(out)) << "lost sample " << i;
        EXPECT_EQ(out, (Sample{i, i * 10}));
    }
    EXPECT_FALSE(reader.HasData());
    EXPECT_EQ(reader.TakeDroppedCount(), 0u) << "nothing was overwritten";
}

// Past capacity the oldest samples are overwritten -- that is what a ring buffer is for. What is
// asserted here is that the loss is orderly: the reader rejoins at the oldest sample that still
// exists, hands back each of them once, and can say how many went by. Before, its position stayed
// where those samples used to be, so a `while (HasData())` drain walked the ring handing out the
// same slots for as many times as the writer had got ahead.
TEST(CircularBufferTest, LappingTheReaderSkipsToTheOldestSurvivorAndCountsTheLoss)
{
    Shared shared;
    CircularBufferWriter<Sample> writer(shared.buffer, &shared.writerIndex, kSize);
    CircularBufferReader<Sample> reader(shared.buffer, &shared.writerIndex, kSize);

    // Half a lap more than the buffer holds, with nobody draining.
    constexpr uint32_t kWritten = kSize + kSize / 2;
    for (uint32_t i = 0; i < kWritten; ++i)
    {
        writer.WriteElementAndIncrementIndex({i, i * 10});
    }

    // The survivors are the last kSize written, oldest first.
    for (uint32_t i = kWritten - kSize; i < kWritten; ++i)
    {
        Sample out{};
        ASSERT_TRUE(reader.GetElementAndIncrementIndex(out)) << "lost sample " << i;
        EXPECT_EQ(out, (Sample{i, i * 10}));
    }
    EXPECT_FALSE(reader.HasData()) << "the drain did not terminate at the writer";
    EXPECT_EQ(reader.TakeDroppedCount(), kWritten - kSize);
    EXPECT_EQ(reader.TakeDroppedCount(), 0u) << "reading the count should clear it";
}

// SkipBufferedSensorData()'s move: catch the reader up to the writer so a session starts on live
// data instead of flushing whatever the sensor tasks buffered while the board sat idle.
TEST(CircularBufferTest, SettingTheReaderToTheWriterDropsTheBacklog)
{
    Shared shared;
    CircularBufferWriter<Sample> writer(shared.buffer, &shared.writerIndex, kSize);
    CircularBufferReader<Sample> reader(shared.buffer, &shared.writerIndex, kSize);

    writer.WriteElementAndIncrementIndex({1, 1});
    writer.WriteElementAndIncrementIndex({2, 2});
    ASSERT_TRUE(reader.HasData());

    reader.SetIndex(shared.writerIndex);
    EXPECT_FALSE(reader.HasData());

    writer.WriteElementAndIncrementIndex({3, 3});
    Sample out{};
    ASSERT_TRUE(reader.GetElementAndIncrementIndex(out));
    EXPECT_EQ(out, (Sample{3, 3})) << "only post-skip samples should be read";
}
