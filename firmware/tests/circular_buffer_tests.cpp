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

// Documents a real limit of this design rather than asserting a fix: with only two indices and no
// count, a buffer filled to exactly its capacity is indistinguishable from an empty one, and
// writing further silently overwrites unread samples. Producers therefore depend on the consumer
// draining faster than they fill -- the buffers are sized (100+ entries) for that margin.
TEST(CircularBufferTest, FillingToExactlyCapacityIsIndistinguishableFromEmpty)
{
    Shared shared;
    CircularBufferWriter<Sample> writer(shared.buffer, &shared.writerIndex, kSize);
    CircularBufferReader<Sample> reader(shared.buffer, &shared.writerIndex, kSize);

    for (uint32_t i = 0; i < kSize; ++i)
    {
        writer.WriteElementAndIncrementIndex({i, i * 10});
    }
    EXPECT_EQ(shared.writerIndex, 0u) << "writer index should wrap back to the start";
    EXPECT_FALSE(reader.HasData()) << "known ambiguity: a wrapped-onto-reader writer reads empty";

    // Usable depth without a drain is therefore capacity - 1.
    reader.SetIndex(shared.writerIndex);
    for (uint32_t i = 0; i < kSize - 1; ++i)
    {
        writer.WriteElementAndIncrementIndex({i, i * 10});
    }
    for (uint32_t i = 0; i < kSize - 1; ++i)
    {
        Sample out{};
        ASSERT_TRUE(reader.GetElementAndIncrementIndex(out));
        EXPECT_EQ(out, (Sample{i, i * 10}));
    }
    EXPECT_FALSE(reader.HasData());
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
