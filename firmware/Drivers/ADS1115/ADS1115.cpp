// I2Cdev library collection - ADS1115 I2C device class
// Based on Texas Instruments ADS1113/4/5 datasheet, May 2009 (SBAS444B, revised October 2009)
// Note that the ADS1115 uses 16-bit registers, not 8-bit registers.
// 8/2/2011 by Jeff Rowberg <jeff@rowberg.net>
// Updates should (hopefully) always be available at https://github.com/jrowberg/i2cdevlib
//
// Changelog:
//     2013-05-05 - Add debug information.  Rename methods to match datasheet.
//     2011-11-06 - added getVoltage, F. Farzanegan
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

#include "ADS1115.hpp"

ADS1115::ADS1115(I2C_HandleTypeDef* i2cHandle, uint8_t address) : _i2cHandle(i2cHandle), _devAddr(address) {}

/** Power on and prepare for general usage.
 * This device is ready to use automatically upon power-up. It defaults to
 * single-shot read mode, P0/N1 mux, 2.048v gain, 128 samples/sec, default
 * comparator with hysterysis, active-low polarity, non-latching comparator,
 * and comparater-disabled operation.
 */
bool ADS1115::initialize() {
    bool status = true;

  status &= setMultiplexer(ADS1115_MUX_P0_N1);
  status &= setGain(ADS1115_PGA_2P048);
  status &= setMode(ADS1115_MODE_SINGLESHOT);
  status &= setRate(ADS1115_RATE_128);
  status &= setComparatorMode(ADS1115_COMP_MODE_HYSTERESIS);
  status &= setComparatorPolarity(ADS1115_COMP_POL_ACTIVE_LOW);
  status &= setComparatorLatchEnabled(ADS1115_COMP_LAT_NON_LATCHING);
  status &= setComparatorQueueMode(ADS1115_COMP_QUE_DISABLE);
  return status;
}

/** Verify the I2C connection.
 * Make sure the device is connected and responds as expected.
 * @return True if connection is valid, false otherwise
 */
bool ADS1115::testConnection() {
    // Register we want to read (Conversion register)
    uint8_t reg = ADS1115_RA_CONVERSION;

    // Read 2 bytes from the device
    HAL_StatusTypeDef status = HAL_I2C_Mem_Read(
        _i2cHandle,           // I2C handle
        _devAddr << 1,        // 7-bit device address shifted for HAL
        reg,                  // register address
        I2C_MEMADD_SIZE_8BIT, // register address size
        _buffer,              // buffer to store the data
        2,                    // number of bytes to read
        ADS1115_DEFAULT_TIMEOUT_MS         // timeout
    );

    // HAL_OK means the device responded
    return (status == HAL_OK);
}

/** Poll the operational status bit until the conversion is finished
 * Retry at most 'max_retries' times
 * conversion is finished, then return true;
 * @see ADS1115_CFG_OS_BIT
 * @return True if data is available, false otherwise
 */
bool ADS1115::pollConversion(uint16_t max_retries) {
  for(uint16_t i = 0; i < max_retries; i++) {
    if (isConversionReady()) return true;
  }
  return false;
}

/** Read differential value based on current MUX configuration.
 * The default MUX setting sets the device to get the differential between the
 * AIN0 and AIN1 pins. There are 8 possible MUX settings, but if you are using
 * all four input pins as single-end voltage sensors, then the default option is
 * not what you want; instead you will need to set the MUX to compare the
 * desired AIN* pin with GND. There are shortcut methods (getConversion*) to do
 * this conveniently, but you can also do it manually with setMultiplexer()
 * followed by this method.
 *
 * In single-shot mode, this register may not have fresh data. You need to write
 * a 1 bit to the MSB of the CONFIG register to trigger a single read/conversion
 * before this will be populated with fresh data. This technique is not as
 * effortless, but it has enormous potential to save power by only running the
 * comparison circuitry when needed.
 *
 * @param triggerAndPoll If true (and only in singleshot mode) the conversion trigger
 *        will be executed and the conversion results will be polled.
 * @return 16-bit signed differential value
 * @see getConversionP0N1();
 * @see getConversionPON3();
 * @see getConversionP1N3();
 * @see getConversionP2N3();
 * @see getConversionP0GND();
 * @see getConversionP1GND();
 * @see getConversionP2GND();
 * @see getConversionP3GND);
 * @see setMultiplexer();
 * @see ADS1115_RA_CONVERSION
 * @see ADS1115_MUX_P0_N1
 * @see ADS1115_MUX_P0_N3
 * @see ADS1115_MUX_P1_N3
 * @see ADS1115_MUX_P2_N3
 * @see ADS1115_MUX_P0_NG
 * @see ADS1115_MUX_P1_NG
 * @see ADS1115_MUX_P2_NG
 * @see ADS1115_MUX_P3_NG
 */
