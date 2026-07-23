#ifndef CIRCULARBUFFER_INC_CIRCULARBUFFERWRITER_HPP_
#define CIRCULARBUFFER_INC_CIRCULARBUFFERWRITER_HPP_

#include <stdint.h>

#include "FreeRTOS.h"
#include "task.h"

// The shared index counts writes; it does not address the buffer.
//
// It advances without ever wrapping, and the modulo is applied only where an element is actually
// addressed. That one change is what makes a full buffer distinguishable from an empty one: with a
// wrapped index, writing exactly `size` elements lands the writer back on an untouched reader, the
// two compare equal, and every reader's "is there anything here" test -- which is that comparison
// -- reports empty. A whole buffer of unread data reads as none. Counting instead, `size` writes
// leave the writer `size` ahead, which is unambiguous at any capacity.
//
// The counter is size_t, so it wraps after 2^32 writes on this MCU. Reader-side arithmetic is
// unsigned subtraction, which stays correct across that boundary.
template <typename T>
class CircularBufferWriter
{
public:
    CircularBufferWriter(T* buffer, size_t* writerIndex, size_t size);

    // Index management
    size_t GetIndex() const;
    void SetIndex(size_t index);

    // Element write
    void WriteElement(const T& value);
    void WriteElement(size_t index, const T& value);
    void WriteElementAndIncrementIndex(const T& value);

private:
    T* _buffer;        // external buffer memory
    size_t* _writerIndex;
    size_t _size;
};

// The write index is owned by the buffer's storage (circular_buffers.c defines every index as a
// statically zero-initialized global), deliberately NOT reset here. The task-error buffer is
// written by every task, so each task constructing its own writer over the shared index would
// otherwise rewind it to 0 -- discarding whatever the tasks that started earlier had already
// logged. Boot-time errors are exactly the ones that get lost that way, and they are the ones
// most worth seeing.
template <typename T>
inline CircularBufferWriter<T>::CircularBufferWriter(T* buffer, size_t* writerIndex, size_t size)
    : _buffer(buffer), _writerIndex(writerIndex), _size(size)
{
}

template <typename T>
inline size_t CircularBufferWriter<T>::GetIndex() const
{
    taskENTER_CRITICAL();
    size_t writerIndex = *_writerIndex;
    taskEXIT_CRITICAL();
    return writerIndex;
}

// Takes a write count, not a slot. Anything a reader hands back (its own position, or a writer
// position it caught up to) is already one of these.
template <typename T>
inline void CircularBufferWriter<T>::SetIndex(size_t index)
{
    taskENTER_CRITICAL();
    *_writerIndex = index;
    taskEXIT_CRITICAL();
}


template <typename T>
inline void CircularBufferWriter<T>::WriteElement(const T& value)
{
    taskENTER_CRITICAL();
    _buffer[*_writerIndex % _size] = value;
    taskEXIT_CRITICAL();
}

template <typename T>
inline void CircularBufferWriter<T>::WriteElement(size_t index, const T& value)
{
    taskENTER_CRITICAL();
    _buffer[index % _size] = value;
    taskEXIT_CRITICAL();
}

template <typename T>
inline void CircularBufferWriter<T>::WriteElementAndIncrementIndex(const T& value)
{
    taskENTER_CRITICAL();
    _buffer[*_writerIndex % _size] = value;
    ++(*_writerIndex);
    taskEXIT_CRITICAL();
}

#endif /* CIRCULARBUFFER_INC_CIRCULARBUFFERWRITER_HPP_ */
