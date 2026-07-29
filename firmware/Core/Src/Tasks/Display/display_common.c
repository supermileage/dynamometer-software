#include "Tasks/Display/display_common.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>

uint32_t display_rpm_digit_increment(display_rpm_digit digit)
{
    switch (digit)
    {
        case DISPLAY_RPM_DIGIT_TEN_THOUSAND: return 10000;
        case DISPLAY_RPM_DIGIT_THOUSAND:     return 1000;
        case DISPLAY_RPM_DIGIT_HUNDRED:      return 100;
        case DISPLAY_RPM_DIGIT_TEN:          return 10;
        case DISPLAY_RPM_DIGIT_ONE:          return 1;
        default:                             return 0;
    }
}

void display_format_fixed2(char *out, size_t out_size, float value, int width)
{
    // One rounding, into hundredths, and integer formatting from there.
    const long hundredths = lroundf(value * 100.0f);

    const long whole = hundredths / 100;
    const long frac = labs(hundredths % 100);

    // Wide enough for a 64-bit long on the host test build; on the target it is 32-bit and a
    // force reading uses a handful of digits.
    char body[32];

    // Truncating toward zero loses the sign for -0.99 .. -0.01, where the whole part is 0.
    if (hundredths < 0 && whole == 0)
    {
        snprintf(body, sizeof(body), "-0.%02ld", frac);
    }
    else
    {
        snprintf(body, sizeof(body), "%ld.%02ld", whole, frac);
    }

    snprintf(out, out_size, "%*s", width, body);
}
