#ifndef INC_TASKS_SESSION_CONTROLLER_INPUT_MANAGER_INTERRUPTS_H_
#define INC_TASKS_SESSION_CONTROLLER_INPUT_MANAGER_INTERRUPTS_H_

#include "main.h"
#include "FreeRTOS.h"
#include "Config/config.h"

#include <stdbool.h>
#include <stdint.h>


#ifdef __cplusplus
extern "C" {
#endif

// Which control produced an event.
typedef enum
{
    ROT_EN_TICKS,
    ROT_EN_SW,
    BTN_BACK,
    BTN_SELECT,
    BTN_BRAKE
} button_opcode;

// One event in the circular buffer. What `positive` means depends on the opcode:
//   ROT_EN_TICKS -- true if the encoder turned in the positive direction
//   BTN_BRAKE    -- the state of the brake button (true = pressed), reported on both edges
//   everything else -- unused; for those buttons the only information that matters is which
//   button was pressed, and only the release edge is reported at all.
typedef struct
{
    button_opcode opcode;
    bool positive;
} button_press_data;

// Where the ISRs will write next. The FSM keeps its own read position and drains up to this
// one in task context; it is the only handshake between the two.
extern volatile uint32_t interrupt_input_data_index;

// EXTI handlers, called from HAL_GPIO_EXTI_Callback in main.c.
void register_rotary_encoder_input(void);
void register_rotary_encoder_sw_input(void);
void register_button_back_input(void);
void register_button_select_input(void);
void register_button_brake_input(void);

void add_to_circular_buffer(button_opcode opcode, bool positive);
volatile button_press_data* get_circular_buffer_data(uint32_t index);


#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_SESSION_CONTROLLER_INPUT_MANAGER_INTERRUPTS_H_ */
