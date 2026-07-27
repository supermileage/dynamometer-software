// Host-test stub. messages_private.h includes cmsis_os2.h for the queue handle type used by
// other headers in that tree, but the message payloads themselves are plain data -- so the
// display renderer under test needs the struct definitions and none of the RTOS. The one
// typedef below is all the generated header's includers ask for.
#ifndef DYNO_TEST_STUB_CMSIS_OS2_H
#define DYNO_TEST_STUB_CMSIS_OS2_H

typedef void *osMessageQueueId_t;

#endif
