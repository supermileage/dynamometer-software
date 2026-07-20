# Host-compiled unit tests for the firmware's hardware-independent logic.
#
# This project is never built by the ARM toolchain: it compiles the HAL/RTOS-free firmware
# sources (the USB RX ring, the frame parser, the sysconfig store) with the machine's
# native compiler and runs them as an ordinary process — no board, no serial port. CI runs
# it on a stock ubuntu runner; locally it is:
#
#   cmake -S firmware/tests -B firmware/tests/build
#   cmake --build firmware/tests/build
#   ctest --test-dir firmware/tests/build --output-on-failure

cmake_minimum_required(VERSION 3.24)
project(dyno_firmware_tests C CXX)

set(CMAKE_C_STANDARD 23)          # messages_public.h uses enums with fixed underlying types
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 20)        # std::bit_cast in the sysconfig tests
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Pinned by hash, like the vendored flashing tools: a swapped file fails loudly.
include(FetchContent)
FetchContent_Declare(
  googletest
  URL https://github.com/google/googletest/releases/download/v1.15.2/googletest-1.15.2.tar.gz
  URL_HASH SHA256=7b42b4d6ed48810c5362c265a17faebe90dc2373c885e5216439d37927f02926
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
)
FetchContent_MakeAvailable(googletest)

set(FIRMWARE_DIR ${CMAKE_CURRENT_SOURCE_DIR}/..)

add_executable(fw_tests
  # The firmware sources under test — the same files the ARM build compiles.
  ${FIRMWARE_DIR}/Core/Src/Config/sysconfig.c
  ${FIRMWARE_DIR}/Core/Src/Tasks/USB/usb_rx_ring.c
  ${FIRMWARE_DIR}/Core/Src/Tasks/USB/usb_framer.cpp
  ${FIRMWARE_DIR}/Core/Src/Tasks/OpticalSensor/encoder_math.c
  usb_rx_ring_tests.cpp
  usb_framer_tests.cpp
  sysconfig_tests.cpp
  circular_buffer_tests.cpp
  encoder_math_tests.cpp
)

target_include_directories(fw_tests PRIVATE
  ${FIRMWARE_DIR}/Core/Inc
  ${FIRMWARE_DIR}/Drivers/ADS1115   # config.h includes ADS1115_main.h (register-code macros)
  ${FIRMWARE_DIR}/Middlewares/CircularBuffer/Inc
  stubs                             # no-op FreeRTOS critical-section macros for the buffer headers
)

target_compile_options(fw_tests PRIVATE -Wall -Wextra)
target_link_libraries(fw_tests PRIVATE gtest_main)

enable_testing()
include(GoogleTest)
gtest_discover_tests(fw_tests)
