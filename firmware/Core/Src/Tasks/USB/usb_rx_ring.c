#include "Tasks/USB/usb_rx_ring.h"

/* See the header for the SPSC concurrency contract. The state lives here (not in
   usbd_cdc_if.c) so the ring is compilable without the USB middleware -- on the board the
   ISR feeds it through usb_rx_push(); in the firmware/tests/ host build the tests do. */
static uint8_t usb_rx_ring[USB_CONTROLLER_RX_BUFFER_SIZE];
static volatile size_t usb_rx_write_index = 0;
static volatile size_t usb_rx_read_index  = 0;
static volatile uint8_t usb_rx_overflow   = 0;  /* set by the ISR when a write would lap the reader */

void usb_rx_push(const uint8_t *data, uint32_t len)
{
  for (uint32_t i = 0; i < len; ++i)
  {
    size_t next = (usb_rx_write_index + 1u) % USB_CONTROLLER_RX_BUFFER_SIZE;
    if (next == usb_rx_read_index)
    {
      usb_rx_overflow = 1;
      return;
    }
    usb_rx_ring[usb_rx_write_index] = data[i];
    usb_rx_write_index = next;
  }
}

size_t usb_rx_available(void)
{
  size_t w = usb_rx_write_index;
  size_t r = usb_rx_read_index;
  return (w + USB_CONTROLLER_RX_BUFFER_SIZE - r) % USB_CONTROLLER_RX_BUFFER_SIZE;
}

size_t usb_rx_peek(uint8_t *dst, size_t n)
{
  size_t avail = usb_rx_available();
  if (n > avail) { n = avail; }
  size_t r = usb_rx_read_index;
  for (size_t i = 0; i < n; ++i)
  {
    dst[i] = usb_rx_ring[(r + i) % USB_CONTROLLER_RX_BUFFER_SIZE];
  }
  return n;
}

void usb_rx_skip(size_t n)
{
  size_t avail = usb_rx_available();
  if (n > avail) { n = avail; }
  usb_rx_read_index = (usb_rx_read_index + n) % USB_CONTROLLER_RX_BUFFER_SIZE;
}

size_t usb_rx_read(uint8_t *dst, size_t n)
{
  n = usb_rx_peek(dst, n);
  usb_rx_skip(n);
  return n;
}

int usb_rx_overflowed(void)
{
  int o = usb_rx_overflow;
  usb_rx_overflow = 0;
  return o;
}

/* Consumer-side discard of everything currently buffered. Safe under the SPSC
   contract: only the consumer advances read_index, so catching it up to a snapshot
   of the producer's write_index drops the (now desynced) contents while preserving
   any bytes that arrive afterward. Used to resync after an overflow. */
void usb_rx_flush(void)
{
  usb_rx_read_index = usb_rx_write_index;
}
