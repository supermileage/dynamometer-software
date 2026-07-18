#include <Tasks/USB/usbcontroller_main.h>
#include <Tasks/USB/USBController.hpp>
#include <Tasks/USB/usb_framer.h>
#include <Config/sysconfig.h>

// The ADC and ADS1115 force sensors share one data buffer; report whichever
// variant is compiled in (exactly one is enabled, enforced in main.c).
#if FORCE_SENSOR_ADS1115_TASK_ENABLE
#define ACTIVE_FORCE_SENSOR_TASK_OFFSET TASK_OFFSET_FORCE_SENSOR_ADS1115
#elif FORCE_SENSOR_ADC_TASK_ENABLE
#define ACTIVE_FORCE_SENSOR_TASK_OFFSET TASK_OFFSET_FORCE_SENSOR_ADC
#endif

// Cadence of the device-ready announcement while waiting for the host's USB_CMD_ACK.
static constexpr uint32_t DEVICE_READY_ANNOUNCE_MS = 200;

extern size_t optical_encoder_circular_buffer_index_writer;
extern size_t forcesensor_circular_buffer_index_writer;
extern size_t bpm_circular_buffer_index_writer;
extern size_t session_controller_circular_buffer_index_writer;

extern optical_encoder_output_data optical_encoder_circular_buffer[OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE];
extern forcesensor_output_data forcesensor_circular_buffer[FORCESENSOR_CIRCULAR_BUFFER_SIZE];
extern bpm_output_data bpm_circular_buffer[BPM_CIRCULAR_BUFFER_SIZE];
extern session_controller_output_data session_controller_circular_buffer[SESSION_CONTROLLER_CIRCULAR_BUFFER_SIZE];

extern size_t task_error_circular_buffer_index_writer;
extern task_error_data task_error_circular_buffer[TASK_ERROR_CIRCULAR_BUFFER_SIZE];

USBController::USBController(osMessageQueueId_t sessionControllerToUsbController,
                             osMessageQueueId_t taskMonitorToUsbControllerHandle,
                             osMessageQueueId_t forceSensorCommandQueue,
                             osMessageQueueId_t taskCompletionQueue)
    : _task_errors_buffer_reader(task_error_circular_buffer, &task_error_circular_buffer_index_writer, TASK_ERROR_CIRCULAR_BUFFER_SIZE),
      _buffer_reader_optical_encoder(optical_encoder_circular_buffer, &optical_encoder_circular_buffer_index_writer, OPTICAL_ENCODER_CIRCULAR_BUFFER_SIZE),
      _buffer_reader_forcesensor(forcesensor_circular_buffer, &forcesensor_circular_buffer_index_writer, FORCESENSOR_CIRCULAR_BUFFER_SIZE),
      _buffer_reader_bpm(bpm_circular_buffer, &bpm_circular_buffer_index_writer, BPM_CIRCULAR_BUFFER_SIZE),
      _buffer_reader_session_controller(session_controller_circular_buffer, &session_controller_circular_buffer_index_writer, SESSION_CONTROLLER_CIRCULAR_BUFFER_SIZE),
      _taskMonitorToUsbControllerHandle(taskMonitorToUsbControllerHandle),
      _sessionControllerToUsbController(sessionControllerToUsbController),
      _forceSensorCommandQueue(forceSensorCommandQueue),
      _taskCompletionQueue(taskCompletionQueue),
      _txBuffer{},
      _txBufferIndex(0),
      _appReady(false),
      _lastAnnounceTick(0),
      _sessionStateDue(false)
{}

bool USBController::Init()
{
	return true;
}

void USBController::ProcessIncomingFrames()
{
    usb_msg_header_t header;
    uint8_t payload[USB_RX_MAX_PAYLOAD];
    size_t payloadLen = 0;

    while (TryReadFrame(header, payload, sizeof(payload), payloadLen))
    {
        DispatchFrame(header, payload, payloadLen);
    }
}