bool ADS1115::getConversion(int16_t& value, bool triggerAndPoll) {
    if (triggerAndPoll && _devMode == ADS1115_MODE_SINGLESHOT) {
        triggerConversion();
        pollConversion(ADS1115_POLL_MAX_RETRIES);
    }

    // Read 2 bytes from the conversion register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONVERSION,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // or some error value
    }

    // Combine MSB and LSB into a signed 16-bit value
    value = (_buffer[0] << 8) | _buffer[1];

    return true;
}

/** Get AIN0/N1 differential.
 * This changes the MUX setting to AIN0/N1 if necessary, triggers a new
 * measurement (also only if necessary), then gets the differential value
 * currently in the CONVERSION register.
 * @return 16-bit signed differential value
 * @see getConversion()
 */
bool ADS1115::getConversionP0N1(int16_t& value) {
    if (_muxMode != ADS1115_MUX_P0_N1) setMultiplexer(ADS1115_MUX_P0_N1);
    return getConversion(value, true);
}

/** Get AIN0/N3 differential.
 * This changes the MUX setting to AIN0/N3 if necessary, triggers a new
 * measurement (also only if necessary), then gets the differential value
 * currently in the CONVERSION register.
 * @return 16-bit signed differential value
 * @see getConversion()
 */
bool ADS1115::getConversionP0N3(int16_t& value) {
    if (_muxMode != ADS1115_MUX_P0_N3) setMultiplexer(ADS1115_MUX_P0_N3);
    return getConversion(value, true);
}

/** Get AIN1/N3 differential.
 * This changes the MUX setting to AIN1/N3 if necessary, triggers a new
 * measurement (also only if necessary), then gets the differential value
 * currently in the CONVERSION register.
 * @return 16-bit signed differential value
 * @see getConversion()
 */
bool ADS1115::getConversionP1N3(int16_t& value) {
    if (_muxMode != ADS1115_MUX_P1_N3) setMultiplexer(ADS1115_MUX_P1_N3);
    return getConversion(value, true);
}

/** Get AIN2/N3 differential.
 * This changes the MUX setting to AIN2/N3 if necessary, triggers a new
 * measurement (also only if necessary), then gets the differential value
 * currently in the CONVERSION register.
 * @return 16-bit signed differential value
 * @see getConversion()
 */
bool ADS1115::getConversionP2N3(int16_t& value) {
    if (_muxMode != ADS1115_MUX_P2_N3) setMultiplexer(ADS1115_MUX_P2_N3);
    return getConversion(value, true);
}

/** Get AIN0/GND differential.
 * This changes the MUX setting to AIN0/GND if necessary, triggers a new
 * measurement (also only if necessary), then gets the differential value
 * currently in the CONVERSION register.
 * @return 16-bit signed differential value
 * @see getConversion()
 */
bool ADS1115::getConversionP0GND(int16_t& value) {
    if (_muxMode != ADS1115_MUX_P0_NG) setMultiplexer(ADS1115_MUX_P0_NG);
    return getConversion(value, true);
}
/** Get AIN1/GND differential.
 * This changes the MUX setting to AIN1/GND if necessary, triggers a new
 * measurement (also only if necessary), then gets the differential value
 * currently in the CONVERSION register.
 * @return 16-bit signed differential value
 * @see getConversion()
 */
