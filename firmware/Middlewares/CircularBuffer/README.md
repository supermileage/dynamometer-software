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
- `CircularBufferReader<T>(T* buf, size_t* writerIndex, size_t size)` — `GetElementAndIncrementIndex(out)`, `HasData()`, `TakeDroppedCount()`; keeps its **own** reader index.

## Contract
- **No heap.** Caller supplies the array + writer index (the actual arrays live in
  `Core/Src/MessagePassing/circular_buffers.c`; consumers declare them `extern`).
- One shared `writerIndex`; each reader has a private read index.
- Writes/reads run inside a critical section for consistency → keep `T` **small**.
- **Losing the oldest is normal, and it is countable.** A reader more than `size` behind has had
  elements overwritten — that is what a ring is for. It rejoins at the oldest element that still
  exists and adds what it skipped to `TakeDroppedCount()`, so the loss is ordered and reportable
  rather than silent. Nothing reports it today; that hook is where it would go.

## Indices count writes; they do not address slots
Both indices advance without wrapping, and `% size` is applied only where an element is addressed.
This is load-bearing, not a detail. Wrapped indices make a full buffer and an empty one the *same
state*: write exactly `size` elements and the writer lands back on an untouched reader, and
`HasData()` — which is that comparison — reports empty, so the entire buffer is dropped without a
trace. That is the realistic shape of the task-error buffer, which by design holds a backlog while
no host is attached to drain it. Counting instead leaves the writer `size` ahead, unambiguous at
any capacity, and makes "the writer has lapped me" a distance a reader can test rather than a
state it cannot see. The counters wrap after 2³² writes; the arithmetic is unsigned, so distances
stay correct across that.

Consequently a value handed between the two — `SkipBufferedSensorData()` giving a reader the
writer's position to drop a backlog — is a write count, and is stored as given rather than
reduced.

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