osMessageQueueId_t USBController::QueueForTaskOffset(task_offset_t taskOffset)
{
    switch (taskOffset)
    {
        case TASK_OFFSET_FORCE_SENSOR_ADS1115:
            return _forceSensorCommandQueue;
        // Add more task_offset -> command queue routes here as tasks gain settings.
        default:
            return NULL;
    }
}

void USBController::DispatchFrame(const usb_msg_header_t& header, const uint8_t* payload, size_t payloadLen)
{
    // Only COMMAND and CONFIG are valid inbound message types from the host.
    if (header.msg_type != USB_MSG_COMMAND && header.msg_type != USB_MSG_CONFIG)
    {
        return;
    }

    // Every command/config payload begins with {opcode, msg_id}.
    if (payloadLen < sizeof(usb_cmd_header_t))
    {
        return; // malformed; no msg_id to ack against, so drop silently
    }

    usb_cmd_header_t cmd;
    memcpy(&cmd, payload, sizeof(cmd));
    const uint8_t* body = payload + sizeof(cmd);
    size_t bodyLen = payloadLen - sizeof(cmd);

    // USB-controller-local commands are handled and acked here directly.
    if (header.task_offset == TASK_OFFSET_USB_CONTROLLER)
    {
        HandleUsbLocalCommand(cmd, body, bodyLen);
        return;
    }

    // Otherwise route straight to the owning task. The ack is non-posted: the task
    // applies the command and posts a completion that we relay (ProcessCompletions).
    // We only respond here for failures the task can never report.
    osMessageQueueId_t target = QueueForTaskOffset(header.task_offset);
    if (target == NULL)
    {
        SendResponse(header.task_offset, cmd.opcode, cmd.msg_id, USB_RSP_NOT_SUPPORTED);
        return;
    }
    if (bodyLen > USB_TASK_CMD_BODY_MAX)
    {
        SendResponse(header.task_offset, cmd.opcode, cmd.msg_id, USB_RSP_MALFORMED);
        return;
    }

    usb_task_command tc{};
    tc.opcode = cmd.opcode;
    tc.msg_id = cmd.msg_id;
    tc.body_len = static_cast<uint8_t>(bodyLen);
    if (bodyLen > 0)
    {
        memcpy(tc.body, body, bodyLen);
    }

    if (osMessageQueuePut(target, &tc, 0, 0) != osOK)
    {
        SendResponse(header.task_offset, cmd.opcode, cmd.msg_id, USB_RSP_QUEUE_FULL);
    }
}

void USBController::HandleUsbLocalCommand(const usb_cmd_header_t& cmd, const uint8_t* body, size_t bodyLen)
{
    switch (cmd.opcode)
    {
        case USB_CMD_ACK:
        {
            // Host acknowledges the device-ready announce. Its body carries the host's
            // protocol version; refuse the link (and keep announcing) if it disagrees with
            // ours, so a host built against an older schema never mis-decodes the stream.
            uint32_t hostVersion = 0;
            if (bodyLen >= sizeof(uint32_t))
            {
                memcpy(&hostVersion, body, sizeof(uint32_t));
            }
            if (hostVersion != USB_PROTOCOL_VERSION)
            {
                SendResponse(TASK_OFFSET_USB_CONTROLLER, cmd.opcode, cmd.msg_id, USB_RSP_VERSION_MISMATCH);
                break;
            }
            // Versions match: unblock streaming and acknowledge. Announce the session state after
            // every ack, even when nothing changed. The first ack is what a host connecting to a
            // *steady* board (idle, or already mid-session) needs -- it would otherwise wait for an
            // edge that may never come. The rest make the state self-healing: the host reuses
            // USB_CMD_ACK as its 5s keep-alive, and a host that missed enough beats to declare the
            // link lost forgets the session state while _appReady here stays set, so an edge-only
            // announcement would never reach it again and a running session would look idle
            // forever. Re-stating it every beat costs 20 bytes and closes that hole; the host
            // raises a change event only when the value actually moves, so this is silent.
            _sessionStateDue = true;
            _appReady = true;
            SendResponse(TASK_OFFSET_USB_CONTROLLER, cmd.opcode, cmd.msg_id, USB_RSP_OK);
            break;
        }

        case USB_CMD_SET_SYSCONFIG:
        {
            // Runtime parameter write. The store is plain RAM read by the owning tasks each
            // loop pass, so applying it here *is* the full application -- the OK below is as
            // truthful as a routed command's completion ack. Range violations and unknown
            // ids are rejected by the store and reported as MALFORMED.
            if (bodyLen < sizeof(sysconfig_set_param_body))
            {
                SendResponse(TASK_OFFSET_USB_CONTROLLER, cmd.opcode, cmd.msg_id, USB_RSP_MALFORMED);
                break;
            }
            sysconfig_set_param_body set;
            memcpy(&set, body, sizeof(set));
            bool applied = sysconfig_set_raw((sysconfig_param_t)set.param_id, set.raw_value);
            SendResponse(TASK_OFFSET_USB_CONTROLLER, cmd.opcode, cmd.msg_id,
                         applied ? USB_RSP_OK : USB_RSP_MALFORMED);
            break;
        }

        default:
            SendResponse(TASK_OFFSET_USB_CONTROLLER, cmd.opcode, cmd.msg_id, USB_RSP_UNKNOWN_COMMAND);
            break;
    }
}

