#include "Tasks/OpticalSensor/encoder_math.h"

#include <math.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

// Radians swept per aperture. Guards against a zero aperture count reaching the divide -- the
// value is host-writable through sysconfig, and a bad one must not produce an infinity that then
// propagates into torque and power.
static float radians_per_count(uint32_t apertures)
{
    if (apertures == 0u)
    {
        return 0.0f;
    }
    return (float)(2.0 * M_PI) / (float)apertures;
}

// Ticks to seconds, or 0 for an interval that cannot be divided by.
static float seconds_from_ticks(uint32_t delta_ticks, uint32_t ticks_per_second)
{
    if (delta_ticks == 0u || ticks_per_second == 0u)
    {
        return 0.0f;
    }
    return (float)delta_ticks / (float)ticks_per_second;
}

float encoder_angular_velocity(uint32_t counts,
                               uint32_t delta_ticks,
                               uint32_t apertures,
                               uint32_t ticks_per_second)
{
    float seconds = seconds_from_ticks(delta_ticks, ticks_per_second);
    if (counts == 0u || seconds <= 0.0f)
    {
        return 0.0f;
    }
    return (float)counts * radians_per_count(apertures) / seconds;
}

float encoder_velocity_upper_bound(uint32_t ticks_since_last_pulse,
                                   uint32_t apertures,
                                   uint32_t ticks_per_second)
{
    float seconds = seconds_from_ticks(ticks_since_last_pulse, ticks_per_second);
    if (seconds <= 0.0f)
    {
        return 0.0f;
    }
    return radians_per_count(apertures) / seconds;
}

float encoder_angular_acceleration(float previous_velocity,
                                   float velocity,
                                   uint32_t delta_ticks,
                                   uint32_t ticks_per_second)
{
    float seconds = seconds_from_ticks(delta_ticks, ticks_per_second);
    if (seconds <= 0.0f)
    {
        return 0.0f;
    }
    return (velocity - previous_velocity) / seconds;
}
