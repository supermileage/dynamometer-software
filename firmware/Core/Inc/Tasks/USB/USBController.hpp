#ifndef INC_TASKS_USB_USBCONTROLLER_HPP_
#define INC_TASKS_USB_USBCONTROLLER_HPP_

#include <array>
#include <algorithm>

#include "cmsis_os2.h"

#include "usbd_cdc_if.h"

#include "TimeKeeping/timestamps.h"

#include "MessagePassing/messages_private.h"
#include "MessagePassing/messages_public.h"
#include "MessagePassing/osqueue_helpers.h"
#include "MessagePassing/messages_public.h"

#include "Config/config.h"
#include "Config/debug.h"
#include "CircularBufferReader.hpp"



class USBController
{
    public:
        USBController(osMessageQueueId_t sessionControllerToUsbController,
                      osMessageQueueId_t taskMonitorToUsbControllerHandle,
                      osMessageQueueId_t forceSensorCommandQueue,
                      osMessageQueueId_t taskCompletionQueue);
        ~USBController() = default; // Destructor

        bool Init();
        void Run();
        void MockMessages(const bool forever = true);
    private:
        void StallIfIsBufferFull(bool bufferFull);   
        bool IsBufferFull(std::size_t msgSize);
        void ProcessErrorsAndWarnings();

        // Pulls one complete, CRC-validated inbound frame out of the USB RX ring.
        // Returns true and fills header/payload/payloadLen when a frame is ready;
        // returns false when no complete frame is available yet (non-blocking).
        // Garbage / corrupt bytes are skipped so the stream resyncs after an overflow.
        bool TryReadFrame(usb_msg_header_t& header, uint8_t* payload, size_t payloadCapacity, size_t& payloadLen);

        // Drain every complete inbound frame and route it. Safe to call each loop
        // regardless of logging state, so the host can configure at any time.
        void ProcessIncomingFrames();
        void DispatchFrame(const usb_msg_header_t& header, const uint8_t* payload, size_t payloadLen);
        void HandleUsbLocalCommand(const usb_cmd_header_t& cmd, const uint8_t* body, size_t bodyLen);

        // Maps a frame's task_offset to the owning task's command queue, or NULL if
        // that module has no command route. The USB task stays a pure router.
        osMessageQueueId_t QueueForTaskOffset(task_offset_t taskOffset);

        // Drain task completion queue and frame a USB_MSG_RESPONSE for each (far end
        // of the full-path ack). Completions with msg_id 0 (internal) are dropped.
        void ProcessCompletions();

        // Frame a USB_MSG_RESPONSE into the TX buffer (echoes opcode/msg_id + status).
        void SendResponse(task_offset_t taskOffset, uint16_t opcode, uint16_t msg_id, uint32_t status);

        // Frame a USB_MSG_EVENT carrying usb_device_ready_event{USB_PROTOCOL_VERSION}. The
        // host watches for this and replies USB_CMD_ACK to start the link.
        void SendDeviceReady();

        // Frame a USB_MSG_EVENT carrying session_state_event{in_session}. Sensor data only leaves
        // the board during a session, so this is what tells the host whether the silence it sees is
        // an idle dyno or a broken one -- and, at a start, that the samples now arriving are live.
        void SendSessionState(bool inSession);

        // While the host has not yet handshaked, re-announce device-ready at most every
        // DEVICE_READY_ANNOUNCE_MS so a host that connects late still sees one. No-op once ready.
        void AnnounceReadyIfDue();

        // Un-ack the link when the host closes the port (CDC DTR drops), so the device announces
        // itself to the next host instead of staying silent forever. No-op while a host is attached.
        void HandleHostDetach();

        // Catch the sensor readers up to their writers, dropping whatever the (continuously
        // sampling) sensor tasks buffered while no session was running. Called on session entry so
        // a session opens with live data rather than a backlog from before it started.
        void SkipBufferedSensorData();

        // Block until the host completes the USB_CMD_ACK handshake (mock/debug path).
        void WaitForHandshake();

        template <typename T>
        void AddToBuffer(T* msg, size_t msgSize) {
            memcpy(_txBuffer + _txBufferIndex, msg, msgSize); 
            _txBufferIndex += msgSize;
        }

        template <typename T>
        void ProcessTaskData(CircularBufferReader<T>& bufferReader, task_offset_t taskId)
        {            
            T data; // Temporary variable to hold the data
            while (bufferReader.HasData()) { // Check if data is available
                // Ensure the buffer is not full before adding data
                StallIfIsBufferFull(IsBufferFull(sizeof(T)));

                if (bufferReader.GetElementAndIncrementIndex(data)) {
                    usb_msg_header_t header = 
                    {
                        .msg_type = USB_MSG_STREAM,
                        .task_offset = taskId,
                        .payload_len = sizeof(T)
                    };
                    AddToBuffer<usb_msg_header_t>(&header, sizeof(usb_msg_header_t));
                    AddToBuffer<T>(&data, sizeof(T));
                }
            }
        }

        template <typename T>
        void ProcessTaskData(osMessageQueueId_t msgqHandle, task_offset_t taskId)
        {            
            T data; // Temporary variable to hold the data
            while (osMessageQueueGet(msgqHandle, &data, 0, 0) == osOK) { // Check if data is available
                // Ensure the buffer is not full before adding data
                StallIfIsBufferFull(IsBufferFull(sizeof(T)));

                usb_msg_header_t header = 
                {
                    .msg_type = USB_MSG_STREAM,
                    .task_offset = taskId,
                    .payload_len = sizeof(T)
                };
                AddToBuffer<usb_msg_header_t>(&header, sizeof(usb_msg_header_t));
                AddToBuffer<T>(&data, sizeof(T));
                
            }
        }

        CircularBufferReader<task_error_data> _task_errors_buffer_reader;
    
        CircularBufferReader<optical_encoder_output_data> _buffer_reader_optical_encoder;
        CircularBufferReader<forcesensor_output_data> _buffer_reader_forcesensor;
        CircularBufferReader<bpm_output_data> _buffer_reader_bpm;
        CircularBufferReader<session_controller_output_data> _buffer_reader_session_controller;

        osMessageQueueId_t _taskMonitorToUsbControllerHandle;
        osMessageQueueId_t _sessionControllerToUsbController;  // carries the in-session flag
        osMessageQueueId_t _forceSensorCommandQueue;   // route target for force-sensor settings
        osMessageQueueId_t _taskCompletionQueue;       // shared: tasks post applied-command acks here

        uint8_t _txBuffer[USB_TX_BUFFER_SIZE];
        int _txBufferIndex = 0;

        bool _appReady;            // set once the host completes the USB_CMD_ACK handshake
        uint32_t _lastAnnounceTick; // tick of the last device-ready announce (AnnounceReadyIfDue)

        // Set by every host ack: the next loop re-states the session state even though nothing
        // changed, so a host that just connected (or one that lost and regained the link) is never
        // left waiting on an edge it cannot see.
        bool _sessionStateDue;
};

#endif // INC_TASKS_USB_USBCONTROLLER_HPP_
