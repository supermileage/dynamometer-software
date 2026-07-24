#ifndef INC_TASKS_OPTICALSENSOR_ENCODER_MATH_H_
#define INC_TASKS_OPTICALSENSOR_ENCODER_MATH_H_

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Pure encoder arithmetic, deliberately free of HAL, FreeRTOS and globals so the host test suite
// compiles it directly (firmware/tests). The task around it owns the timing and the hardware; this
// file owns the numbers, which are the part worth proving.
//
// The measurement interval is bounded by real pulse edges, not by when the task happened to run.
// That is the whole point: over an interval that starts and ends on a pulse, the angle travelled
// is exactly counts * (2*pi / apertures) -- no partial aperture at either end -- so the only error
// left is the 1 us resolution of the timestamps. Sampling on a fixed task period instead leaves a
// +/-1 count ambiguity, which at 64 apertures and a 10 ms window is +/-9.8 rad/s of velocity and
// +/-982 rad/s^2 of acceleration regardless of how fast the shaft is actually turning.

/**
 * @brief Angular velocity over an interval bounded by two pulses.
 * @param counts       Pulses in the interval (the interval ends on the last of them).
 * @param delta_ticks  Timestamp ticks between the pulse that ended the previous interval and the
 *                     pulse that ended this one.
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
 * "stopped" and a full quantum, which the fixed-window counter did below ~94 RPM.
 *
 * @return rad/s upper bound, or 0 once the elapsed time is unusable.
 */
float encoder_velocity_upper_bound(uint32_t ticks_since_last_pulse,
                                   uint32_t apertures,
                                   uint32_t ticks_per_second);

/**
 * @brief Angular acceleration between two velocity samples.
 * @param delta_ticks Ticks between the instants the two velocities are attributed to.
 * @return rad/s^2, or 0 when no time separates them.
 */
float encoder_angular_acceleration(float previous_velocity,
                                   float velocity,
                                   uint32_t delta_ticks,
                                   uint32_t ticks_per_second);

#ifdef __cplusplus
}
#endif

#endif // INC_TASKS_OPTICALSENSOR_ENCODER_MATH_H_
