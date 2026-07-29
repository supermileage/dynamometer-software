#ifndef INC_TASKS_OPTICALSENSOR_ENCODER_MATH_H_
#define INC_TASKS_OPTICALSENSOR_ENCODER_MATH_H_

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Pure encoder arithmetic, deliberately free of HAL, FreeRTOS and globals so the host test suite
// compiles it directly (firmware/tests). The task around it owns the timing and the hardware; this
// file owns the numbers, which are the part worth proving.
//
// Pulses are counted in hardware: PD12 (OP_IN_CLOCK) drives TIM4's TI1FP1 in external clock mode 1,
// so TIM4's CNT *is* the pulse count. No interrupt runs per edge, which means no pulse can be lost
// to interrupt latency and a fast shaft costs no CPU at all. The task samples that counter on a
// fixed period and divides by the time actually elapsed between samples, measured with the same
// timestamp timer everything else uses.
//
// Two consequences worth stating, because they are what the numbers below have to cope with:
//
// 1. The interval is bounded by *task wake-ups*, not by pulse edges, so a partial aperture at each
//    end leaves a +/-1 count ambiguity no matter how fast the shaft turns. That error is set
//    entirely by the window: at 64 apertures it is +/-9.8 rad/s (+/-94 RPM) over a 10 ms window but
//    only +/-0.49 rad/s (+/-4.7 RPM) over 200 ms. That is why the sampling window is 200 ms --
//    see OPTICAL_ENCODER_TASK_OSDELAY -- and why lowering it trades resolution for update rate.
// 2. Below one count per window the reading is simply zero, so the same window also sets the
//    slowest detectable speed (~4.7 RPM at 200 ms). encoder_velocity_upper_bound() is what keeps
//    that floor from reading as a hard stop.

/**
 * @brief Angular velocity from the pulses counted over a sampling window.
 * @param counts       Pulses counted in the window.
 * @param delta_ticks  Timestamp ticks actually elapsed across the window (measured, not assumed --
 *                     the task's wake-ups jitter and a wrong denominator is a wrong speed).
 * @param apertures    Apertures per revolution.
 * @param ticks_per_second Timestamp timer rate.
 * @return rad/s, or 0 when the inputs cannot describe an interval.
 */
float encoder_angular_velocity(uint32_t counts,
                               uint32_t delta_ticks,
                               uint32_t apertures,
                               uint32_t ticks_per_second);

/**
 * @brief The fastest the shaft could be turning given that no pulse has arrived for this long.
 *
 * Used while counts == 0. One more aperture has not yet passed, so velocity is below one aperture
 * per the elapsed time -- a bound that decays toward zero on its own the longer the silence lasts.
 * Reporting this instead of a hard zero is what stops a slowly turning shaft from flapping between
 * "stopped" and a full quantum every time it straddles the one-count-per-window floor.
 *
 * It is a bound, not a measurement, so it must not be trusted forever: the task gives up on it
 * after OPTICAL_ENCODER_MAX_EMPTY_WINDOWS silent windows and calls the shaft stopped.
 *
 * @return rad/s upper bound, or 0 once the elapsed time is unusable.
 */
float encoder_velocity_upper_bound(uint32_t ticks_since_last_pulse,
                                   uint32_t apertures,
                                   uint32_t ticks_per_second);

/**
 * @brief Revolutions per minute from an angular velocity in rad/s.
 *
 * Everything this file produces is rad/s, which is the right unit to compute in and the wrong
 * one to read off a panel. Kept here rather than in a display driver so the conversion happens
 * once, next to the measurement it belongs to, however many panels end up showing it.
 */
float encoder_rpm(float angular_velocity);

/**
 * @brief Angular acceleration between two velocity samples.
 * @param delta_ticks Ticks between the instants the two velocities are attributed to.
 * @return rad/s^2, or 0 when no time separates them.
 */
float encoder_angular_acceleration(float previous_velocity,
                                   float velocity,
                                   uint32_t delta_ticks,
                                   uint32_t ticks_per_second);

/**
 * @brief Splice TIM4's 16-bit counter and its wrap count into one 32-bit pulse total.
 *
 * @param overflows         Wraps seen by the update ISR since boot.
 * @param counter           TIM4's CNT.
 * @param overflow_pending  TIM4's update flag was set when CNT was read -- the counter has wrapped
 *                          but the ISR has not run yet (it is masked while the task reads both),
 *                          so @p overflows is one behind the @p counter it is being paired with.
 *
 * The result deliberately wraps at 2^32 rather than saturating: it is only ever consumed as a
 * difference by encoder_count_delta(), which stays correct across that wrap. Nothing here can
 * overflow into a wrong answer -- @p overflows wrapping is the same wrap seen 65536 times sooner.
 */
uint32_t encoder_extended_count(uint32_t overflows, uint16_t counter, bool overflow_pending);

/**
 * @brief Pulses counted between two reads of the free-running total.
 *
 * The hardware counter is never reset -- resetting it would drop whatever pulses arrived between
 * reading CNT and clearing it, which biases every reading low and does so worse the faster the
 * shaft turns. Differencing a free-running counter cannot lose a pulse, and unsigned wraparound
 * makes the subtraction correct across the 2^32 boundary with no special case.
 */
uint32_t encoder_count_delta(uint32_t current_total, uint32_t previous_total);

#ifdef __cplusplus
}
#endif

#endif // INC_TASKS_OPTICALSENSOR_ENCODER_MATH_H_
