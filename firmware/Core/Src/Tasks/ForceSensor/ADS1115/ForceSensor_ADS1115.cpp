#include <Tasks/ForceSensor/ADS1115/forcesensor_ads1115_main.h>
#include <Tasks/ForceSensor/ADS1115/ForceSensor_ADS1115.hpp>
#include <Config/sysconfig.h>

#define LBF_TO_NEWTON 4.44822
#define ADS1115_VOLTAGE 5.1

extern I2C_HandleTypeDef* forceSensorADS1115Handle;


extern size_t forcesensor_circular_buffer_index_writer;
extern forcesensor_output_data forcesensor_circular_buffer[FORCESENSOR_CIRCULAR_BUFFER_SIZE];

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

// Global interrupts
static volatile bool ads1115_alert_status = false;

ForceSensorADS1115::ForceSensorADS1115(osMessageQueueId_t sessionControllerToForceSensorHandle,
                                       osMessageQueueId_t usbToForceSensorCommandHandle,
                                       osMessageQueueId_t taskCompletionHandle) :
		// this comes directly from circular_buffers.h and config.h
		_data_buffer_writer(forcesensor_circular_buffer, &forcesensor_circular_buffer_index_writer, FORCESENSOR_CIRCULAR_BUFFER_SIZE),
        _task_error_buffer_writer(task_error_circular_buffer, &task_error_circular_buffer_index_writer, TASK_ERROR_CIRCULAR_BUFFER_SIZE),
        _ads1115(forceSensorADS1115Handle),
		_sessionControllerToForceSensorHandle(sessionControllerToForceSensorHandle),
		_usbToForceSensorCommandHandle(usbToForceSensorCommandHandle),
		_taskCompletionHandle(taskCompletionHandle) {}

bool ForceSensorADS1115::Init()
{
    // Lay down the ADS1115 config (multiplexer, gain, data rate, mode, comparator settings)
    // from the sysconfig store rather than hard-coded constants, so the same registers are
    // owned by ReconcileConfig and stay tunable at runtime. sysconfig_init() has already
    // seeded the store from the config.h defaults, and the host may have pushed overrides.
    bool status = ReconcileConfig();

    // Route the ALERT/RDY pin as a conversion-ready output. This takes no tunable argument,
    // so it stays a fixed one-time setup rather than a sysconfig parameter, and runs after the
    // config registers exactly as before.
	status &= _ads1115.setConversionReadyPinMode();

    if (!status)
    {
        task_error_data error_data = PopulateTaskErrorDataStruct(
            get_timestamp(),
            TASK_OFFSET_FORCE_SENSOR_ADS1115,
            static_cast<uint32_t>(ERROR_FORCE_SENSOR_ADS1115_INIT_FAILURE)
        );
        _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
    }

    return status;
}

// Apply one uint8-valued ADS1115 register from sysconfig, but only when it differs from what
// the chip already holds (_applied), so an unchanged parameter costs nothing on the I2C bus.
bool ForceSensorADS1115::ApplyIfChanged(uint8_t& applied, uint8_t target,
                                        bool (ADS1115::*setter)(uint8_t))
{
    if (target == applied)
    {
        return true;
    }
    if (!(_ads1115.*setter)(target))
    {
        return false;   // leave _applied stale so the next reconcile retries this register
    }
    applied = target;
    return true;
}

// Same, for the boolean-valued registers (mode, comparator mode/polarity/latch). The stored
// code is 0/1; the driver takes a bool.
bool ForceSensorADS1115::ApplyIfChanged(uint8_t& applied, uint8_t target,
                                        bool (ADS1115::*setter)(bool))
{
    if (target == applied)
    {
        return true;
    }
    if (!(_ads1115.*setter)(target != 0))
    {
        return false;
    }
    applied = target;
    return true;
}

