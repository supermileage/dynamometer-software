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
    private:
        // Synthetic stand-in for one pass of the sensor streaming step, used when
        // SYSCFG_USB_MOCK_MESSAGES is on. Appends one record per stream, plus a canned error and
        // warning, to the same TX buffer the real path fills -- so everything downstream of the
        // frame builder is exercised identically and only the numbers are invented.
        void AppendMockFrames(uint32_t &timestamp);

        // How often the mock stream's canned error/warning pair goes out. The sensor records it
        // fabricates are streamed every pass, as real ones would be; the faults are not, because
        // they land in the host's event log rather than on a plot, and one per pass makes that log
        // useless for anything else.
        static constexpr uint32_t MOCK_FAULT_INTERVAL_MS = 1000;
        void StallIfIsBufferFull(bool bufferFull);
        bool IsBufferFull(std::size_t msgSize);
        void ProcessErrorsAndWarnings();

        // One framed usb_tx_batch_trailer: SOF + header + payload + CRC. Every transfer ends with
        // one, so IsBufferFull holds this much back at all times and TransmitBatch always fits.
        static constexpr std::size_t BATCH_TRAILER_FRAME_SIZE =
            2 * sizeof(uint16_t) + sizeof(usb_msg_header_t) + sizeof(usb_tx_batch_trailer);

        // Frames the usb_tx_batch_trailer that closes the pending transfer. Written last, and
        // deliberately so: the loss this instruments eats the *leading* bytes of a transfer, which
        // is exactly where a marker would be destroyed by the thing it is meant to measure.
        void AppendBatchTrailer();

        // Stamps the trailer onto the pending batch and hands it to the CDC driver. Returns
        // CDC_Transmit_FS's result; the caller still owns _txBufferIndex. Anything but USBD_OK
        // rewinds the trailer, because the batch it described was refused: a BUSY retry goes out
        // with whatever else accumulated meanwhile and must be described by one trailer, not two,
        // and a FAIL batch the caller abandons must leave no stamp behind at all.
        uint8_t TransmitBatch();

        // Frame a rate-limited WARNING_USB_TX_BATCH_DROPPED whenever give-up drops have happened,
        // so link saturation shows in the host's event log instead of being silent sample loss.
        void ReportTxDropsIfDue();

        // The circular buffers this task reads, in the order their tallies are kept.
        enum OverflowStream : uint8_t
        {
            OVERFLOW_OPTICAL_ENCODER = 0,
            OVERFLOW_FORCE_SENSOR,
            OVERFLOW_BPM,
            OVERFLOW_TASK_ERROR,
            OVERFLOW_STREAM_COUNT
        };

        // Collect each reader's overwritten-element count and frame a rate-limited warning naming
        // the buffer it happened on. An overflow here is a full lap: the producer got so far ahead
        // that elements this task had not read were written over, so the samples are gone. Which
        // buffer it is carries the diagnosis -- one stream means that producer outran the drain,
        // all four at once means this task stalled -- which is why they are counted and reported
        // separately rather than as one "something overflowed".
        void ReportBufferOverflowsIfDue();

        // How often a buffer may report an overflow. A reader that has fallen a lap behind
        // usually stays behind, and a warning per pass would then be the loudest thing on a link
        // whose problem is that it is already too busy.
        static constexpr uint32_t BUFFER_OVERFLOW_REPORT_INTERVAL_MS = 1000;

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

        // Frames one record into the TX buffer with the shared SOF/CRC envelope (v5):
        // [SOF][header][payload][crc16 over header+payload]. The caller has already reserved
        // room via IsBufferFull, which accounts for the envelope bytes.
        void AppendFrame(const usb_msg_header_t& header, const void* payload, size_t payloadLen)
        {
            const uint16_t sof = USB_FRAME_SOF;
            memcpy(_txBuffer + _txBufferIndex, &sof, sizeof(sof));
            _txBufferIndex += sizeof(sof);

            const size_t crcFrom = _txBufferIndex;
            memcpy(_txBuffer + _txBufferIndex, &header, sizeof(header));
            _txBufferIndex += sizeof(header);
            if (payloadLen > 0)
            {
                memcpy(_txBuffer + _txBufferIndex, payload, payloadLen);
                _txBufferIndex += payloadLen;
            }

            const uint16_t crc = usb_frame_crc16(_txBuffer + crcFrom, sizeof(header) + payloadLen);
            memcpy(_txBuffer + _txBufferIndex, &crc, sizeof(crc));
            _txBufferIndex += sizeof(crc);
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
                    AppendFrame(header, &data, sizeof(T));
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
                AppendFrame(header, &data, sizeof(T));
            }
        }

        CircularBufferReader<task_error_data> _task_errors_buffer_reader;
    
        CircularBufferReader<optical_encoder_output_data> _buffer_reader_optical_encoder;
        CircularBufferReader<forcesensor_output_data> _buffer_reader_forcesensor;
        CircularBufferReader<bpm_output_data> _buffer_reader_bpm;

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

        // TX batches discarded (give-up after bounded BUSY retries, or a FAIL result) since the
        // last WARNING_USB_TX_BATCH_DROPPED went out, and when that was (ReportTxDropsIfDue).
        uint32_t _txDropsPending = 0;
        uint32_t _lastDropReportTick = 0;

        // When the mock stream last emitted its canned fault pair (MOCK_FAULT_INTERVAL_MS). 0 means
        // "emit on the next pass", so a host that has just connected sees one straight away rather
        // than waiting out an interval it did not start.
        uint32_t _lastMockFaultTick = 0;

        // Per-buffer overflow accounting (ReportBufferOverflowsIfDue). Elements overwritten before
        // this task read them, awaiting a report, and when that buffer last reported one.
        uint32_t _overflowPending[OVERFLOW_STREAM_COUNT] = {};
        uint32_t _lastOverflowReportTick[OVERFLOW_STREAM_COUNT] = {};

        // Transfers the CDC driver has accepted, stamped into each usb_tx_batch_trailer. Advanced
        // on USBD_OK alone -- not on BUSY, and not on FAIL, which the same iteration of Run counts
        // as a drop -- so a batch the driver refused leaves no gap for the host to misread as a
        // transfer that vanished in flight. A sequence number the host never receives is
        // indistinguishable, from its side, from one that was sent and lost.
        uint32_t _batchSeq = 0;
};

#endif // INC_TASKS_USB_USBCONTROLLER_HPP_