void USBController::ProcessCompletions()
{
    usb_task_completion done;
    while (osMessageQueueGet(_taskCompletionQueue, &done, NULL, 0) == osOK)
    {
        if (done.msg_id == 0)
        {
            continue; // internal command; the host never asked for an ack
        }
        SendResponse(done.task_offset, done.opcode, done.msg_id, done.status);
    }
}

void USBController::SendResponse(task_offset_t taskOffset, uint16_t opcode, uint16_t msg_id, uint32_t status)
{
    usb_response_data_t resp = { opcode, msg_id, status };

    StallIfIsBufferFull(IsBufferFull(sizeof(resp)));

    usb_msg_header_t header =
    {
        .msg_type = USB_MSG_RESPONSE,
        .task_offset = taskOffset,
        .payload_len = sizeof(resp)
    };
    AddToBuffer<usb_msg_header_t>(&header, sizeof(header));
    AddToBuffer<usb_response_data_t>(&resp, sizeof(resp));
}

void USBController::SendDeviceReady()
{
    usb_device_ready_event evt = { USB_PROTOCOL_VERSION };

    StallIfIsBufferFull(IsBufferFull(sizeof(evt)));

    usb_msg_header_t header =
    {
        .msg_type = USB_MSG_EVENT,
        .task_offset = TASK_OFFSET_USB_CONTROLLER,
        .payload_len = sizeof(evt)
    };
    AddToBuffer<usb_msg_header_t>(&header, sizeof(header));
    AddToBuffer<usb_device_ready_event>(&evt, sizeof(evt));
}

void USBController::SendSessionState(bool inSession)
{
    session_state_event evt =
    {
        .timestamp = get_timestamp(),
        .in_session = inSession ? 1u : 0u
    };

    StallIfIsBufferFull(IsBufferFull(sizeof(evt)));

    usb_msg_header_t header =
    {
        .msg_type = USB_MSG_EVENT,
        .task_offset = TASK_OFFSET_SESSION_CONTROLLER,
        .payload_len = sizeof(evt)
    };
    AddToBuffer<usb_msg_header_t>(&header, sizeof(header));
    AddToBuffer<session_state_event>(&evt, sizeof(evt));
}

void USBController::AnnounceReadyIfDue()
{
    uint32_t now = osKernelGetTickCount();
    // First call (_lastAnnounceTick == 0) announces immediately so a host already
    // listening when the task starts is not made to wait a full interval.
    if (_lastAnnounceTick != 0 && (now - _lastAnnounceTick) < DEVICE_READY_ANNOUNCE_MS)
    {
        return;
    }
    _lastAnnounceTick = now;
    SendDeviceReady();
}