bool ForceSensorADS1115::ReconcileConfig()
{
    bool ok = true;
    ok &= ApplyIfChanged(_applied.mux,       static_cast<uint8_t>(sysconfig_get_u32(SYSCFG_ADS1115_MUX)),       &ADS1115::setMultiplexer);
    ok &= ApplyIfChanged(_applied.gain,      static_cast<uint8_t>(sysconfig_get_u32(SYSCFG_ADS1115_GAIN)),      &ADS1115::setGain);
    ok &= ApplyIfChanged(_applied.rate,      static_cast<uint8_t>(sysconfig_get_u32(SYSCFG_ADS1115_RATE)),      &ADS1115::setRate);
    ok &= ApplyIfChanged(_applied.comp_que,  static_cast<uint8_t>(sysconfig_get_u32(SYSCFG_ADS1115_COMP_QUE)),  &ADS1115::setComparatorQueueMode);
    ok &= ApplyIfChanged(_applied.mode,      static_cast<uint8_t>(sysconfig_get_u32(SYSCFG_ADS1115_MODE)),      &ADS1115::setMode);
    ok &= ApplyIfChanged(_applied.comp_mode, static_cast<uint8_t>(sysconfig_get_u32(SYSCFG_ADS1115_COMP_MODE)), &ADS1115::setComparatorMode);
    ok &= ApplyIfChanged(_applied.comp_pol,  static_cast<uint8_t>(sysconfig_get_u32(SYSCFG_ADS1115_COMP_POL)),  &ADS1115::setComparatorPolarity);
    ok &= ApplyIfChanged(_applied.comp_lat,  static_cast<uint8_t>(sysconfig_get_u32(SYSCFG_ADS1115_COMP_LAT)),  &ADS1115::setComparatorLatchEnabled);
    return ok;
}

void ForceSensorADS1115::Run(void)
{
    bool enableADS1115 = false;
    forcesensor_output_data outputData;

    while (1)
    {
        // Get the latest enable/disable message. When enabled, poll non-blocking;
        // when disabled, wait only a bounded time (not forever) so queued USB setting
        // commands still get serviced and applied while the sensor is idle.
        GetLatestFromQueue(_sessionControllerToForceSensorHandle,
                                            &enableADS1115,
                                            sizeof(enableADS1115),
                                            enableADS1115 ? 0 : sysconfig_get_u32(SYSCFG_FORCESENSOR_COMMAND_POLL_OSDELAY));

        // Drain and ack any queued host commands (the force sensor defines none now), in any state.
        ProcessCommands();

        // Re-apply any ADS1115 config the host changed (data rate, gain, ...) before sampling, in
        // any state, so a setting takes effect even while the sensor is idle. Only a register whose
        // value actually changed hits the I2C bus, so in steady state this is a few RAM compares.
        // On a write failure, back off and retry next pass instead of sampling with stale config.
        if (!ReconcileConfig())
        {
            task_error_data error_data = PopulateTaskErrorDataStruct(
                get_timestamp(),
                TASK_OFFSET_FORCE_SENSOR_ADS1115,
                static_cast<uint32_t>(WARNING_FORCE_SENSOR_ADS1115_CONFIG_FAILURE)
            );
            _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
            osDelay(sysconfig_get_u32(SYSCFG_TASK_WARNING_RETRY_OSDELAY));
            continue;
        }

        // If the latest message says disabled, skip sampling
        if (!enableADS1115)
        {
               continue;
        }

        ads1115_alert_status = false;

        // --- Trigger conversion ---
        if (!_ads1115.triggerConversion()) 
        {
            task_error_data error_data = PopulateTaskErrorDataStruct(
                get_timestamp(),
                TASK_OFFSET_FORCE_SENSOR_ADS1115,
                static_cast<uint32_t>(WARNING_FORCE_SENSOR_ADS1115_TRIGGER_CONVERSION_FAILURE)
            );
            
            _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
            osDelay(sysconfig_get_u32(SYSCFG_TASK_WARNING_RETRY_OSDELAY));
            continue;
        }

        // --- Wait for alert GPIO to indicate conversion complete (bounded) ---
        uint32_t alertWaitStart = osKernelGetTickCount();
        const uint32_t conversionTimeoutMs = sysconfig_get_u32(SYSCFG_FORCESENSOR_CONVERSION_TIMEOUT_MS);
        while (!ads1115_alert_status &&
               (osKernelGetTickCount() - alertWaitStart) < conversionTimeoutMs)
        {
            osDelay(1); // yield to other tasks
        }

        // Conversion-ready alert never arrived within the timeout. Record a warning and restart
        // the loop -- crucially, this returns to ProcessCommands() so queued host commands keep
        // getting applied and acked, instead of the task spinning here forever on a stuck sensor.
        if (!ads1115_alert_status)
        {
            task_error_data error_data = PopulateTaskErrorDataStruct(
                get_timestamp(),
                TASK_OFFSET_FORCE_SENSOR_ADS1115,
                static_cast<uint32_t>(WARNING_FORCE_SENSOR_ADS1115_GET_CONVERSION_FAILURE)
            );
            _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
            osDelay(sysconfig_get_u32(SYSCFG_TASK_WARNING_RETRY_OSDELAY));
            continue;
        }

        // --- Read conversion and populate output ---
        int16_t rawVal;
        if (!_ads1115.getConversion(rawVal, false)) 
        {
            task_error_data error_data = PopulateTaskErrorDataStruct(
                get_timestamp(),
                TASK_OFFSET_FORCE_SENSOR_ADS1115,
                static_cast<uint32_t>(WARNING_FORCE_SENSOR_ADS1115_GET_CONVERSION_FAILURE)
            );
            _task_error_buffer_writer.WriteElementAndIncrementIndex(error_data);
            osDelay(sysconfig_get_u32(SYSCFG_TASK_WARNING_RETRY_OSDELAY));
            continue; 
        }

        outputData.force = GetForce(rawVal);
        outputData.timestamp = get_timestamp();
        outputData.raw_value = rawVal;

        _data_buffer_writer.WriteElementAndIncrementIndex(outputData);

        osDelay(sysconfig_get_u32(SYSCFG_FORCESENSOR_TASK_OSDELAY));  // allow other tasks to run
    }
}




