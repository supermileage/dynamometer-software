cmake_minimum_required(VERSION 3.22)

#
# This file is generated only once,
# and is not re-generated if converter is called multiple times.
#
# User is free to modify the file as much as necessary
#

# Setup compiler settings
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS ON)


# Define the build type
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE "Debug")
endif()

# Set the project name
set(CMAKE_PROJECT_NAME stm32_dyno_firmware_v2)

# Enable compile command to ease indexing with e.g. clangd
set(CMAKE_EXPORT_COMPILE_COMMANDS TRUE)

# Core project settings
project(${CMAKE_PROJECT_NAME})
message("Build type: " ${CMAKE_BUILD_TYPE})

# Enable CMake support for ASM, C and C++ languages
enable_language(C CXX ASM)

# Create an executable object type
add_executable(${CMAKE_PROJECT_NAME})

# Add STM32CubeMX generated sources
add_subdirectory(cmake/stm32cubemx)

# Link directories setup
target_link_directories(${CMAKE_PROJECT_NAME} PRIVATE
    # Add user defined library search paths
)

# Application sources (not managed by STM32CubeMX). CubeMX only generates files
# directly under Core/Src, so globbing the application subdirectories is safe
# from duplicates and picks up new task files automatically.
file(GLOB_RECURSE APP_SOURCES CONFIGURE_DEPENDS
    ${CMAKE_SOURCE_DIR}/Core/Src/Tasks/*.c
    ${CMAKE_SOURCE_DIR}/Core/Src/Tasks/*.cpp
    ${CMAKE_SOURCE_DIR}/Core/Src/MessagePassing/*.c
    ${CMAKE_SOURCE_DIR}/Core/Src/MessagePassing/*.cpp
    ${CMAKE_SOURCE_DIR}/Core/Src/TimeKeeping/*.c
    ${CMAKE_SOURCE_DIR}/Core/Src/TimeKeeping/*.cpp
    ${CMAKE_SOURCE_DIR}/Middlewares/CircularBuffer/Src/*.cpp
)

target_sources(${CMAKE_PROJECT_NAME} PRIVATE
    ${CMAKE_SOURCE_DIR}/Core/Src/interrupts.c
    ${CMAKE_SOURCE_DIR}/Drivers/ADS1115/ADS1115.cpp
    ${APP_SOURCES}
)

# Add include paths
target_include_directories(${CMAKE_PROJECT_NAME} PRIVATE
    # Application headers and the on-board ADS1115 force-sensor driver headers.
    ${CMAKE_SOURCE_DIR}/Core/Inc
    ${CMAKE_SOURCE_DIR}/Middlewares/CircularBuffer/Inc
    ${CMAKE_SOURCE_DIR}/Drivers/ADS1115
)

# Add project symbols (macros)
target_compile_definitions(${CMAKE_PROJECT_NAME} PRIVATE
    # Add user defined symbols
)

# Remove wrong libob.a library dependency when using cpp files
list(REMOVE_ITEM CMAKE_C_IMPLICIT_LINK_LIBRARIES ob)

# Add linked libraries
target_link_libraries(${CMAKE_PROJECT_NAME}
    stm32cubemx

    # Add user defined libraries
)

# Print size and emit flashable .hex / .bin next to the .elf. Use absolute output
# paths (and declare them as byproducts) so artifact locations are deterministic
# regardless of the build tool's working directory. CMAKE_CURRENT_BINARY_DIR is
# where the executable is placed; target-referencing generator expressions such
# as $<TARGET_FILE_DIR:...> are not allowed in BYPRODUCTS.
set(FW_HEX ${CMAKE_CURRENT_BINARY_DIR}/${CMAKE_PROJECT_NAME}.hex)
set(FW_BIN ${CMAKE_CURRENT_BINARY_DIR}/${CMAKE_PROJECT_NAME}.bin)
add_custom_command(TARGET ${CMAKE_PROJECT_NAME} POST_BUILD
    BYPRODUCTS ${FW_HEX} ${FW_BIN}
    COMMAND ${CMAKE_SIZE} $<TARGET_FILE:${CMAKE_PROJECT_NAME}>
    COMMAND ${CMAKE_OBJCOPY} -O ihex   $<TARGET_FILE:${CMAKE_PROJECT_NAME}> ${FW_HEX}
    COMMAND ${CMAKE_OBJCOPY} -O binary $<TARGET_FILE:${CMAKE_PROJECT_NAME}> ${FW_BIN}
    COMMENT "Generating ${CMAKE_PROJECT_NAME}.hex and ${CMAKE_PROJECT_NAME}.bin"
    VERBATIM
)

# `flash` target: program the built firmware over SWD/ST-Link using whichever
# flashing tool is installed (auto-detected). Included after FW_BIN is defined.
include(${CMAKE_SOURCE_DIR}/cmake/flash.cmake)