void USBController::SkipBufferedSensorData()
{
    // Catch each reader up to its writer: everything currently buffered predates the session and
    // is not part of it. Safe under the single-consumer contract -- only this task moves these
    // reader indices.
    _buffer_reader_optical_encoder.SetIndex(optical_encoder_circular_buffer_index_writer);
    _buffer_reader_forcesensor.SetIndex(forcesensor_circular_buffer_index_writer);
    _buffer_reader_bpm.SetIndex(bpm_circular_buffer_index_writer);
    _buffer_reader_session_controller.SetIndex(session_controller_circular_buffer_index_writer);
}

void USBController::HandleHostDetach()
{
    if (!usb_host_detached())
    {
        return;
    }

    // The session is over. Un-ack the link so we announce ourselves to whoever connects next
    // (AnnounceReadyIfDue only runs while !_appReady) and stop streaming into a port nobody is
    // reading. Both buffers are dropped rather than kept: a half-sent frame in _txBuffer and a
    // half-received one in the RX ring belong to the old session, and replaying either into the
    // new one is exactly how a parser ends up straddling two streams.
    _appReady = false;
    _lastAnnounceTick = 0;  // 0 means "announce immediately", not up to 200ms from now
    _txBufferIndex = 0;
    usb_rx_flush();
}

void USBController::WaitForHandshake()
{
    // Block until the host completes the USB_CMD_ACK handshake, announcing device-ready
    // and flushing the TX buffer as it goes. Used by the mock/debug path.
    while (!_appReady)
    {
        ProcessIncomingFrames();
        AnnounceReadyIfDue();

        if (_txBufferIndex > 0 && CDC_Transmit_FS(_txBuffer, _txBufferIndex) != USBD_BUSY)
        {
            _txBufferIndex = 0;
        }

        osDelay(10);
    }
}

bool USBController::TryReadFrame(usb_msg_header_t& header, uint8_t* payload, size_t payloadCapacity, size_t& payloadLen)
{
    // The scan/validate/resync logic lives in usb_framer.cpp (HAL-free, so the unit tests in
    // firmware/tests/ exercise the same code that runs here).
    return usb_framer_try_read_frame(header, payload, payloadCapacity, payloadLen);
}

