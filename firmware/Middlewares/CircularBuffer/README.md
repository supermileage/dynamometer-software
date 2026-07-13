---
module: CircularBuffer
summary: Heap-free, templated single-writer / multi-reader circular buffers.
code:
  - Middlewares/CircularBuffer/Inc/CircularBufferWriter.hpp
  - Middlewares/CircularBuffer/Inc/CircularBufferReader.hpp
storage: Core/Src/MessagePassing/circular_buffers.c
related: [MessagePassing, USB, SessionController]
---

# CircularBuffer — SPMC data buffers

Template classes that wrap a caller-owned array + shared writer index. Used for the
data streams that one task produces and several consume (e.g. a sensor writes, while
[[USB]] and [[SessionController]] each read at their own pace). Unlike `osMessageQueue`,
multiple independent readers are supported.

## Types
- `CircularBufferWriter<T>(T* buf, size_t* writerIndex, size_t size)` — `WriteElement(...)`, `WriteElementAndIncrementIndex(v)`.
- `CircularBufferReader<T>(T* buf, size_t* writerIndex, size_t size)` — `GetElementAndIncrementIndex(out)`, `HasData()`; keeps its **own** reader index.

## Contract
- **No heap.** Caller supplies the array + writer index (the actual arrays live in
  `Core/Src/MessagePassing/circular_buffers.c`; consumers declare them `extern`).
- One shared `writerIndex`; each reader has a private read index.
- Writes/reads run inside a critical section for consistency → keep `T` **small**.
- **No overflow protection:** a reader more than `size` behind the writer loses data; readers are assumed to keep up.

## Example
```cpp
Data buffer[N]; size_t writerIndex = 0;
CircularBufferWriter<Data> writer(buffer, &writerIndex, N);
CircularBufferReader<Data> reader(buffer, &writerIndex, N);
writer.WriteElementAndIncrementIndex({42});
Data d; if (reader.GetElementAndIncrementIndex(d)) { /* use d */ }
```

## Related
[[MessagePassing]] · [[USB]] · [[SessionController]]
