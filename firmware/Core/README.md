---
module: Core
summary: Firmware application — FreeRTOS tasks, message passing, and STM32H743 hardware bring-up.
entry: Core/Src/main.c
related: [MessagePassing, SessionController, USB, TaskMonitor, BPM, PID, LCD, Display, ForceSensor, OpticalSensor, Config, TimeKeeping]
---

# Core — application firmware

`Core/Src/main.c` is CubeMX-generated hardware + FreeRTOS bring-up plus `USER CODE`
that creates the tasks, queues, and the USART1 mutex, and wires peripheral handles.
Each task runs an `<x>_main()` entry, owns a single class, and talks to the rest of
the system **only** through CMSIS-RTOS queues and the shared [[CircularBuffer]] arrays —
never by calling into another task directly.

## Module index
| Module | Doc | Role |
|---|---|---|
| SessionController | `Core/Src/Tasks/SessionController/README.md` | Orchestrator: FSM, task coordination, LCD readout |
| BPM | `Core/Src/Tasks/BPM/README.md` | Motor PWM / duty-cycle |
| PID | `Core/Src/Tasks/PID/README.md` | Closed-loop brake control from encoder feedback |
| ForceSensor | `Core/Src/Tasks/ForceSensor/README.md` | On-board force: i2c (ADS1115) and internal ADC |
| OpticalSensor | `Core/Src/Tasks/OpticalSensor/README.md` | Angular velocity / acceleration from an optical encoder |
| Display | `Core/Src/Tasks/Display/README.md` | The display seam, plus both panels: `Lumex/` 16x2 character, `ILI9341/` 320x240 TFT |
| USB | `Core/Src/Tasks/USB/README.md` | Streams data + errors to the PC over USB CDC |
| TaskMonitor | `Core/Src/Tasks/TaskMonitor/README.md` | Per-task state and stack usage |
| MessagePassing | `Core/Src/MessagePassing/README.md` | Queue helpers, circular buffers, USB wire protocol |
| TimeKeeping | `Core/Src/TimeKeeping/README.md` | Microsecond timestamps |
| Config | `Core/Inc/Config/README.md` | Constants (`config.h`) + task/peripheral enables (`debug.h`) |
| CircularBuffer | `Middlewares/CircularBuffer/README.md` | Heap-free single-writer / multi-reader buffers |
| ADS1115 driver | `Drivers/ADS1115/README.md` | I2C 16-bit ADC driver used by the force sensor |
| ILI9341 driver | `Drivers/ILI9341/README.md` | SPI TFT driver used by the ILI9341 display task |

## main.c conventions
- Timer handles are renamed for clarity: `timestampTimer`, `lumexLcdTimer`, `bpmTimer`.
- Peripheral and queue handles are passed into task entry points; handles also needed
  by ISRs are declared `extern` in the consuming file.
- CubeMX owns everything outside the `USER CODE BEGIN/END` markers — regenerating from
  `stm32_dyno_firmware_v2.ioc` rewrites those regions.

## Related
[[MessagePassing]] · [[SessionController]] · [[Config]]