void USBController::Run()
{
    bool inSession = false;
    bool prevInSession = false;

    while (1)
    {
        // 0. Notice the host closing the port. USB CDC keeps the cable enumerated across a
        //    host-side close, so without the DTR edge from CDC_SET_CONTROL_LINE_STATE we would
        //    go on believing the host we acked long ago is still listening: _appReady would
        //    stay set, the device-ready announcement would stay silenced, and the *next*
        //    connect would find a device that never introduces itself and so never handshakes.
        HandleHostDetach();

        // 1. Always service inbound commands first, so the host can handshake and
        //    configure before or after a session runs.
        ProcessIncomingFrames();

        // 2. Relay any applied-command acks the owning tasks have posted back.
        ProcessCompletions();

        // 3. Until the host handshakes, periodically announce that the device is ready so
        //    the host knows to reply USB_CMD_ACK. Self-throttled; stops once _appReady.
        if (!_appReady)
        {
            AnnounceReadyIfDue();
        }

        // 4. Pick up the SessionController's in-session flag. Sensor data is streamed only while
        //    a session runs; there is no separate on/off switch for the link, so a connected host
        //    always sees the data a running session produces.
        GetLatestFromQueue(_sessionControllerToUsbController, &inSession, sizeof(inSession), 0);

        // 5. Entering a session: skip the readers past everything the sensor tasks buffered while
        //    we were idle. They sample continuously (SessionController enables them once, at
        //    startup) and their circular buffers keep filling whether or not we drain them, so
        //    without this the first moments of a session would flush a backlog of stale samples
        //    -- data from before the session, timestamped before it, ahead of the live stream.
        if (inSession && !prevInSession)
        {
            SkipBufferedSensorData();
        }

        // 6. Announce the session state: on every start/stop, and once more to a host that has just
        //    handshaked (_sessionStateDue) even though nothing changed. Framed *before* this
        //    iteration's samples below, so a start always reaches the host ahead of the data it
        //    explains -- the host shows sensor readings only while it believes a session is running,
        //    and samples that landed before the start event would be dropped as belonging to no
        //    session. An edge that falls while no host is acked is not lost: the ack that follows
        //    sets _sessionStateDue and the current state goes out then.
        if (_appReady && (inSession != prevInSession || _sessionStateDue))
        {
            SendSessionState(inSession);
            _sessionStateDue = false;
        }
        prevInSession = inSession;

        // 7. Stream sensor data once the host has handshaked *and* a session is running.
        if (_appReady && inSession)
        {
            #if !defined(OPTICAL_ENCODER_TASK_ENABLE)
            #error "OPTICAL_ENCODER_TASK_ENABLE must be defined"
            #elif (OPTICAL_ENCODER_TASK_ENABLE == 1)
            // Process optical encoder data
            ProcessTaskData(_buffer_reader_optical_encoder, TASK_OFFSET_OPTICAL_ENCODER);
            #endif

            #if !defined(FORCE_SENSOR_ADC_TASK_ENABLE) || !defined(FORCE_SENSOR_ADS1115_TASK_ENABLE)
            #error "FORCE_SENSOR_TASK_ENABLE must be defined"
            #elif (FORCE_SENSOR_ADS1115_TASK_ENABLE || FORCE_SENSOR_ADC_TASK_ENABLE)
            // Process force sensor data
            ProcessTaskData(_buffer_reader_forcesensor, ACTIVE_FORCE_SENSOR_TASK_OFFSET);
            #endif

            #if !defined(BPM_CONTROLLER_TASK_ENABLE)
            #error "BPM_CONTROLLER_TASK_ENABLE must be defined"
            #elif (BPM_CONTROLLER_TASK_ENABLE == 1)
            // Process BPM data
            ProcessTaskData(_buffer_reader_bpm, TASK_OFFSET_BPM_CONTROLLER);
            #endif

            #if !defined(SESSION_CONTROLLER_TASK_ENABLE)
            #error "SESSION_CONTROLLER_TASK_ENABLE must be defined"
            #elif (SESSION_CONTROLLER_TASK_ENABLE == 1)
            // Process derived torque/power data
            ProcessTaskData(_buffer_reader_session_controller, TASK_OFFSET_SESSION_CONTROLLER);
            #endif
        }

        // 8. Health and faults are *not* sensor data: they are streamed to any handshaked host,
        //    session or not. A task dying, or a stack running out, is most worth seeing while the
        //    dyno sits idle -- and draining the task-monitor queue regardless also stops it from
        //    filling up and dropping samples between sessions.
        if (_appReady)
        {
            #if !defined(TASK_MONITOR_TASK_ENABLE)
            #error "TASK_MONITOR_TASK_ENABLE must be defined"
            #elif (TASK_MONITOR_TASK_ENABLE == 1)
            // Process Task Monitor data
            ProcessTaskData<task_monitor_output_data>(_taskMonitorToUsbControllerHandle, TASK_OFFSET_TASK_MONITOR);
            #endif

            ProcessErrorsAndWarnings();
            ReportTxDropsIfDue();
        }

        // 9. Flush whatever accumulated this iteration: command responses, session events and/or
        //    stream records. Nothing is transmitted when the buffer is empty.
        if (_txBufferIndex > 0)
        {
            uint8_t result = CDC_Transmit_FS(_txBuffer, _txBufferIndex);
            if (result == USBD_BUSY)
            {
                osDelay(sysconfig_get_u32(SYSCFG_USB_TASK_OSDELAY));
                continue; // host busy; keep the buffer and retry next iteration
            }
            if (result != USBD_OK)
            {
                _txDropsPending++; // FAIL (e.g. device not configured): the batch never left
            }
            _txBufferIndex = 0;
        }

        osDelay(sysconfig_get_u32(SYSCFG_USB_TASK_OSDELAY));
    }
}



// void USBController::Run()
// {
// 	uint8_t msg[6];

