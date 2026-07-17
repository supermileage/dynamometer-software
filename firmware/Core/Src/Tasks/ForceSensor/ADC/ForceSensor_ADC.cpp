#include <Tasks/ForceSensor/ADC/forcesensor_adc_main.h>
#include <Tasks/ForceSensor/ADC/ForceSensor_ADC.hpp>
#include <Config/sysconfig.h>

#define LBF_TO_NEWTON 4.44822

extern ADC_HandleTypeDef* forceSensorADCHandle;

extern size_t forcesensor_circular_buffer_index_writer;
extern forcesensor_output_data forcesensor_circular_buffer[FORCESENSOR_CIRCULAR_BUFFER_SIZE];

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

// Global interrupts
static volatile uint32_t timestamp = 0;
static volatile uint16_t adc_value = 0;
static volatile bool adc_conversion_complete = false;

ForceSensorADC::ForceSensorADC(osMessageQueueId_t sessionControllerToForceSensorHandle) :
		// this comes directly from circular_buffers.h and config.h
		_data_buffer_writer(forcesensor_circular_buffer, &forcesensor_circular_buffer_index_writer, FORCESENSOR_CIRCULAR_BUFFER_SIZE),
        _task_error_buffer_writer(task_error_circular_buffer, &task_error_circular_buffer_index_writer, TASK_ERROR_CIRCULAR_BUFFER_SIZE),
		_sessionControllerToForceSensorHandle(sessionControllerToForceSensorHandle)
{}

bool ForceSensorADC::Init()
{
	return true;
}

void ForceSensorADC::Run(void)
{
    bool enableADC = false;
    forcesensor_output_data outputData;

    while (1)
    {
        // --- Get the latest enable/disable state ---
        GetLatestFromQueue(_sessionControllerToForceSensorHandle,
                                             &enableADC,
                                             sizeof(enableADC),
                                             enableADC ? 0 : osWaitForever);


        // Skip processing if the latest state says disabled
        if (!enableADC)
        {
            continue;
        }

        adc_conversion_complete = false;

        // --- Trigger ADC conversion via interrupt ---
        if (HAL_ADC_Start_IT(forceSensorADCHandle) != HAL_OK)
        {
            task_error_data error_data = PopulateTaskErrorDataStruct(
                get_timestamp(),
                TASK_OFFSET_FORCE_SENSOR_ADC,
                static_cast<uint32_t>(ERROR_FORCE_SENSOR_ADC_START_FAILURE)
            );
            
            _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
            return;
        }

        while (!adc_conversion_complete)
        {
            osDelay(1); // yield to other tasks
        }

        // --- Populate output struct ---
        outputData.timestamp = timestamp;
        outputData.force = GetForce(adc_value);
        outputData.raw_value = adc_value;

        _data_buffer_writer.WriteElementAndIncrementIndex(outputData);

        // --- Yield to other tasks ---
        osDelay(sysconfig_get_u32(SYSCFG_FORCESENSOR_TASK_OSDELAY));
    }
}




float ForceSensorADC::GetForce(uint16_t adcValue)
{
	return static_cast<float> (adcValue) / UINT16_MAX * sysconfig_get_float(SYSCFG_MAX_FORCE_LBF) * LBF_TO_NEWTON; // Have the calculation here
}


extern "C" void forcesensor_adc_interrupt()
{
	
    timestamp = get_timestamp();
	adc_value = HAL_ADC_GetValue(forceSensorADCHandle);
    adc_conversion_complete = true;
}

extern "C" void forcesensor_adc_main(osMessageQueueId_t sessionControllerToForceSensorADCHandle)
{
	ForceSensorADC forcesensor = ForceSensorADC(sessionControllerToForceSensorADCHandle);

	if (!forcesensor.Init())
	{
		 osThreadSuspend(osThreadGetId());;
	}


    forcesensor.Run();

}
