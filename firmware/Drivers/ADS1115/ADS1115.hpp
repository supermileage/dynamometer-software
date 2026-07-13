// I2Cdev library collection - ADS1115 I2C device class header file
// Based on Texas Instruments ADS1113/4/5 datasheet, May 2009 (SBAS444B, revised October 2009)
// Note that the ADS1115 uses 16-bit registers, not 8-bit registers.
// 8/2/2011 by Jeff Rowberg <jeff@rowberg.net>
// Updates should (hopefully) always be available at https://github.com/jrowberg/i2cdevlib
//
// Changelog:
//     2013-05-05 - Add debug information.  Clean up Single Shot implementation
//     2011-10-29 - added getDifferentialx() methods, F. Farzanegan
//     2011-08-02 - initial release


/* ============================================
I2Cdev device library code is placed under the MIT license
Copyright (c) 2011 Jeff Rowberg

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
===============================================
*/

#ifndef ADS1115_ADS1115_HPP_
#define ADS1115_ADS1115_HPP_

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>
#include <stdbool.h>

#include "ADS1115_main.h"

#include "main.h"


class ADS1115
{
public:

    ADS1115(I2C_HandleTypeDef* i2cHandle, uint8_t address = ADS1115_DEFAULT_ADDRESS);
    ~ADS1115() = default;

	bool initialize();
    bool testConnection();

    // SINGLE SHOT utilities
    bool pollConversion(uint16_t max_retries);
    bool triggerConversion();

    // Read the current CONVERSION register
    bool getConversion(int16_t& value, bool triggerAndPoll = true);

    // Differential
    bool getConversionP0N1(int16_t& value);
    bool getConversionP0N3(int16_t& value);
    bool getConversionP1N3(int16_t& value);
    bool getConversionP2N3(int16_t& value);

    // Single-ended
    bool getConversionP0GND(int16_t& value);
    bool getConversionP1GND(int16_t& value);
    bool getConversionP2GND(int16_t& value);
    bool getConversionP3GND(int16_t& value);

    // Utility
    float getMilliVolts(bool triggerAndPoll = true);
    float getMvPerCount();

    // CONFIG register
    bool isConversionReady();
    bool getMultiplexer(uint8_t& muxMode);
    bool setMultiplexer(uint8_t mux);
    bool getGain(uint8_t& gain);
    bool setGain(uint8_t gain);
    bool getMode();
    bool setMode(bool mode);
    bool getRate(uint8_t& rate);
    bool setRate(uint8_t rate);
    bool getComparatorMode();
    bool setComparatorMode(bool mode);
    bool getComparatorPolarity();
    bool setComparatorPolarity(bool polarity);
    bool getComparatorLatchEnabled();
    bool setComparatorLatchEnabled(bool enabled);
    bool getComparatorQueueMode(uint8_t& mode);
    bool setComparatorQueueMode(uint8_t mode);
    bool setConversionReadyPinMode();

    // *_THRESH registers
    bool getLowThreshold(int16_t& threshold);
    bool setLowThreshold(int16_t threshold);
    bool getHighThreshold(int16_t& threshold);
    bool setHighThreshold(int16_t threshold);

    // DEBUG
    bool showConfigRegister();

private:
    I2C_HandleTypeDef* _i2cHandle;
    uint8_t _devAddr;
    uint8_t  _buffer[2];
    bool    _devMode;
    uint8_t _muxMode;
    uint8_t _pgaMode;


};

#ifdef __cplusplus
}
#endif
        


#endif /* ADS1115_ADS1115_HPP_ */
