#ifndef INC_TASKS_OPTICALSENSOR_OPTICALSENSOR_HPP_
#define INC_TASKS_OPTICALSENSOR_OPTICALSENSOR_HPP_

#include <cstdint>

#include "FreeRTOS.h"
#include "cmsis_os2.h"



#include "TimeKeeping/timestamps.h"
#include "Tasks/OpticalSensor/encoder_math.h"

#include "MessagePassing/messages_private.h"

#include "CircularBufferWriter.hpp"

class OpticalSensor
{
public:
    OpticalSensor(osMessageQueueId_t sessionControllerToOpticalSensorHandle);

    bool Init();
    void Run();
    
private:
    // The arithmetic lives in encoder_math.h -- pure, HAL-free, and unit tested on the host.
    CircularBufferWriter<optical_encoder_output_data> _data_buffer_writer;

    osMessageQueueId_t _sessionControllerToOpticalSensorHandle;

    const uint32_t _timestampClockSpeedFreq;

    bool _opticalEncoderEnabled;
};

#endif // INC_TASKS_OPTICALSENSOR_OPTICALSENSOR_HPP_