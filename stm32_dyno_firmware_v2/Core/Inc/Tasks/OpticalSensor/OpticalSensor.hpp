#ifndef INC_TASKS_OPTICALSENSOR_OPTICALSENSOR_HPP_
#define INC_TASKS_OPTICALSENSOR_OPTICALSENSOR_HPP_

#include <cstdint>

#include "FreeRTOS.h"
#include "cmsis_os2.h"



#include "TimeKeeping/timestamps.h"

#include "MessagePassing/messages_private.h"

#include "CircularBufferWriter.hpp"

class OpticalSensor
{
public:
    OpticalSensor(osMessageQueueId_t sessionControllerToOpticalSensorHandle);

    bool Init();
    void Run();
    
private:
    float CalculateRPM(uint32_t numCounts, uint32_t prevTimestamp, uint32_t currTimestamp);
    float CalculateAngularVelocity(uint32_t numCounts, uint32_t prevTimestamp, uint32_t currTimestamp);
    float CalculateAngularAcceleration(float prevAngularVelocity, float currAngularVelocity, uint32_t prevTimestamp, uint32_t currTimestamp);


	void ToggleOpticalEncoder(bool enable);

    CircularBufferWriter<optical_encoder_output_data> _data_buffer_writer;

    osMessageQueueId_t _sessionControllerToOpticalSensorHandle;

    const uint32_t _timestampClockSpeedFreq;

    bool _opticalEncoderEnabled;
};

#endif // INC_TASKS_OPTICALSENSOR_OPTICALSENSOR_HPP_