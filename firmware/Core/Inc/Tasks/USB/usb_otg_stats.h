#ifndef TASKS_USB_USB_OTG_STATS_H_
#define TASKS_USB_USB_OTG_STATS_H_

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* TEMP DIAGNOSTIC (16-byte head-loss investigation).
 *
 * The open question is whether the *device* splits a telemetry batch or whether it leaves whole
 * and the host loses the first 16 bytes. Every theory so far has been argued from host-side
 * evidence and two of them were wrong, so this asks the silicon directly.
 *
 * The OTG core has its own TX FIFO underrun flag, DIEPINT.TXFIFOUDRN, which is exactly the
 * "an IN token arrived and I did not have the whole packet to give" condition. ST's HAL disables
 * it (stm32h7xx_ll_usb.c: DIEPMSK &= ~TXFURM) and USB_ReadDevInEPInterrupt masks its DIEPINT read
 * by DIEPMSK, so the driver never reads it and never clears it -- the bit just latches. That is
 * what makes this measurable without touching vendor code: we read and clear it ourselves and the
 * HAL cannot tell the difference.
 *
 * Reading is binary. Underruns tracking the host's warnings means the device really does split
 * the batch and the firmware is in scope. Underruns staying at zero across a fault means the core
 * transmitted everything it was handed, and no amount of further firmware work can help.
 */
typedef struct
{
    uint32_t tx_fifo_underrun;    /* DIEPINT.TXFIFOUDRN -- packet transmitted short */
    uint32_t in_token_fifo_empty; /* DIEPINT.ITTXFE -- IN token with nothing queued */
} usb_otg_in_stats_t;

/* Sample and clear the latched flags. Must run at OTG ISR entry, before HAL_PCD_IRQHandler:
 * the HAL clears ITTXFE itself, so reading afterwards would miss it. */
void usb_otg_stats_sample_isr(void);

/* Snapshot for the USB task. Torn reads are not a concern -- these are counters that only ever
 * rise, and the caller compares against its own previous snapshot. */
void usb_otg_stats_get(usb_otg_in_stats_t *out);

#ifdef __cplusplus
}
#endif

#endif /* TASKS_USB_USB_OTG_STATS_H_ */
