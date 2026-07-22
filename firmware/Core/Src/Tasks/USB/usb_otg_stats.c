#include "Tasks/USB/usb_otg_stats.h"

#include "stm32h7xx_hal.h"
#include "usbd_cdc.h" /* CDC_IN_EP */

/* The data IN endpoint, as an endpoint *number* rather than an address: CDC_IN_EP is 0x81 and the
 * register file is indexed by the low nibble. */
#define USB_OTG_STATS_IN_EP (CDC_IN_EP & 0x0FU)

static volatile uint32_t s_tx_fifo_underrun = 0;
static volatile uint32_t s_in_token_fifo_empty = 0;

static USB_OTG_INEndpointTypeDef *in_endpoint(void)
{
    return (USB_OTG_INEndpointTypeDef *)((uint32_t)USB_OTG_FS + USB_OTG_IN_ENDPOINT_BASE
                                         + (USB_OTG_STATS_IN_EP * USB_OTG_EP_REG_SIZE));
}

void usb_otg_stats_sample_isr(void)
{
    USB_OTG_INEndpointTypeDef *ep = in_endpoint();
    uint32_t flags = ep->DIEPINT;

    /* Count and clear in one pass. Clearing is what keeps the count honest: TXFIFOUDRN is masked
     * out of the HAL's view and so is never cleared by it, and a latched bit left alone would be
     * re-counted on every subsequent interrupt -- which on a streaming link is thousands a second.
     *
     * Only these two bits are touched, and the HAL takes no action on either beyond clearing
     * ITTXFE. XFRC in particular is left strictly alone: the driver drives the transfer state
     * machine off it, and clearing it here would break transmission outright. */
    uint32_t handled = 0;

    if ((flags & USB_OTG_DIEPINT_TXFIFOUDRN) != 0U)
    {
        s_tx_fifo_underrun++;
        handled |= USB_OTG_DIEPINT_TXFIFOUDRN;
    }

    if ((flags & USB_OTG_DIEPINT_ITTXFE) != 0U)
    {
        s_in_token_fifo_empty++;
        handled |= USB_OTG_DIEPINT_ITTXFE;
    }

    if (handled != 0U)
    {
        ep->DIEPINT = handled; /* rc_w1: writing 1 clears */
    }
}

void usb_otg_stats_get(usb_otg_in_stats_t *out)
{
    if (out == NULL)
    {
        return;
    }
    out->tx_fifo_underrun = s_tx_fifo_underrun;
    out->in_token_fifo_empty = s_in_token_fifo_empty;
}
