#include "Config/sysconfig.h"
#include "Config/config.h"

#include <math.h>
#include <stddef.h>

/* One parameter: its kind, valid range, config.h default, and the live value.
 * def/min/max are unions so float entries can be written in natural units; the live
 * value is kept as raw bits in a single volatile 32-bit word (see sysconfig.h for why
 * that is enough for cross-task safety). */
typedef union
{
    float f;
    uint32_t u;
} sysconfig_value_t;

typedef struct
{
    bool is_float;
    sysconfig_value_t def;
    sysconfig_value_t min;
    sysconfig_value_t max;
    volatile uint32_t raw;
} sysconfig_entry_t;

#define SYSCFG_F32(deflt, mn, mx) { .is_float = true,  .def = { .f = (deflt) }, .min = { .f = (mn) }, .max = { .f = (mx) }, .raw = 0u }
#define SYSCFG_U32(deflt, mn, mx) { .is_float = false, .def = { .u = (deflt) }, .min = { .u = (mn) }, .max = { .u = (mx) }, .raw = 0u }

/* The entries are generated from the sysconfig_params section of the message schema
 * (tools/message_gen/schema/messages_public.yaml) -- the same source that defines
 * sysconfig_param_t and the host's catalog -- so id, kind and range can never drift
 * between firmware and host. Defaults are the config.h macros, expanded here. */
static sysconfig_entry_t s_table[SYSCFG_PARAM_COUNT] =
{
#include "Config/sysconfig_table.inc"
};

void sysconfig_init(void)
{
    for (size_t i = 0; i < SYSCFG_PARAM_COUNT; ++i)
    {
        /* .def was written through the union's natural member, so .def.u is already the
         * value's bit pattern whichever kind it is. */
        s_table[i].raw = s_table[i].def.u;
    }
}

float sysconfig_get_float(sysconfig_param_t id)
{
    if (id >= SYSCFG_PARAM_COUNT)
    {
        return 0.0f;
    }
    sysconfig_value_t v;
    v.u = s_table[id].raw;
    return v.f;
}

uint32_t sysconfig_get_u32(sysconfig_param_t id)
{
    if (id >= SYSCFG_PARAM_COUNT)
    {
        return 0u;
    }
    return s_table[id].raw;
}

bool sysconfig_set_raw(sysconfig_param_t id, uint32_t raw_value)
{
    if (id >= SYSCFG_PARAM_COUNT)
    {
        return false;
    }

    sysconfig_entry_t *entry = &s_table[id];
    if (entry->is_float)
    {
        sysconfig_value_t v;
        v.u = raw_value;
        if (!isfinite(v.f) || v.f < entry->min.f || v.f > entry->max.f)
        {
            return false;
        }
    }
    else if (raw_value < entry->min.u || raw_value > entry->max.u)
    {
        return false;
    }

    entry->raw = raw_value;
    return true;
}
