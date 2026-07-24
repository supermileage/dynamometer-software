// Host-test stub. The circular buffer headers pull in FreeRTOS only for the critical-section
// macros; the tests are single-threaded, so those collapse to nothing and the buffer logic
// (index ownership, wraparound, empty/full) is exercised as plain C++.
#ifndef DYNO_TEST_STUB_FREERTOS_H
#define DYNO_TEST_STUB_FREERTOS_H
#endif
