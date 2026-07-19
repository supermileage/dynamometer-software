#ifndef CIRCULARBUFFER_INC_CIRCULARBUFFERWRITER_HPP_
#define CIRCULARBUFFER_INC_CIRCULARBUFFERWRITER_HPP_

#include <stdint.h>

#include "FreeRTOS.h"
#include "task.h"

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

template <typename T>
inline void CircularBufferWriter<T>::SetIndex(size_t index)
{
    taskENTER_CRITICAL(); 
    *_writerIndex = index % _size;
    taskEXIT_CRITICAL(); 
}


template <typename T>
inline void CircularBufferWriter<T>::WriteElement(const T& value)
{
    taskENTER_CRITICAL(); 
    _buffer[*_writerIndex] = value;
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
    _buffer[*_writerIndex] = value;
    *_writerIndex = (*_writerIndex + 1) % _size;
    taskEXIT_CRITICAL(); 
}

#endif /* CIRCULARBUFFER_INC_CIRCULARBUFFERWRITER_HPP_ */
