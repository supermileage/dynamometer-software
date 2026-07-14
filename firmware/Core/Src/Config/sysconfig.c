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

/* osDelay(0) only yields, so a 0 here would turn a task loop into a near-busy spin that
 * starves lower-priority tasks; 1 tick is the smallest honest delay. The 60s cap stops a
 * mistyped value from making a task look dead for minutes. */
#define OSDELAY_MIN 1u
#define OSDELAY_MAX 60000u

static sysconfig_entry_t s_table[SYSCFG_PARAM_COUNT] =
{
    [SYSCFG_DISTANCE_FROM_FORCE_SENSOR_TO_CENTER_OF_SHAFT_M] = SYSCFG_F32(DISTANCE_FROM_FORCE_SENSOR_TO_CENTER_OF_SHAFT_M, 1e-6f, 1000.0f),
    [SYSCFG_MOMENT_OF_INERTIA_KG_M2]                         = SYSCFG_F32(MOMENT_OF_INERTIA_KG_M2, 1e-9f, 1e6f),
    [SYSCFG_K_P]                                             = SYSCFG_F32(K_P, -1e6f, 1e6f),
    [SYSCFG_K_I]                                             = SYSCFG_F32(K_I, -1e6f, 1e6f),
    [SYSCFG_K_D]                                             = SYSCFG_F32(K_D, -1e6f, 1e6f),
    [SYSCFG_PID_MAX_OUTPUT]                                  = SYSCFG_F32(PID_MAX_OUTPUT, 1e-6f, 1e6f),
    [SYSCFG_THROTTLE_GAIN]                                   = SYSCFG_F32(THROTTLE_GAIN, -1e6f, 1e6f),
    [SYSCFG_BRAKE_GAIN]                                      = SYSCFG_F32(BRAKE_GAIN, -1e6f, 1e6f),
    [SYSCFG_HORIZONTAL_BIAS]                                 = SYSCFG_F32(HORIZONTAL_BIAS, -1e6f, 1e6f),
    [SYSCFG_VERTICAL_BIAS]                                   = SYSCFG_F32(VERTICAL_BIAS, -1e6f, 1e6f),
    [SYSCFG_MIN_DUTY_CYCLE_PERCENT]                          = SYSCFG_F32(MIN_DUTY_CYCLE_PERCENT, 0.0f, 1.0f),
    [SYSCFG_MAX_DUTY_CYCLE_PERCENT]                          = SYSCFG_F32(MAX_DUTY_CYCLE_PERCENT, 0.0f, 1.0f),
    [SYSCFG_MAX_FORCE_LBF]                                   = SYSCFG_F32(MAX_FORCE_LBF, 1e-3f, 1e5f),
    [SYSCFG_SESSIONCONTROLLER_TASK_OSDELAY]                  = SYSCFG_U32(SESSIONCONTROLLER_TASK_OSDELAY, OSDELAY_MIN, OSDELAY_MAX),
    [SYSCFG_BPM_TASK_OSDELAY]                                = SYSCFG_U32(BPM_TASK_OSDELAY, OSDELAY_MIN, OSDELAY_MAX),
    [SYSCFG_FORCESENSOR_TASK_OSDELAY]                        = SYSCFG_U32(FORCESENSOR_TASK_OSDELAY, OSDELAY_MIN, OSDELAY_MAX),
    [SYSCFG_FORCESENSOR_COMMAND_POLL_OSDELAY]                = SYSCFG_U32(FORCESENSOR_COMMAND_POLL_OSDELAY, OSDELAY_MIN, OSDELAY_MAX),
    [SYSCFG_FORCESENSOR_CONVERSION_TIMEOUT_MS]               = SYSCFG_U32(FORCESENSOR_CONVERSION_TIMEOUT_MS, 1u, OSDELAY_MAX),
    [SYSCFG_OPTICAL_ENCODER_TASK_OSDELAY]                    = SYSCFG_U32(OPTICAL_ENCODER_TASK_OSDELAY, OSDELAY_MIN, OSDELAY_MAX),
    [SYSCFG_NUM_APERTURES]                                   = SYSCFG_U32(NUM_APERTURES, 1u, 100000u),
    [SYSCFG_PID_TASK_OSDELAY]                                = SYSCFG_U32(PID_TASK_OSDELAY, OSDELAY_MIN, OSDELAY_MAX),
    [SYSCFG_USB_TASK_OSDELAY]                                = SYSCFG_U32(USB_TASK_OSDELAY, OSDELAY_MIN, OSDELAY_MAX),
    [SYSCFG_USB_TX_FLUSH_MAX_RETRIES]                        = SYSCFG_U32(USB_TX_FLUSH_MAX_RETRIES, 0u, 1000u),
    [SYSCFG_LCD_TASK_OSDELAY]                                = SYSCFG_U32(LCD_TASK_OSDELAY, OSDELAY_MIN, OSDELAY_MAX),
    [SYSCFG_LED_TASK_OSDELAY]                                = SYSCFG_U32(LED_TASK_OSDELAY, OSDELAY_MIN, OSDELAY_MAX),
    [SYSCFG_TASK_WARNING_RETRY_OSDELAY]                      = SYSCFG_U32(TASK_WARNING_RETRY_OSDELAY, OSDELAY_MIN, OSDELAY_MAX),
    [SYSCFG_TASK_MONITOR_TASK_OSDELAY]                       = SYSCFG_U32(TASK_MONITOR_TASK_OSDELAY, OSDELAY_MIN, OSDELAY_MAX),
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
