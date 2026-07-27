#include "Tasks/Display/display_common.h"

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
