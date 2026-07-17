#ifndef INC_CONFIG_CONFIG_H_
#define INC_CONFIG_CONFIG_H_

#include "ADS1115_main.h"

// Tunable quantities below (gains, task delays, thresholds) are *boot defaults*: they
// seed the runtime sysconfig store (Config/sysconfig.h), which the host can rewrite live
// over USB (USB_CMD_SET_SYSCONFIG) -- the host re-pushes its saved values after every
// handshake. Buffer sizes are not runtime: they dimension static arrays on a heapless
// firmware, so changing them still requires a rebuild.

// Voltage Reference (should be 3V3)
#define VREF 3.3f

// Mechanical Power Calculation Constants
#define DISTANCE_FROM_FORCE_SENSOR_TO_CENTER_OF_SHAFT_M 1.0f
#define MOMENT_OF_INERTIA_KG_M2 1.0f

// Main PID controller parameters
#define K_P 1.0f
#define K_I 1.0f
#define K_D 1.0f
#define PID_MAX_OUTPUT 100.0f
#define THROTTLE_GAIN 1.0f
#define BRAKE_GAIN 1.0f
#define HORIZONTAL_BIAS 0.0f
#define VERTICAL_BIAS 0.0f

// User Input Config (like buttons)
#define USER_INPUT_CIRCULAR_BUFFER_SIZE 100u

// Session Controller Config
#define SESSIONCONTROLLER_TASK_OSDELAY 5

// BPM Config
#define MIN_DUTY_CYCLE_PERCENT 0.0f
#define MAX_DUTY_CYCLE_PERCENT 0.95f
#define BPM_CIRCULAR_BUFFER_SIZE 100
#define BPM_TASK_OSDELAY 3

// FORCE SENSOR Config
#define MAX_FORCE_LBF 25.0f
#define FORCESENSOR_TASK_OSDELAY 1
#define FORCESENSOR_CIRCULAR_BUFFER_SIZE 100
// Bounded wait (ms) on the enable queue while disabled, so USB setting commands
// are still serviced when the sensor is idle (instead of blocking forever).
#define FORCESENSOR_COMMAND_POLL_OSDELAY 50
// Bounded wait (ms) for the ADS1115 conversion-ready alert GPIO. Comfortably longer
// than one conversion even at the slowest rate (8 SPS ~= 125 ms); if it elapses the
// alert never fired, so the task abandons the sample rather than spinning forever --
// which would otherwise starve host command servicing/acks.
#define FORCESENSOR_CONVERSION_TIMEOUT_MS 250

// ADS1115 I2C config registers -- runtime-tunable via sysconfig. The force-sensor task
// re-applies a change over I2C on its next loop pass (ForceSensorADS1115::ReconcileConfig),
// so these are the *boot defaults* like the quantities above, not the only place they live.
// Each value is the register code from Drivers/ADS1115/ADS1115_main.h; the trailing comment
// names the code this default maps to (kept numeric so the host catalog can read the default).
#define ADS1115_MUX       4  // ADS1115_MUX_P0_NG            (AIN0 measured against GND)
#define ADS1115_GAIN      0  // ADS1115_PGA_6P144            (+/-6.144 V full scale)
#define ADS1115_MODE      1  // ADS1115_MODE_SINGLESHOT      (the read loop triggers each conversion)
#define ADS1115_RATE      6  // ADS1115_RATE_475            (475 SPS)
#define ADS1115_COMP_MODE 0  // ADS1115_COMP_MODE_HYSTERESIS
#define ADS1115_COMP_POL  0  // ADS1115_COMP_POL_ACTIVE_LOW
#define ADS1115_COMP_LAT  0  // ADS1115_COMP_LAT_NON_LATCHING
#define ADS1115_COMP_QUE  3  // ADS1115_COMP_QUE_DISABLE


// Optical Encoder Config
#define OPTICAL_MAX_NUM_OVERFLOWS 3 // Meant to count overflows for optical encoder
#define NUM_APERTURES 64 // Tied to physical 3D printed apparatus
#define OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE 100 // Need to evaluate maximum possible size from STM32
#define OPTICAL_ENCODER_TASK_OSDELAY 2

// PID config
#define PID_INITIAL_STATUS false
#define PID_TASK_OSDELAY 10

// USB config
#define USB_TX_BUFFER_SIZE 512 // Buffer that is being sent to USB peripheral
#define USB_TASK_OSDELAY 5
// Bounded retries when flushing a full TX buffer before giving up, so a host that
// stops draining the IN endpoint can't block the USB task and starve RX/command
// handling. Each retry waits ~1ms (rides out a prior packet still in flight).
#define USB_TX_FLUSH_MAX_RETRIES 5

// LCD config
#define LCD_TASK_OSDELAY 20
#define SESSION_CONTROLLER_TO_LUMEX_LCD_MSG_STRING_SIZE 16 + 1

// LED config
#define LED_TASK_OSDELAY 500

// Error and Warning settings
#define TASK_ERROR_CIRCULAR_BUFFER_SIZE 50
#define TASK_WARNING_RETRY_OSDELAY 100

// Task Monitor config
#define TASK_MONITOR_TASK_OSDELAY 1000





// Values changed on the desktop app's SysConfig page are written to config_overrides.h (which is
// generated, git-ignored, and absent unless something is overridden) and applied last, so they win
// over the defaults above. Nothing else in the firmware knows the difference: every consumer still
// reads the plain names below. Delete the file, or build from a clean tree, and you are back to
// exactly what this header says.
#if __has_include("config_overrides.h")
#include "config_overrides.h"
#endif

#endif /* INC_CONFIG_CONFIG_H_ */
