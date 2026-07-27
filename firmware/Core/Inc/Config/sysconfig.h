#ifndef INC_CONFIG_SYSCONFIG_H_
#define INC_CONFIG_SYSCONFIG_H_

#include <stdint.h>
#include <stdbool.h>
#include "MessagePassing/messages_public.h"

/*
 * Runtime system configuration store.
 *
 * The tunable quantities from Config/config.h (gains, task delays, thresholds) live here
 * as RAM values, seeded from those #defines by sysconfig_init() before the scheduler
 * starts. Tasks read the store every loop iteration instead of baking the macro in, so a
 * host write (USB_CMD_SET_SYSCONFIG, handled by the USB task) takes effect on the task's
 * next pass with no queueing or task wake-up involved.
 *
 * Concurrency: each value is one 32-bit aligned word, and aligned 32-bit loads/stores are
 * atomic on the Cortex-M7, so a reader can never observe a torn value. Parameters are
 * independent -- there is deliberately no multi-parameter transaction to need a lock for.
 *
 * Persistence: none on the board. The host keeps the values (SQLite on the PC) and
 * re-pushes them after every handshake; until then a freshly-booted board runs the
 * config.h defaults.
 */

#ifdef __cplusplus
extern "C" {
#endif

/* Seeds every parameter with its config.h default. Call once, before any task runs. */
void sysconfig_init(void);

/* Typed reads. The float/uint32 kind of each id is fixed (see sysconfig_param_t);
 * reading an id with the wrong getter returns that id's bits reinterpreted -- callers
 * use the getter matching the parameter's documented kind. Unknown ids read as 0. */
float    sysconfig_get_float(sysconfig_param_t id);
uint32_t sysconfig_get_u32(sysconfig_param_t id);

/* Validates raw_value (kind-aware range check, NaN/Inf rejected for floats) and stores
 * it. False -- and no store -- for an unknown id or an out-of-range value. */
bool sysconfig_set_raw(sysconfig_param_t id, uint32_t raw_value);

/* The brake duty-cycle envelope, as an ordered pair.
 *
 * SYSCFG_MIN_DUTY_CYCLE_PERCENT and SYSCFG_MAX_DUTY_CYCLE_PERCENT are written
 * independently over USB and each is range-checked against [0,1] but not against the
 * other, so a host can leave min > max. Every consumer wants the same reading of that
 * case -- the lower of the two is the floor, the higher is the ceiling -- and this is the
 * one place that decides it, so the UI cannot disagree with the actuator about what the
 * envelope is. Neither output is ever NaN, so the result is always safe to clamp with. */
void sysconfig_get_duty_cycle_limits(float *min_out, float *max_out);

#ifdef __cplusplus
}
#endif

#endif /* INC_CONFIG_SYSCONFIG_H_ */
