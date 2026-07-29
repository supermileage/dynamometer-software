#ifndef INC_CONFIG_CONFIG_H_
#define INC_CONFIG_CONFIG_H_

#include "ADS1115_main.h"
#include "ILI9341_main.h"

// Tunable quantities below (gains, task delays, thresholds) are *boot defaults*: they
// seed the runtime sysconfig store (Config/sysconfig.h), which the host can rewrite live
// over USB (USB_CMD_SET_SYSCONFIG) -- the host re-pushes its saved values after every
// handshake. Buffer sizes are not runtime: they dimension static arrays on a heapless
// firmware, so changing them still requires a rebuild.

// Voltage Reference (should be 3V3)
#define VREF 3.3f

// The torque constants (force-sensor lever arm, moment of inertia) and the gear ratio used to
// live here. They are gone from the firmware entirely: the device streams what it measures and
// the desktop app derives torque and power, so those constants are its to keep -- editable in
// the app's PC Constants section and stored in its database. That way correcting a value
// recomputes past runs, instead of needing a rebuild and reflash to fix the next one.

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

// Rotary encoder input conditioning.
//
// The encoder is decoded the cheap way: an EXTI on ROT_EN_A, reading ROT_EN_B's level to get
// the direction. That has no filtering of any kind, so a bounced contact gives extra ticks and
// a disturbed read of B gives a tick in the wrong direction -- and a wrong direction is worse
// than a missed tick, because the brake setpoint then random-walks instead of merely lagging.
//
// Two guards, both cheap enough for interrupt context:
//   DEBOUNCE_US  -- ignore an A edge that lands within this long of the last accepted one.
//                   A hand-turned encoder produces edges milliseconds apart; contact bounce
//                   and coupled noise are microseconds. 0 disables.
//   DIRECTION_SAMPLES -- read B this many times and take the majority, so a single disturbed
//                   sample cannot decide which way the knob went. Must be odd.
#define ROTARY_ENCODER_DEBOUNCE_US 1000u
#define ROTARY_ENCODER_DIRECTION_SAMPLES 3u

// Session Controller Config
// 10ms = 100 Hz torque/power. Task delays below are tuned as a set: at the old rates the four
// streams totalled ~38 kB/s, which saturated the USB TX path once a session started (rising
// heartbeat RTTs, dropped batches); these halve the load with no visible loss on the plots.
#define SESSIONCONTROLLER_TASK_OSDELAY 10

// BPM Config
#define MIN_DUTY_CYCLE_PERCENT 0.0f
#define MAX_DUTY_CYCLE_PERCENT 0.95f
#define BPM_CIRCULAR_BUFFER_SIZE 100
// 50 Hz: the brake duty changes far slower than the sensors; no reason to stream it at 333 Hz.
#define BPM_TASK_OSDELAY 20

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
// A shaft is called stopped after this many consecutive windows with no pulses. Until then the
// task reports the decaying upper bound (encoder_math.h) instead, so a slow shaft straddling the
// one-count-per-window floor eases to zero rather than flapping. At a 200ms window, 3 windows is
// 600ms of silence, i.e. anything under ~1.6 RPM reads as stopped.
#define OPTICAL_ENCODER_MAX_EMPTY_WINDOWS 3
#define NUM_APERTURES 64 // Tied to physical 3D printed apparatus
#define OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE 100 // Need to evaluate maximum possible size from STM32
// This is the velocity sampling window, not just a loop delay: TIM4 counts pulses continuously and
// the task divides by the time between wake-ups, so the window sets both the resolution and the
// slowest detectable speed. At 64 apertures, 200ms gives +/-1 count = +/-4.7 RPM and a floor of
// ~4.7 RPM; the old 10ms window gave +/-94 RPM and saw nothing at all below that. The cost is
// update rate: 5 Hz samples instead of 100 Hz, and up to 200ms to notice an enable/disable.
#define OPTICAL_ENCODER_TASK_OSDELAY 200

// PID config
#define PID_INITIAL_STATUS false
#define PID_TASK_OSDELAY 10

// USB config
#define USB_TX_BUFFER_SIZE 512 // Buffer that is being sent to USB peripheral
// 2ms: drain in smaller, more frequent batches. At 5ms a busy session filled the 512-byte
// buffer inside a couple of passes, hitting the mid-pass flush (and its give-up drop) often.
#define USB_TASK_OSDELAY 2
// Bounded retries when flushing a full TX buffer before giving up, so a host that
// stops draining the IN endpoint can't block the USB task and starve RX/command
// handling. Each retry waits ~1ms (rides out a prior packet still in flight).
// 20: a 512-byte CDC transfer can legitimately take several ms of BUSY at full-speed USB;
// the old 5 gave up (and dropped the batch) during ordinary congestion, not just dead hosts.
#define USB_TX_FLUSH_MAX_RETRIES 20

// ===== Display =====
// Applies whichever panel is fitted. Which one that is, is a task enable in debug.h.
#define LCD_TASK_OSDELAY 20

// ===== Display: Lumex 16x2 =====
// The Lumex panel's character grid. The display message no longer carries strings -- it
// carries screen state, and the Lumex driver lays that out into a grid this size -- so these
// describe the panel itself rather than a queue payload, which is what the old
// SESSION_CONTROLLER_TO_LUMEX_LCD_MSG_STRING_SIZE was really doing.
#define LUMEX_LCD_ROWS 2
#define LUMEX_LCD_COLUMNS 16

// ===== Display: ILI9341 320x240 TFT =====
// Which way up the panel is fitted. Both LANDSCAPE and LANDSCAPE_FLIP are 320x240, so this
// changes nothing but the origin corner -- the layout is unaffected either way.
//
// FLIP because the panel is mounted 180 degrees from the controller's default landscape:
// LANDSCAPE rendered the screens upside down on the rig. This is a property of the enclosure,
// not of the driver, so it lives here rather than in ILI9341Display::Init().
#define ILI9341_DISPLAY_ROTATION ILI9341_ROTATION_LANDSCAPE_FLIP

// LED config
#define LED_TASK_OSDELAY 500

// Error and Warning settings
// 100, matching the sensor buffers. This one has the least margin of any of them despite being the
// smallest: every sensor buffer is drained each pass of the USB task, whereas errors are held back
// until a host has handshaked (deliberately -- it is what lets a boot-time fault reach whoever
// connects later), so this is the one buffer expected to hold a backlog rather than run near empty.
#define TASK_ERROR_CIRCULAR_BUFFER_SIZE 100
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
