#ifndef CIRCULARBUFFER_INC_CIRCULARBUFFERREADER_HPP_
#define CIRCULARBUFFER_INC_CIRCULARBUFFERREADER_HPP_

#include <stdint.h>

#include "FreeRTOS.h"
#include "task.h"

// Reads a buffer whose indices count elements rather than address slots (see
// CircularBufferWriter). Two things follow from that, and both matter:
//
//  - "Has data" stays a plain inequality, but is no longer ambiguous. A buffer filled to exactly
//    its capacity used to leave the writer sitting on the reader, which reads as empty -- so the
//    whole buffer was silently dropped. Counting instead, that state is `size` apart.
//  - Being lapped becomes *detectable*: the writer is more than `size` ahead exactly when it has
//    overwritten elements this reader had not yet taken. The reader then restarts from the oldest
//    element that still exists and counts what it skipped, so a caller draining in a
//    `while (HasData())` loop reads each surviving element once, in order, rather than walking a
//    stale position round and round handing back the same slots repeatedly.
//
// Losing the oldest elements when a producer outruns a consumer is what a ring buffer is for and
// is not itself a fault; what this adds is that the loss is ordered and countable instead of
// silent. Nothing here reports it -- see TakeDroppedCount for the hook.
template <typename T>
class CircularBufferReader
{
public:
    CircularBufferReader(T* buffer, size_t* writerIndex, size_t size);

    // Index management
    size_t GetIndex() const;
    void SetIndex(size_t index);

    T GetElement(size_t index) const;
    bool GetElement(T& out) const;
    bool GetElementAndIncrementIndex(T& out);

    // New method to check if data is available
    bool HasData() const;

    // Elements the writer overwrote before this reader reached them, since the last call. Reading
    // clears it, so a caller can report a run of losses once rather than per element.
    size_t TakeDroppedCount();

private:
    // The oldest element still in the buffer: this reader's own position, unless the writer has
    // lapped it, in which case everything older than `writer - size` has already been overwritten.
    // Caller must hold the critical section -- it reads the shared write index.
    size_t OldestReadable() const;

    T* _buffer;           // external buffer memory
    size_t* _writerIndex;
    size_t _size;
    size_t _readerIndex;
    size_t _dropped;      // overwritten before being read, awaiting TakeDroppedCount
};

template <typename T>
inline CircularBufferReader<T>::CircularBufferReader(T* buffer, size_t* writerIndex, size_t size)
    : _buffer(buffer), _writerIndex(writerIndex), _size(size), _readerIndex(0), _dropped(0)
{
}

template <typename T>
inline size_t CircularBufferReader<T>::OldestReadable() const
{
    // Unsigned throughout, so this stays right across the counter's own 2^32 wrap.
    return (*_writerIndex - _readerIndex) > _size ? *_writerIndex - _size : _readerIndex;
}

template <typename T>
inline size_t CircularBufferReader<T>::GetIndex() const
{
    taskENTER_CRITICAL();
    size_t readerIndex = _readerIndex;
    taskEXIT_CRITICAL();
    return readerIndex;
}

// Takes a write count, not a slot: the value passed is one the writer produced (SkipBufferedSensorData
// hands over the writer's own position to drop a backlog), so it is stored as given.
template <typename T>
inline void CircularBufferReader<T>::SetIndex(size_t index)
{
    taskENTER_CRITICAL();
    _readerIndex = index;
    taskEXIT_CRITICAL();
}

template <typename T>
inline T CircularBufferReader<T>::GetElement(size_t index) const
{
    // The exit must precede the return. Written the other way round it is unreachable, and a
    // single call would then leak a level of uxCriticalNesting -- BASEPRI stays raised for the
    // rest of the run and every maskable interrupt in the system is dead. Nothing calls this
    // overload today, which is the only reason that has never happened.
    taskENTER_CRITICAL();
    T element = _buffer[index % _size];
    taskEXIT_CRITICAL();
    return element;
}

template <typename T>
inline bool CircularBufferReader<T>::GetElement(T& out) const
{
    taskENTER_CRITICAL();

    // empty: nothing new to read
    if (_readerIndex == *_writerIndex)
    {
        taskEXIT_CRITICAL();
        return false;
    }

    // A peek, so it corrects for a lap without consuming it: reads the oldest element that still
    // exists rather than a slot whose contents the writer has since replaced.
    out = _buffer[OldestReadable() % _size];
    taskEXIT_CRITICAL();
    return true;
}

template <typename T>
inline bool CircularBufferReader<T>::GetElementAndIncrementIndex(T& out)
{
    taskENTER_CRITICAL();                // disable context switch
    if (_readerIndex == *_writerIndex)
    {
        taskEXIT_CRITICAL();
        return false;
    }
    // Rejoin the buffer at its oldest surviving element if the writer has lapped, remembering how
    // many went past unread. Without this the position stays where the elements used to be and the
    // drain hands back the same slots for as many times as the writer got ahead.
    const size_t oldest = OldestReadable();
    _dropped += oldest - _readerIndex;
    _readerIndex = oldest;

    out = _buffer[_readerIndex % _size];  // read full struct
    ++_readerIndex;
    taskEXIT_CRITICAL();
    return true;
}

// New method implementation
template <typename T>
inline bool CircularBufferReader<T>::HasData() const
{
    taskENTER_CRITICAL();
    bool hasData = (_readerIndex != *_writerIndex);
    taskEXIT_CRITICAL();
    return hasData;
}

template <typename T>
inline size_t CircularBufferReader<T>::TakeDroppedCount()
{
    taskENTER_CRITICAL();
    size_t dropped = _dropped;
    _dropped = 0;
    taskEXIT_CRITICAL();
    return dropped;
}

#endif /* CIRCULARBUFFER_INC_CIRCULARBUFFERREADER_HPP_ */
