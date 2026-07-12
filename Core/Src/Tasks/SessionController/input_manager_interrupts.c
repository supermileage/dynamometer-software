#include <Tasks/SessionController/input_manager_interrupts.h>


// This data is used in both SessionController and this c file
volatile uint32_t interrupt_input_data_index = 0;

// Circular Buffer (only used in this c file)
volatile button_press_data button_press_circular_buffer[USER_INPUT_CIRCULAR_BUFFER_SIZE];

void register_rotary_encoder_input()
{
    // Called on a ROT_EN_A edge; the level of ROT_EN_B gives the rotation direction.
    bool positive = (HAL_GPIO_ReadPin(ROT_EN_B_GPIO_Port, ROT_EN_B_Pin) == GPIO_PIN_SET);

    add_to_circular_buffer(ROT_EN_TICKS, positive);
}

// Shared handler for momentary push-buttons (back, select): LED lights while held,
// and the press is queued on release. LEDs are active-low (RESET = on).
static void handle_momentary_button(GPIO_TypeDef* btn_port, uint16_t btn_pin,
                                    GPIO_TypeDef* led_port, uint16_t led_pin,
                                    button_opcode opcode)
{
    if (HAL_GPIO_ReadPin(btn_port, btn_pin) == GPIO_PIN_RESET)
    {
        // Pressed: turn LED on.
        HAL_GPIO_WritePin(led_port, led_pin, GPIO_PIN_RESET);
    }
    else
    {
        // Released: turn LED off and queue the event for the session controller.
        // POSITIVE is unused for these opcodes; see input_manager_interrupts.h.
        HAL_GPIO_WritePin(led_port, led_pin, GPIO_PIN_SET);
        add_to_circular_buffer(opcode, false);
    }
}

void register_rotary_encoder_sw_input()
{
	// if button is pressed
    GPIO_PinState pin_state = HAL_GPIO_ReadPin(ROT_EN_SW_GPIO_Port, ROT_EN_SW_Pin);
    if (pin_state == GPIO_PIN_RESET)
    {
        // Do nothing, can change later
        __NOP();
    }
    else
    {

        // Add to the circular buffer for the session controller to read later
        // POSITIVE parameter does not matter here for the SELECT_INPUT. Read input_manager_interrupts.h file for explanation
        add_to_circular_buffer(ROT_EN_SW, false);

    }

}

void register_button_back_input()
{
    handle_momentary_button(BTN_BACK_GPIO_Port, BTN_BACK_Pin,
                            LED_BACK_GPIO_Port, LED_BACK_Pin, BTN_BACK);
}

void register_button_select_input()
{
    handle_momentary_button(BTN_SELECT_GPIO_Port, BTN_SELECT_Pin,
                            LED_SELECT_GPIO_Port, LED_SELECT_Pin, BTN_SELECT);
}

void register_button_brake_input()
{

    // Get the current pin state of the brake button
    GPIO_PinState pin_state = HAL_GPIO_ReadPin(BTN_BRAKE_GPIO_Port, BTN_BRAKE_Pin);
    if (pin_state == GPIO_PIN_SET)
    {
        // Turn on LED
        HAL_GPIO_WritePin(LED_BRAKE_GPIO_Port, LED_BRAKE_Pin, GPIO_PIN_RESET);

        // Add to the circular buffer for the session controller to read later
        add_to_circular_buffer(BTN_BRAKE, true);
    }
    else
    {
        // Turn off LED
        HAL_GPIO_WritePin(LED_BRAKE_GPIO_Port, LED_BRAKE_Pin, GPIO_PIN_SET);

        // Add to the circular buffer for the session controller to read later
        add_to_circular_buffer(BTN_BRAKE, false);
    }
}


void add_to_circular_buffer(button_opcode opcode, bool positive)
{
    button_press_data data_to_add;
    data_to_add.opcode = opcode;
    data_to_add.positive = positive;

    // These handlers run in EXTI ISRs that can preempt one another, so the buffer write and
    // index increment must be atomic. Mask all interrupts for the (very short) critical section,
    // saving/restoring PRIMASK so we behave correctly even if called with interrupts already off.
    uint32_t primask = __get_PRIMASK();
    __disable_irq();

    button_press_circular_buffer[interrupt_input_data_index] = data_to_add;
    interrupt_input_data_index = (interrupt_input_data_index + 1) % USER_INPUT_CIRCULAR_BUFFER_SIZE;

    __set_PRIMASK(primask);
}

volatile button_press_data* get_circular_buffer_data(uint32_t index)
{
    return &button_press_circular_buffer[index];
}