// 	for (;;)
// 	{
// 	    msg[0] = 'B';
// 	    msg[1] = HAL_GPIO_ReadPin(BTN_BRAKE_GPIO_Port,  BTN_BRAKE_Pin)  ? '1' : '0';
// 	    msg[2] = 'S';
// 	    msg[3] = HAL_GPIO_ReadPin(BTN_SELECT_GPIO_Port, BTN_SELECT_Pin) ? '1' : '0';
// 	    msg[4] = 'K';
// 	    msg[5] = '\n';

// 	    while (CDC_Transmit_FS(msg, sizeof(msg)) == USBD_BUSY)
// 	    {
// 	        osDelay(1);
// 	    }

// 	    osDelay(100);
// 	}

// }

void USBController::MockMessages(const bool forever)
{

    uint32_t timestamp = 0;
    usb_msg_header_t usb_header{};

    // Wait for the host handshake before starting data transmission
    WaitForHandshake();

    // There is no SessionController in the mock path, but the host only displays sensor data for a
    // running session -- so say one is running, or the mock stream below arrives and is discarded.
    SendSessionState(true);

    while(forever)
    {
        #if !defined(OPTICAL_ENCODER_TASK_ENABLE)
        #error "OPTICAL_ENCODER_TASK_ENABLE must be defined"
        #elif (OPTICAL_ENCODER_TASK_ENABLE == 1)
        static float angular_velocity = 0.0f;
        static uint32_t optical_raw_value = 0;
        static float angular_acceleration = 0.0f;
        optical_encoder_output_data mock_data = {
            .timestamp = timestamp++,
            .angular_velocity = angular_velocity++,
            .raw_value = optical_raw_value++,
            .angular_acceleration = angular_acceleration++
        };
        // Process optical encoder data
        usb_header.msg_type = USB_MSG_STREAM;
        usb_header.task_offset = TASK_OFFSET_OPTICAL_ENCODER;
        usb_header.payload_len = sizeof(optical_encoder_output_data);

        AddToBuffer<usb_msg_header_t>(&usb_header, sizeof(usb_msg_header_t));
        AddToBuffer<optical_encoder_output_data>(&mock_data, sizeof(optical_encoder_output_data));
        #endif

        #if !defined(FORCE_SENSOR_ADS1115_TASK_ENABLE) || !defined(FORCE_SENSOR_ADC_TASK_ENABLE)
        #error "FORCE_SENSOR_TASK_ENABLE must be defined"
        #elif (FORCE_SENSOR_ADS1115_TASK_ENABLE || FORCE_SENSOR_ADC_TASK_ENABLE)
        static float force = 0.0f;
        static uint32_t fs_raw_value = 0;
        forcesensor_output_data mock_fs_data = {
            .timestamp = timestamp++,
            .force = force++,
            .raw_value = fs_raw_value++
        };
        usb_header.msg_type = USB_MSG_STREAM;
        usb_header.task_offset = ACTIVE_FORCE_SENSOR_TASK_OFFSET;
        usb_header.payload_len = sizeof(forcesensor_output_data);

        AddToBuffer<usb_msg_header_t>(&usb_header, sizeof(usb_msg_header_t));
        AddToBuffer<forcesensor_output_data>(&mock_fs_data, sizeof(forcesensor_output_data));
        #endif

        #if !defined(BPM_CONTROLLER_TASK_ENABLE)
        #error "BPM_CONTROLLER_TASK_ENABLE must be defined"
        #elif (BPM_CONTROLLER_TASK_ENABLE == 1)
        static float duty_cycle = 0.0f;
        static uint32_t bpm_raw_value = 0;
        bpm_output_data mock_bpm_data = {
            .timestamp = timestamp++,
            .duty_cycle = duty_cycle++,
            .raw_value = bpm_raw_value++
        };
        usb_header.msg_type = USB_MSG_STREAM;
        usb_header.task_offset = TASK_OFFSET_BPM_CONTROLLER;
        usb_header.payload_len = sizeof(bpm_output_data);
        AddToBuffer<usb_msg_header_t>(&usb_header, sizeof(usb_msg_header_t));
        AddToBuffer<bpm_output_data>(&mock_bpm_data, sizeof(bpm_output_data));
        #endif

        #if !defined(SESSION_CONTROLLER_TASK_ENABLE)
        #error "SESSION_CONTROLLER_TASK_ENABLE must be defined"
        #elif (SESSION_CONTROLLER_TASK_ENABLE == 1)
        static float torque = 0.0f;
        static float power = 0.0f;
        session_controller_output_data mock_sc_data = {
            .timestamp = timestamp++,
            .torque = torque++,
            .power = power++
        };
        usb_header.msg_type = USB_MSG_STREAM;
        usb_header.task_offset = TASK_OFFSET_SESSION_CONTROLLER;
        usb_header.payload_len = sizeof(session_controller_output_data);
        AddToBuffer<usb_msg_header_t>(&usb_header, sizeof(usb_msg_header_t));
        AddToBuffer<session_controller_output_data>(&mock_sc_data, sizeof(session_controller_output_data));
        #endif

        #if !defined(TASK_MONITOR_TASK_ENABLE)
        #error "TASK_MONITOR_TASK_ENABLE must be defined"
        #elif (TASK_MONITOR_TASK_ENABLE == 1)
        static uint32_t task_monitor_raw_value = 0;

        task_monitor_output_data mock_tm_data = {
            .timestamp = timestamp++,
            .task_offset = TASK_OFFSET_NO_TASK,
            .task_state = 0,
            .free_bytes = task_monitor_raw_value++
        };
        usb_header.msg_type = USB_MSG_STREAM;
        usb_header.task_offset = TASK_OFFSET_TASK_MONITOR;
        usb_header.payload_len = sizeof(task_monitor_output_data);
        AddToBuffer<usb_msg_header_t>(&usb_header, sizeof(usb_msg_header_t));
        AddToBuffer<task_monitor_output_data>(&mock_tm_data, sizeof(task_monitor_output_data));
        #endif

        task_error_data mock_error_data = 
        PopulateTaskErrorDataStruct(
            timestamp++,
            TASK_OFFSET_SESSION_CONTROLLER,
            ERROR_SESSION_CONTROLLER_TIMESTAMP_TIMER_START_FAILURE
        );

        usb_header.msg_type = USB_MSG_ERROR;
        usb_header.task_offset = TASK_OFFSET_SESSION_CONTROLLER;
        usb_header.payload_len = sizeof(task_error_data);

        AddToBuffer<usb_msg_header_t>(&usb_header, sizeof(usb_header));
        AddToBuffer<task_error_data>(&mock_error_data, sizeof(mock_error_data));

        task_error_data mock_warning_data = PopulateTaskErrorDataStruct(
            timestamp++,
            TASK_OFFSET_FORCE_SENSOR_ADS1115,
            WARNING_FORCE_SENSOR_ADS1115_TRIGGER_CONVERSION_FAILURE
        );

        usb_header.msg_type = USB_MSG_WARNING;
        usb_header.task_offset = TASK_OFFSET_FORCE_SENSOR_ADS1115;
        usb_header.payload_len = sizeof(task_error_data);

        AddToBuffer<usb_msg_header_t>(&usb_header, sizeof(usb_header));
        AddToBuffer<task_error_data>(&mock_warning_data, sizeof(mock_warning_data));


        if (CDC_Transmit_FS(_txBuffer, _txBufferIndex) == USBD_BUSY) {
            continue;
        }
        _txBufferIndex = 0;

       
        osDelay(sysconfig_get_u32(SYSCFG_USB_TASK_OSDELAY));
    }
}

