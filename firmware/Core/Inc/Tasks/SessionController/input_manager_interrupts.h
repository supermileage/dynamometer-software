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

// Allows the Finite State Machine to know what button was pressed
typedef enum
{
    ROT_EN_TICKS,
    ROT_EN_SW,
    BTN_BACK, 
    BTN_SELECT, 
    BTN_BRAKE
} button_opcode;

// data for each "message" in the circular buffer. opcode tells which button was pressed.
// the 'positive' field's definition changes depending on the opcode
// ROT_EN_TICKS tells the whether the rotary encoder tick was positive or negative
// BTN_BRAKE tells the status of the brake button at that time (true = pressed, false = not pressed)
// The other opcodes do not need to use this bool parameter. The only info which matters for 
// the other buttons is WHICH button was pressed
typedef struct 
{
    button_opcode opcode;
    bool positive;
} button_press_data;

extern volatile uint32_t interrupt_input_data_index;

void register_rotary_encoder_input();
void register_rotary_encoder_sw_input();
void register_button_back_input();
void register_button_select_input();
void register_button_brake_input();
void add_to_circular_buffer(button_opcode opcode, bool positive);
volatile button_press_data* get_circular_buffer_data(uint32_t index);


#ifdef __cplusplus
}
#endif

#endif /* INC_TASKS_SESSION_CONTROLLER_INPUT_MANAGER_INTERRUPTS_H_ */
