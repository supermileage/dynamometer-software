#include "MessagePassing/messages_public.h"
#include "Config/config.h"
#include <stddef.h>

size_t optical_encoder_circular_buffer_index_writer = 0;
size_t forcesensor_circular_buffer_index_writer = 0;
size_t bpm_circular_buffer_index_writer = 0;
size_t session_controller_circular_buffer_index_writer = 0;

optical_encoder_output_data optical_encoder_circular_buffer[OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE];
forcesensor_output_data forcesensor_circular_buffer[FORCESENSOR_CIRCULAR_BUFFER_SIZE];
bpm_output_data bpm_circular_buffer[BPM_CIRCULAR_BUFFER_SIZE];
session_controller_output_data session_controller_circular_buffer[SESSION_CONTROLLER_CIRCULAR_BUFFER_SIZE];

size_t task_error_circular_buffer_index_writer = 0;

task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