void USBController::ProcessErrorsAndWarnings()
{
    while(_task_errors_buffer_reader.HasData()) 
    {

        StallIfIsBufferFull(IsBufferFull(sizeof(task_error_data)));

        task_error_data error_data;
        if (_task_errors_buffer_reader.GetElementAndIncrementIndex(error_data)) {
            usb_msg_header_t header = 
            {
                .msg_type = (error_data.error_code & WARNING_FLAG) ? USB_MSG_WARNING : USB_MSG_ERROR,
                .task_offset = (task_offset_t)(error_data.error_code & TASK_OFFSET_MASK),
                .payload_len = sizeof(task_error_data)
            };

            AddToBuffer<usb_msg_header_t>(&header, sizeof(header));
            AddToBuffer<task_error_data>(&error_data, sizeof(task_error_data));
        }
    }
}

void USBController::StallIfIsBufferFull(bool bufferFull)
{
    if (!bufferFull) {
        return;
    }
    // Make room by flushing, but never block the task indefinitely. This runs on the
    // single-threaded USB task, so spinning here while a host stops draining the IN
    // endpoint would also starve RX/command handling. Retry a bounded number of times
    // to ride out transient BUSY (a prior packet still in flight), then give up and drop
    // the buffered batch so the loop can keep servicing inbound commands. Telemetry is
    // lossy by nature; a dropped command response is recovered by the host's ack-timeout
    // retry.
    const uint32_t maxRetries = sysconfig_get_u32(SYSCFG_USB_TX_FLUSH_MAX_RETRIES);
    for (uint32_t attempt = 0; attempt < maxRetries; ++attempt) {
        if (CDC_Transmit_FS(_txBuffer, _txBufferIndex) != USBD_BUSY) {
            _txBufferIndex = 0;
            return;
        }
        osDelay(1);
    }
    _txBufferIndex = 0; // host not draining; drop this batch and move on
    _txDropsPending++;
}

