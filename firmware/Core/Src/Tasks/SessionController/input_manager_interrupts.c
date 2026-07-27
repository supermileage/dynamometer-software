// Button and rotary-encoder ISRs.
//
// These run in interrupt context and do as little as possible: read the pin, drive the button's
// LED, and append one event to a circular buffer. The FSM drains that buffer in task context
// (FSM::HandleUserInputs), so no UI work ever happens inside an interrupt.

#include <Tasks/SessionController/input_manager_interrupts.h>


// Where the ISRs will write next; the FSM reads it to know how far to drain. Declared in the
// header because that handshake is the FSM's business.
volatile uint32_t interrupt_input_data_index = 0;

// The events themselves are reached only through get_circular_buffer_data().
static volatile button_press_data button_press_circular_buffer[USER_INPUT_CIRCULAR_BUFFER_SIZE];

// The button LEDs are wired active-low: pulling the pin low lights them.
#define LED_ON  GPIO_PIN_RESET
#define LED_OFF GPIO_PIN_SET


// Shared body of the three push buttons. They differ in only two ways, both passed in:
//
//   pressed_level -- BACK and SELECT are active-low, BRAKE is active-high.
//   report_press  -- BACK and SELECT report only the release edge, so a press is nothing but
//                    an LED change. BRAKE reports both edges, because the FSM runs a session
//                    for exactly as long as the button is held.
static void register_button(GPIO_TypeDef* button_port, uint16_t button_pin,
                            GPIO_TypeDef* led_port, uint16_t led_pin,
                            GPIO_PinState pressed_level,
                            button_opcode opcode, bool report_press)
{
    const bool pressed = (HAL_GPIO_ReadPin(button_port, button_pin) == pressed_level);

    HAL_GPIO_WritePin(led_port, led_pin, pressed ? LED_ON : LED_OFF);

    if (report_press || !pressed)
    {
        add_to_circular_buffer(opcode, pressed);
    }
}


// Called on an edge of ROT_EN_A; ROT_EN_B's level at that moment gives the direction.
void register_rotary_encoder_input(void)
{
    const bool positive = (HAL_GPIO_ReadPin(ROT_EN_B_GPIO_Port, ROT_EN_B_Pin) != GPIO_PIN_RESET);

    add_to_circular_buffer(ROT_EN_TICKS, positive);
}

// The encoder's push switch. Reported on release only, like the other buttons, and it has no
// LED of its own -- so it does not go through register_button().
void register_rotary_encoder_sw_input(void)
{
    const bool pressed = (HAL_GPIO_ReadPin(ROT_EN_SW_GPIO_Port, ROT_EN_SW_Pin) == GPIO_PIN_RESET);

    if (!pressed)
    {
        add_to_circular_buffer(ROT_EN_SW, false);
    }
}

void register_button_back_input(void)
{
    register_button(BTN_BACK_GPIO_Port, BTN_BACK_Pin, LED_BACK_GPIO_Port, LED_BACK_Pin,
                    GPIO_PIN_RESET, BTN_BACK, false);
}

void register_button_select_input(void)
{
    register_button(BTN_SELECT_GPIO_Port, BTN_SELECT_Pin, LED_SELECT_GPIO_Port, LED_SELECT_Pin,
                    GPIO_PIN_RESET, BTN_SELECT, false);
}

void register_button_brake_input(void)
{
    register_button(BTN_BRAKE_GPIO_Port, BTN_BRAKE_Pin, LED_BRAKE_GPIO_Port, LED_BRAKE_Pin,
                    GPIO_PIN_SET, BTN_BRAKE, true);
}


// Appends one event. No interrupt masking around the write: every button and encoder line is
// configured at the same NVIC preemption priority (main.c, MX_GPIO_Init), and equal-priority
// interrupts cannot preempt one another on Cortex-M, so two of these can never interleave.
// The element is written before the index advances, so the FSM -- which only ever reads up to
// the index it saw -- cannot observe a half-written event.
void add_to_circular_buffer(button_opcode opcode, bool positive)
{
    button_press_data data_to_add;
    data_to_add.opcode = opcode;
    data_to_add.positive = positive;

    button_press_circular_buffer[interrupt_input_data_index] = data_to_add;
    interrupt_input_data_index = (interrupt_input_data_index + 1) % USER_INPUT_CIRCULAR_BUFFER_SIZE;
}

volatile button_press_data* get_circular_buffer_data(uint32_t index)
{
    return &button_press_circular_buffer[index];
}
