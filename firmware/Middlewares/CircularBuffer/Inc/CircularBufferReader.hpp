#ifndef CIRCULARBUFFER_INC_CIRCULARBUFFERREADER_HPP_
#define CIRCULARBUFFER_INC_CIRCULARBUFFERREADER_HPP_

#include <stdint.h>

#include "FreeRTOS.h"
#include "task.h"

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

private:
    T* _buffer;           // external buffer memory
    size_t* _writerIndex;
    size_t _size; 
    size_t _readerIndex;
};

template <typename T>
inline CircularBufferReader<T>::CircularBufferReader(T* buffer, size_t* writerIndex, size_t size)
    : _buffer(buffer), _writerIndex(writerIndex), _size(size), _readerIndex(0)
{
}

template <typename T>
inline size_t CircularBufferReader<T>::GetIndex() const
{
    taskENTER_CRITICAL(); 
    size_t readerIndex = _readerIndex;
    taskEXIT_CRITICAL(); 
    return readerIndex;
}

template <typename T>
inline void CircularBufferReader<T>::SetIndex(size_t index)
{
    taskENTER_CRITICAL(); 
    _readerIndex = index % _size;
    taskEXIT_CRITICAL();
}

template <typename T>
inline T CircularBufferReader<T>::GetElement(size_t index) const
{
    taskENTER_CRITICAL(); 
    return _buffer[index % _size];
    taskEXIT_CRITICAL();
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

    out = _buffer[_readerIndex];
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
    out = _buffer[_readerIndex];         // read full struct
    _readerIndex = (_readerIndex + 1) % _size;
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

#endif /* CIRCULARBUFFER_INC_CIRCULARBUFFERREADER_HPP_ */