void USBController::ReportTxDropsIfDue()
{
    // At most one warning per second while batches are being dropped: enough for the host's
    // event log to show a saturated link without the report itself adding to the saturation.
    if (_txDropsPending == 0)
    {
        return;
    }
    uint32_t now = osKernelGetTickCount();
    if (_lastDropReportTick != 0 && (now - _lastDropReportTick) < 1000)
    {
        return;
    }
    if (IsBufferFull(sizeof(task_error_data)))
    {
        return; // no room without flushing (which is what just failed); try again next pass
    }

    task_error_data warning = PopulateTaskErrorDataStruct(
        get_timestamp(),
        TASK_OFFSET_USB_CONTROLLER,
        static_cast<uint32_t>(WARNING_USB_TX_BATCH_DROPPED)
    );
    usb_msg_header_t header =
    {
        .msg_type = USB_MSG_WARNING,
        .task_offset = TASK_OFFSET_USB_CONTROLLER,
        .payload_len = sizeof(task_error_data)
    };
    AddToBuffer<usb_msg_header_t>(&header, sizeof(header));
    AddToBuffer<task_error_data>(&warning, sizeof(warning));

    _lastDropReportTick = now;
    _txDropsPending = 0;
}

bool USBController::IsBufferFull(std::size_t msgSize)
{   
    if (_txBufferIndex + sizeof(usb_msg_header_t) + msgSize >= USB_TX_BUFFER_SIZE) {
        return true;   
    }
	return false;

}

extern "C" void usbcontroller_main(osMessageQueueId_t sessionControllerToUsbController,
                                   osMessageQueueId_t taskMonitorToUsbControllerHandle,
                                   osMessageQueueId_t forceSensorCommandQueue,
                                   osMessageQueueId_t taskCompletionQueue)
{
	USBController usb = USBController(sessionControllerToUsbController, taskMonitorToUsbControllerHandle,
	                                  forceSensorCommandQueue, taskCompletionQueue);

	if (!usb.Init())
	{
		 osThreadSuspend(osThreadGetId());;
	}

    #if !defined(DEBUG_USB_CONTROLLER_MOCK_MESSAGES)
    #error "DEBUG_USB_CONTROLLER_MOCK_MESSAGES must be defined"
    #elif (DEBUG_USB_CONTROLLER_MOCK_MESSAGES == 1)
    usb.MockMessages();
    #else
	usb.Run();
    #endif
}