float ForceSensorADS1115::GetForce(uint16_t raw_value)
{
    // mv per count * count / 1000 to get volts / supply voltage to get ratio * max force in lbf * lbf to newton
    return _ads1115.getMvPerCount() * raw_value  / 1000 / ADS1115_VOLTAGE * sysconfig_get_float(SYSCFG_MAX_FORCE_LBF) * LBF_TO_NEWTON;
}

void ForceSensorADS1115::ProcessCommands()
{
    usb_task_command cmd;
    while (osMessageQueueGet(_usbToForceSensorCommandHandle, &cmd, NULL, 0) == osOK)
    {
        // The force sensor defines no command opcodes now -- its ADS1115 config is runtime
        // sysconfig applied by ReconcileConfig, not a routed command -- so a host-originated
        // command (msg_id != 0) is acked UNKNOWN rather than silently dropped. Draining the
        // queue keeps the generic per-task command path here for whatever settings come next.
        if (cmd.msg_id != 0)
        {
            usb_task_completion done;
            done.task_offset = TASK_OFFSET_FORCE_SENSOR_ADS1115;
            done.opcode = cmd.opcode;
            done.msg_id = cmd.msg_id;
            done.status = USB_RSP_UNKNOWN_COMMAND;
            osMessageQueuePut(_taskCompletionHandle, &done, 0, 0);
        }
    }
}



extern "C" void forcesensor_ads1115_gpio_alert_interrupt(void)
{
    ads1115_alert_status = true;
}

extern "C" void forcesensor_ads1115_main(osMessageQueueId_t sessionControllerToForcesensorADS1115Handle,
                                         osMessageQueueId_t usbToForceSensorCommandHandle,
                                         osMessageQueueId_t taskCompletionHandle)
{
	ForceSensorADS1115 forcesensor = ForceSensorADS1115(sessionControllerToForcesensorADS1115Handle,
	                                                    usbToForceSensorCommandHandle,
	                                                    taskCompletionHandle);

	if (!forcesensor.Init())
	{
		 osThreadSuspend(osThreadGetId());;
	}

    forcesensor.Run();

}