bool ADS1115::getConversionP1GND(int16_t& value) {
    if (_muxMode != ADS1115_MUX_P1_NG) setMultiplexer(ADS1115_MUX_P1_NG);
    return getConversion(value, true);
}
/** Get AIN2/GND differential.
 * This changes the MUX setting to AIN2/GND if necessary, triggers a new
 * measurement (also only if necessary), then gets the differential value
 * currently in the CONVERSION register.
 * @return 16-bit signed differential value
 * @see getConversion()
 */
bool ADS1115::getConversionP2GND(int16_t& value) {
    if (_muxMode != ADS1115_MUX_P2_NG) setMultiplexer(ADS1115_MUX_P2_NG);
    return getConversion(value, true);
}
/** Get AIN3/GND differential.
 * This changes the MUX setting to AIN3/GND if necessary, triggers a new
 * measurement (also only if necessary), then gets the differential value
 * currently in the CONVERSION register.
 * @return 16-bit signed differential value
 * @see getConversion()
 */
bool ADS1115::getConversionP3GND(int16_t& value) {
    if (_muxMode != ADS1115_MUX_P3_NG) setMultiplexer(ADS1115_MUX_P3_NG);
    return getConversion(value, true);
}

/** Get the current voltage reading
 * Read the current differential and return it multiplied
 * by the constant for the current gain.  mV is returned to
 * increase the precision of the voltage
 * @param triggerAndPoll If true (and only in singleshot mode) the conversion trigger
 *        will be executed and the conversion results will be polled.
 */
float ADS1115::getMilliVolts(bool triggerAndPoll) {
    int16_t value;
  switch (_pgaMode) {
    case ADS1115_PGA_6P144:
        getConversion(value, triggerAndPoll);
      return (value * ADS1115_MV_6P144);
      break;
    case ADS1115_PGA_4P096:
      getConversion(value, triggerAndPoll);
      return (value * ADS1115_MV_4P096);
      break;
    case ADS1115_PGA_2P048:
      getConversion(value, triggerAndPoll);
      return (value * ADS1115_MV_2P048);
      break;
    case ADS1115_PGA_1P024:
      getConversion(value, triggerAndPoll);
      return (value * ADS1115_MV_1P024);
      break;
    case ADS1115_PGA_0P512:
      getConversion(value, triggerAndPoll);
      return (value * ADS1115_MV_0P512);
      break;
    case ADS1115_PGA_0P256:
    case ADS1115_PGA_0P256B:
    case ADS1115_PGA_0P256C:
      getConversion(value, triggerAndPoll);
      return (value * ADS1115_MV_0P256);
      break;
    default:
    	return 0.0;
  }


}

/**
 * Return the current multiplier for the PGA setting.
 *
 * This may be directly retreived by using getMilliVolts(),
 * but this causes an independent read.  This function could
 * be used to average a number of reads from the getConversion()
 * getConversionx() functions and cut downon the number of
 * floating-point calculations needed.
 *
 */

float ADS1115::getMvPerCount() {
  switch (_pgaMode) {
    case ADS1115_PGA_6P144:
      return ADS1115_MV_6P144;
      break;
    case ADS1115_PGA_4P096:
      return  ADS1115_MV_4P096;
      break;
    case ADS1115_PGA_2P048:
      return ADS1115_MV_2P048;
      break;
    case ADS1115_PGA_1P024:
      return ADS1115_MV_1P024;
      break;
    case ADS1115_PGA_0P512:
      return ADS1115_MV_0P512;
      break;
    case ADS1115_PGA_0P256:
    case ADS1115_PGA_0P256B:
    case ADS1115_PGA_0P256C:
      return ADS1115_MV_0P256;
      break;
    default:
    	return 0.0;
  }
}

// CONFIG register

/** Get operational status.
 * @return Current operational status (false for active conversion, true for inactive)
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_OS_BIT
 */
bool ADS1115::isConversionReady() {
    // Read 2 bytes from CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error, treat as not ready
    }

    // Combine MSB and LSB into 16-bit value
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // OS bit (bit 15) indicates conversion status
    return (config & 0x8000) != 0;
}


/** Trigger a new conversion.
 * Writing to this bit will only have effect while in power-down mode (no conversions active).
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_OS_BIT
 */
