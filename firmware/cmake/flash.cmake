# cmake/flash.cmake
# Convenience targets that flash the freshly-built firmware to the STM32H743 by
# delegating to Scripts/flash.sh (Linux/macOS) or Scripts/flash.ps1 (Windows),
# so the tool matrix lives in one place. You choose the tool explicitly via
# -DFLASH_TOOL=...; there is no auto-detect and no fallback.
#
#   flash        SWD via ST-Link probe          FLASH_TOOL: cubeprog|st-flash|openocd
#   flash-dfu    USB DFU via ROM bootloader      FLASH_TOOL: cubeprog|dfu-util
#   flash-uart   UART via ROM bootloader         FLASH_TOOL: cubeprog|stm32flash
#
# Usage (the preset selects which build is flashed):
#   cmake --preset Debug -DFLASH_TOOL=st-flash
#   cmake --build --preset Debug --target flash
#
#   cmake --preset Debug -DFLASH_TOOL=cubeprog -DFLASH_SERIAL=0670FF...
#   cmake --build --preset Debug --target flash          # pick a specific probe
#
#   cmake --preset Debug -DFLASH_TOOL=stm32flash -DFLASH_UART_PORT=/dev/ttyUSB0
#   cmake --build --preset Debug --target flash-uart
#
# Flashing needs USB/serial access to the board, so always run these on the host,
# never inside the Docker build container.

set(FLASH_TOOL   "" CACHE STRING "Flashing tool: cubeprog|st-flash|openocd|dfu-util|stm32flash (required)")
set(FLASH_SERIAL "" CACHE STRING "ST-Link/DFU serial number to target a specific probe/device")
set(FLASH_INDEX  "1" CACHE STRING "USB DFU device index for cubeprog (port=USB<n>)")
if(WIN32)
    set(FLASH_UART_PORT "COM3" CACHE STRING "Serial port for UART bootloader flashing")
else()
    set(FLASH_UART_PORT "/dev/ttyUSB0" CACHE STRING "Serial port for UART bootloader flashing")
endif()
set(FLASH_UART_BAUD "115200" CACHE STRING "Baud rate for UART bootloader flashing")

# add_flash_target(<target> <swd|dfu|uart>) — forward the choices to the wrapper
# script with the correct per-platform argument syntax, rebuilding firmware first.
function(add_flash_target target method)
    if(WIN32)
        set(cmd powershell -NoProfile -ExecutionPolicy Bypass
            -File "${CMAKE_SOURCE_DIR}/Scripts/flash.ps1"
            -Config "${CMAKE_BUILD_TYPE}" -Method "${method}")
        if(FLASH_TOOL)
            list(APPEND cmd -Tool "${FLASH_TOOL}")
        endif()
        if(FLASH_SERIAL)
            list(APPEND cmd -Serial "${FLASH_SERIAL}")
        endif()
        if(method STREQUAL "dfu")
            list(APPEND cmd -Index "${FLASH_INDEX}")
        elseif(method STREQUAL "uart")
            list(APPEND cmd -Port "${FLASH_UART_PORT}" -Baud "${FLASH_UART_BAUD}")
        endif()
    else()
        set(cmd bash "${CMAKE_SOURCE_DIR}/Scripts/flash.sh" "${CMAKE_BUILD_TYPE}" "${method}")
        if(FLASH_TOOL)
            list(APPEND cmd --tool "${FLASH_TOOL}")
        endif()
        if(FLASH_SERIAL)
            list(APPEND cmd --serial "${FLASH_SERIAL}")
        endif()
        if(method STREQUAL "dfu")
            list(APPEND cmd --index "${FLASH_INDEX}")
        elseif(method STREQUAL "uart")
            list(APPEND cmd --port "${FLASH_UART_PORT}" --baud "${FLASH_UART_BAUD}")
        endif()
    endif()

    add_custom_target(${target}
        COMMAND ${cmd}
        USES_TERMINAL   # show probe progress live and allow prompts
        VERBATIM
        COMMENT "Flashing ${CMAKE_PROJECT_NAME} (${method}) via ${CMAKE_SOURCE_DIR}/Scripts"
    )
    add_dependencies(${target} ${CMAKE_PROJECT_NAME})  # rebuild before flashing
endfunction()

add_flash_target(flash      swd)
add_flash_target(flash-dfu  dfu)
add_flash_target(flash-uart uart)