bool ADS1115::triggerConversion() {
    // Read current config
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine bytes into 16-bit value
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Set OS bit (bit 15) to 1 to trigger conversion
    config |= 0x8000;

    // Split back into bytes
    _buffer[0] = (config >> 8) & 0xFF;
    _buffer[1] = config & 0xFF;

    // Write updated config back to CONFIG register
    if (HAL_I2C_Mem_Write(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                      I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }
    return true;
}

/** Get multiplexer connection.
 * @return Current multiplexer connection setting
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_MUX_BIT
 * @see ADS1115_CFG_MUX_LENGTH
 */
bool ADS1115::getMultiplexer(uint8_t& muxMode) {
    // Read 2 bytes from CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into 16-bit value
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Extract MUX bits (bits 14:12)
    _muxMode = (config >> 12) & 0x07;  // 0b111 = 3 bits

    muxMode = _muxMode;

    return true;
}


/** Set multiplexer connection.  Continous mode may fill the conversion register
 * with data before the MUX setting has taken effect.  A stop/start of the conversion
 * is done to reset the values.
 * @param mux New multiplexer connection setting
 * @see ADS1115_MUX_P0_N1
 * @see ADS1115_MUX_P0_N3
 * @see ADS1115_MUX_P1_N3
 * @see ADS1115_MUX_P2_N3
 * @see ADS1115_MUX_P0_NG
 * @see ADS1115_MUX_P1_NG
 * @see ADS1115_MUX_P2_NG
 * @see ADS1115_MUX_P3_NG
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_MUX_BIT
 * @see ADS1115_CFG_MUX_LENGTH
 */
bool ADS1115::setMultiplexer(uint8_t mux) {
    // Read current CONFIG register
	HAL_StatusTypeDef status = HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
            I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS);

	if (status != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Clear MUX bits (bits 14:12)
    config &= ~(0x7 << 12);

    // Set new MUX bits
    config |= ((mux & 0x7) << 12);

    // Split back into bytes
    _buffer[0] = (config >> 8) & 0xFF;
    _buffer[1] = config & 0xFF;

    // Write back updated CONFIG register
    if (HAL_I2C_Mem_Write(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                          I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Store new MUX mode
    _muxMode = mux;

    // If in continuous mode, force a stop/start to reset conversion
    if (_devMode == ADS1115_MODE_CONTINUOUS) {
        setMode(ADS1115_MODE_SINGLESHOT);
        int16_t value;
        getConversion(value, true);  // perform one conversion to reset
        setMode(ADS1115_MODE_CONTINUOUS);
    }

    return true;
}

/** Get programmable gain amplifier level.
 * @return Current programmable gain amplifier level
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_PGA_BIT
 * @see ADS1115_CFG_PGA_LENGTH
 */
bool ADS1115::getGain(uint8_t& pgaMode) {
    // Read 2 bytes from CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into 16-bit value
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Extract PGA bits (bits 11:9)
    _pgaMode = (config >> 9) & 0x07; // 3 bits

    pgaMode = _pgaMode;

    return true;
}


/** Set programmable gain amplifier level.
 * Continuous mode may fill the conversion register
 * with data before the gain setting has taken effect. A stop/start of the conversion
 * is done to reset the values.
 * @param gain New programmable gain amplifier level
 */
bool ADS1115::setGain(uint8_t gain) {
    // Read current CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Clear PGA bits (bits 11:9)
    config &= ~(0x7 << 9);

    // Set new PGA bits
    config |= ((gain & 0x7) << 9);

    // Split back into bytes
    _buffer[0] = (config >> 8) & 0xFF;
    _buffer[1] = config & 0xFF;

    // Write updated CONFIG register
    if (HAL_I2C_Mem_Write(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                          I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Store new PGA mode
    _pgaMode = gain;

    // If in continuous mode, force a stop/start to reset conversion
    if (_devMode == ADS1115_MODE_CONTINUOUS) {
        setMode(ADS1115_MODE_SINGLESHOT);
        int16_t value;
        getConversion(value, true);  // perform one conversion to reset
        setMode(ADS1115_MODE_CONTINUOUS);
    }

    return true;
}

/** Get device mode.
 * @return Current device mode (false = continuous, true = single-shot)
 * @see ADS1115_MODE_CONTINUOUS
 * @see ADS1115_MODE_SINGLESHOT
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_MODE_BIT
 */
bool ADS1115::getMode() {
    // Read 2 bytes from CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error, assume continuous
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Extract MODE bit (bit 8)
    _devMode = (config >> 8) & 0x01;

    return _devMode;
}

/** Set device mode.
 * @param mode New device mode (false = continuous, true = single-shot)
 * @see ADS1115_MODE_CONTINUOUS
 * @see ADS1115_MODE_SINGLESHOT
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_MODE_BIT
 */
bool ADS1115::setMode(bool mode) {
    // Read current CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Clear MODE bit (bit 8)
    config &= ~(1 << 8);

    // Set new MODE bit
    config |= ((mode & 0x01) << 8);

    // Split back into bytes
    _buffer[0] = (config >> 8) & 0xFF;
    _buffer[1] = config & 0xFF;

    // Write updated CONFIG register
    if (HAL_I2C_Mem_Write(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                          I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Store mode locally
    _devMode = mode;

    return true;
}

/** Get data rate.
 * @return Current data rate (3-bit value)
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_DR_BIT
 * @see ADS1115_CFG_DR_LENGTH
 */
bool ADS1115::getRate(uint8_t& rate) {
    // Read 2 bytes from CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Extract DR bits (bits 7:5)
    rate = (config >> 5) & 0x07;  // 3 bits

    return true;
}


/** Set data rate.
 * @param rate New data rate (3-bit value)
 * @see ADS1115_RATE_8
 * @see ADS1115_RATE_16
 * @see ADS1115_RATE_32
 * @see ADS1115_RATE_64
 * @see ADS1115_RATE_128
 * @see ADS1115_RATE_250
 * @see ADS1115_RATE_475
 * @see ADS1115_RATE_860
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_DR_BIT
 * @see ADS1115_CFG_DR_LENGTH
 */
bool ADS1115::setRate(uint8_t rate) {
    // Read current CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Clear DR bits (bits 7:5)
    config &= ~(0x7 << 5);

    // Set new DR bits
    config |= ((rate & 0x07) << 5);

    // Split back into bytes
    _buffer[0] = (config >> 8) & 0xFF;
    _buffer[1] = config & 0xFF;

    // Write updated CONFIG register
    if (HAL_I2C_Mem_Write(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                      I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    return true;
}

/** Get comparator mode.
 * @return Current comparator mode (false = hysteresis, true = window)
 * @see ADS1115_COMP_MODE_HYSTERESIS
 * @see ADS1115_COMP_MODE_WINDOW
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_COMP_MODE_BIT
 */
bool ADS1115::getComparatorMode() {
    // Read 2 bytes from CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error, default to hysteresis
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Extract COMP_MODE bit (bit 4)
    return (config >> 4) & 0x01;
}


/** Set comparator mode.
 * @param mode New comparator mode (false = hysteresis, true = window)
 * @see ADS1115_COMP_MODE_HYSTERESIS
 * @see ADS1115_COMP_MODE_WINDOW
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_COMP_MODE_BIT
 */
bool ADS1115::setComparatorMode(bool mode) {
    // Read current CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Clear COMP_MODE bit (bit 4)
    config &= ~(1 << 4);

    // Set new COMP_MODE bit
    config |= ((mode & 0x01) << 4);

    // Split back into bytes
    _buffer[0] = (config >> 8) & 0xFF;
    _buffer[1] = config & 0xFF;

    // Write updated CONFIG register
    if (HAL_I2C_Mem_Write(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                      I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }
    return true;
}


/** Get comparator polarity setting.
 * @return Current comparator polarity setting (false = active low, true = active high)
 * @see ADS1115_COMP_POL_ACTIVE_LOW
 * @see ADS1115_COMP_POL_ACTIVE_HIGH
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_COMP_POL_BIT
 */
bool ADS1115::getComparatorPolarity() {
    // Read 2 bytes from CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error, default to active low
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Extract COMP_POL bit (bit 3)
    return (config >> 3) & 0x01;
}


/** Set comparator polarity setting.
 * @param polarity New comparator polarity setting (false = active low, true = active high)
 * @see ADS1115_COMP_POL_ACTIVE_LOW
 * @see ADS1115_COMP_POL_ACTIVE_HIGH
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_COMP_POL_BIT
 */
bool ADS1115::setComparatorPolarity(bool polarity) {
    // Read current CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Clear COMP_POL bit (bit 3)
    config &= ~(1 << 3);

    // Set new COMP_POL bit
    config |= ((polarity & 0x01) << 3);

    // Split back into bytes
    _buffer[0] = (config >> 8) & 0xFF;
    _buffer[1] = config & 0xFF;

    // Write updated CONFIG register
    if (HAL_I2C_Mem_Write(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                      I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }
    return true;
}


/** Get comparator latch enabled value.
 * @return Current comparator latch enabled value (false = non-latching, true = latching)
 * @see ADS1115_COMP_LAT_NON_LATCHING
 * @see ADS1115_COMP_LAT_LATCHING
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_COMP_LAT_BIT
 */
bool ADS1115::getComparatorLatchEnabled() {
    // Read 2 bytes from CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error, default to non-latching
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Extract COMP_LAT bit (bit 2)
    return (config >> 2) & 0x01;
}


/** Set comparator latch enabled value.
 * @param enabled New comparator latch enabled value (false = non-latching, true = latching)
 * @see ADS1115_COMP_LAT_NON_LATCHING
 * @see ADS1115_COMP_LAT_LATCHING
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_COMP_LAT_BIT
 */
bool ADS1115::setComparatorLatchEnabled(bool enabled) {
    // Read current CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Clear COMP_LAT bit (bit 2)
    config &= ~(1 << 2);

    // Set new COMP_LAT bit
    config |= ((enabled & 0x01) << 2);

    // Split back into bytes
    _buffer[0] = (config >> 8) & 0xFF;
    _buffer[1] = config & 0xFF;

    // Write updated CONFIG register
    if (HAL_I2C_Mem_Write(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                      I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    return true;
}

/** Get comparator queue mode.
 * @return Current comparator queue mode (0-3)
 * @see ADS1115_COMP_QUE_ASSERT1
 * @see ADS1115_COMP_QUE_ASSERT2
 * @see ADS1115_COMP_QUE_ASSERT4
 * @see ADS1115_COMP_QUE_DISABLE
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_COMP_QUE_BIT
 * @see ADS1115_CFG_COMP_QUE_LENGTH
 */
bool ADS1115::getComparatorQueueMode(uint8_t& mode) {
    // Read 2 bytes from CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error, default to assert 1
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Extract COMP_QUE bits (bits 1:0)
    mode = config & 0x03;  // mask 2 LSBs
    return true;
}


/** Set comparator queue mode.
 * @param mode New comparator queue mode (0–3)
 * @see ADS1115_COMP_QUE_ASSERT1
 * @see ADS1115_COMP_QUE_ASSERT2
 * @see ADS1115_COMP_QUE_ASSERT4
 * @see ADS1115_COMP_QUE_DISABLE
 * @see ADS1115_RA_CONFIG
 * @see ADS1115_CFG_COMP_QUE_BIT
 * @see ADS1115_CFG_COMP_QUE_LENGTH
 */
bool ADS1115::setComparatorQueueMode(uint8_t mode) {
    // Read current CONFIG register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into 16-bit
    uint16_t config = (_buffer[0] << 8) | _buffer[1];

    // Clear COMP_QUE bits (bits 1:0)
    config &= ~0x03;

    // Set new COMP_QUE bits
    config |= (mode & 0x03);

    // Split back into bytes
    _buffer[0] = (config >> 8) & 0xFF;
    _buffer[1] = config & 0xFF;

    // Write updated CONFIG register
    if (HAL_I2C_Mem_Write(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
                      I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    return true;
}


// *_THRESH registers

/** Get low threshold value.
 * @return Current low threshold value
 * @see ADS1115_RA_LO_THRESH
 */
bool ADS1115::getLowThreshold(int16_t& value) {
    // Read 2 bytes from LO_THRESH register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_LO_THRESH,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into a signed 16-bit value
    value = (_buffer[0] << 8) | _buffer[1];

    return true;
}

/** Set low threshold value.
 * @param threshold New low threshold value
 * @see ADS1115_RA_LO_THRESH
 */
bool ADS1115::setLowThreshold(int16_t threshold) {
    // Split 16-bit threshold into MSB and LSB
    _buffer[0] = (threshold >> 8) & 0xFF;
    _buffer[1] = threshold & 0xFF;

    // Write 2 bytes to LO_THRESH register
    if (HAL_I2C_Mem_Write(_i2cHandle, _devAddr << 1, ADS1115_RA_LO_THRESH,
                      I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }
    return true;
}

/** Get high threshold value.
 * @return Current high threshold value
 * @see ADS1115_RA_HI_THRESH
 */
bool ADS1115::getHighThreshold(int16_t& value) {
    // Read 2 bytes from HI_THRESH register
    if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_HI_THRESH,
                         I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }

    // Combine MSB and LSB into a signed 16-bit value
    value = (_buffer[0] << 8) | _buffer[1];

    return true;
}


/** Set high threshold value.
 * @param threshold New high threshold value
 * @see ADS1115_RA_HI_THRESH
 */
bool ADS1115::setHighThreshold(int16_t threshold) {
    // Split 16-bit threshold into MSB and LSB
    _buffer[0] = (threshold >> 8) & 0xFF;
    _buffer[1] = threshold & 0xFF;

    // Write 2 bytes to HI_THRESH register
    if (HAL_I2C_Mem_Write(_i2cHandle, _devAddr << 1, ADS1115_RA_HI_THRESH,
                      I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
        return false; // Communication error
    }
    return true;
}


/** Configures ALERT/RDY pin as a conversion ready pin.
 *  It does this by setting the MSB of the high threshold register to '1' and the MSB
 *  of the low threshold register to '0'. COMP_POL and COMP_QUE bits will be set to '0'.
 *  Note: ALERT/RDY pin requires a pull-up resistor.
 */
bool ADS1115::setConversionReadyPinMode() {
    int16_t hi;
    if (!getHighThreshold(hi)) {
        return false; // Communication error
    }
    int16_t lo;
    if (!getLowThreshold(lo)) {
        return false; // Communication error
    }

    // Set MSB of HI_THRESH to 1
    hi |= 0x8000;
    // Set MSB of LO_THRESH to 0
    lo &= ~0x8000;

    bool status = true;

    // Write updated thresholds
    status &= setHighThreshold(hi);
    status &= setLowThreshold(lo);

    // Set comparator polarity and queue bits using HAL-native methods
    status &= setComparatorPolarity(false);     // COMP_POL = 0
    status &= setComparatorQueueMode(0);        // COMP_QUE = 0

    return status;
}

// Create a mask between two bits
unsigned createMask(unsigned a, unsigned b) {
   unsigned mask = 0;
   for (unsigned i=a; i<=b; i++)
       mask |= 1 << i;
   return mask;
}

uint16_t shiftDown(uint16_t extractFrom, int places) {
  return (extractFrom >> places);
}


uint16_t getValueFromBits(uint16_t extractFrom, int high, int length) {
   int low= high-length +1;
   uint16_t mask = createMask(low ,high);
   return shiftDown(extractFrom & mask, low);
}

/** Show all the config register settings
 */
//void ADS1115_showConfigRegister() {
//  // Read 2 bytes from CONFIG register
//  if (HAL_I2C_Mem_Read(_i2cHandle, _devAddr << 1, ADS1115_RA_CONFIG,
//                        I2C_MEMADD_SIZE_8BIT, _buffer, 2, ADS1115_DEFAULT_TIMEOUT_MS) != HAL_OK) {
//      return; // Communication error
//  }
//
//  // Combine MSB and LSB into 16-bit value
//  uint16_t configRegister = (_buffer[0] << 8) | _buffer[1];
//
//}


