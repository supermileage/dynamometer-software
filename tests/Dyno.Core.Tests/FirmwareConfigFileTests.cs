using Dyno.Core.Firmware;
using Xunit;

namespace Dyno.Core.Tests;

public class FirmwareConfigFileTests
{
    // Mirrors the real config.h shapes: include guard, single- and multi-define sections,
    // comment blocks glued under a define (descriptions), trailing comments, an expression
    // value, suffixed literals, and an enum-token value.
    private const string ConfigHeader = """
        #ifndef INC_CONFIG_CONFIG_H_
        #define INC_CONFIG_CONFIG_H_

        #include "ADS1115_main.h"

        // Voltage Reference (should be 3V3)
        #define VREF 3.3f

        // Main PID controller parameters
        #define K_P 1.0f
        #define K_I 1.0f
        #define PID_INITIAL_STATUS false

        // FORCE SENSOR Config
        #define FORCESENSOR_TASK_OSDELAY 1
        // Bounded wait (ms) on the enable queue while disabled, so USB setting commands
        // are still serviced when the sensor is idle.
        #define FORCESENSOR_COMMAND_POLL_OSDELAY 50

        // ADS1115 I2C Config
        #define ADS1115_SAMPLE_SPEED ADS1115_RATE_475

        // Optical Encoder Config
        #define NUM_APERTURES 64 // Tied to physical 3D printed apparatus

        // LCD config
        #define SESSION_CONTROLLER_TO_LUMEX_LCD_MSG_STRING_SIZE 16 + 1

        // User Input Config (like buttons)
        #define USER_INPUT_CIRCULAR_BUFFER_SIZE 100u

        #endif /* INC_CONFIG_CONFIG_H_ */
        """;

    // Mirrors debug.h: a leading comment plus banner heading the first group, banner-only
    // sections, aligned 0/1 values, and blank-line-separated per-define comments.
    private const string DebugHeader = """
        #ifndef INC_CONFIG_DEBUG_H_
        #define INC_CONFIG_DEBUG_H_

        // Peripheral enable/disables
        // ===== GPIO =====
        #define STM32_PERIPHERAL_GPIO_ENABLE      1

        // ===== TIMERS =====
        #define STM32_PERIPHERAL_TIM1_ENABLE      1
        #define STM32_PERIPHERAL_TIM2_ENABLE      0

        // Task enable/disables
        #define FORCE_SENSOR_ADS1115_TASK_ENABLE 1

        // USB Controller task settings
        #define USB_CONTROLLER_TASK_ENABLE 1
        #define DEBUG_USB_CONTROLLER_MOCK_MESSAGES 0

        #endif /* INC_CONFIG_DEBUG_H_ */
        """;

    private static FirmwareConfigFile ParseConfig() =>
        FirmwareConfigFile.Parse("config.h", ConfigHeader);

    private static FirmwareConfigFile ParseDebug() =>
        FirmwareConfigFile.Parse("debug.h", DebugHeader, binaryTogglesAreBool: true);

    private static ConfigDefine Get(FirmwareConfigFile file, string name) =>
        Assert.Single(file.Defines, d => d.Name == name);

    [Fact]
    public void IncludeGuardAndIncludeAreNotSettings()
    {
        var file = ParseConfig();
        Assert.DoesNotContain(file.Defines, d => d.Name == "INC_CONFIG_CONFIG_H_");
        Assert.Equal(10, file.Defines.Count);
    }

    [Fact]
    public void SectionCommentsBecomeCategories()
    {
        var file = ParseConfig();
        Assert.Equal("Voltage Reference (should be 3V3)", Get(file, "VREF").Category);
        Assert.Equal("Main PID controller parameters", Get(file, "K_P").Category);
        Assert.Equal("Main PID controller parameters", Get(file, "PID_INITIAL_STATUS").Category);
        Assert.Equal("FORCE SENSOR Config", Get(file, "FORCESENSOR_TASK_OSDELAY").Category);
    }

    [Fact]
    public void CommentGluedUnderADefineDescribesTheNextDefine_NotANewCategory()
    {
        var file = ParseConfig();
        var define = Get(file, "FORCESENSOR_COMMAND_POLL_OSDELAY");
        Assert.Equal("FORCE SENSOR Config", define.Category);
        Assert.StartsWith("Bounded wait (ms) on the enable queue", define.Description);
        Assert.Contains("serviced when the sensor is idle", define.Description);
    }

    [Fact]
    public void TrailingCommentBecomesDescription()
    {
        var define = Get(ParseConfig(), "NUM_APERTURES");
        Assert.Equal("Tied to physical 3D printed apparatus", define.Description);
        Assert.Equal("64", define.Value);
    }

    [Fact]
    public void ValueKindsAreClassified()
    {
        var file = ParseConfig();
        Assert.Equal(ConfigValueKind.Number, Get(file, "VREF").Kind);
        Assert.Equal("3.3f", Get(file, "VREF").Value);
        Assert.Equal(ConfigValueKind.Number, Get(file, "USER_INPUT_CIRCULAR_BUFFER_SIZE").Kind);
        Assert.Equal(ConfigValueKind.Bool, Get(file, "PID_INITIAL_STATUS").Kind);
        Assert.Equal(ConfigValueKind.Text, Get(file, "ADS1115_SAMPLE_SPEED").Kind);
        // In config.h a bare number stays a number even when it happens to be 0 or 1.
        Assert.Equal(ConfigValueKind.Number, Get(file, "FORCESENSOR_TASK_OSDELAY").Kind);
    }

    [Fact]
    public void ExpressionValueIsCapturedWhole()
    {
        var define = Get(ParseConfig(), "SESSION_CONTROLLER_TO_LUMEX_LCD_MSG_STRING_SIZE");
        Assert.Equal("16 + 1", define.Value);
        Assert.Equal(ConfigValueKind.Text, define.Kind);
    }

    [Fact]
    public void BannerCommentsNameDebugSections()
    {
        var file = ParseDebug();
        Assert.Equal("GPIO", Get(file, "STM32_PERIPHERAL_GPIO_ENABLE").Category);
        Assert.Equal("TIMERS", Get(file, "STM32_PERIPHERAL_TIM1_ENABLE").Category);
        Assert.Equal(
            "Task enable/disables",
            Get(file, "FORCE_SENSOR_ADS1115_TASK_ENABLE").Category
        );
        Assert.Equal(
            "USB Controller task settings",
            Get(file, "DEBUG_USB_CONTROLLER_MOCK_MESSAGES").Category
        );
    }

    [Fact]
    public void DebugTogglesAreBool()
    {
        var file = ParseDebug();
        Assert.All(file.Defines, d => Assert.Equal(ConfigValueKind.Bool, d.Kind));
    }

    [Fact]
    public void SetValueRewritesOnlyTheValue()
    {
        var file = ParseConfig();
        Assert.True(file.TrySetValue("VREF", "5.0f"));
        Assert.True(file.TrySetValue("NUM_APERTURES", "128"));
        Assert.True(file.TrySetValue("SESSION_CONTROLLER_TO_LUMEX_LCD_MSG_STRING_SIZE", "32 + 1"));

        var text = file.ToText();
        Assert.Contains("#define VREF 5.0f", text);
        // The trailing comment survives in place.
        Assert.Contains("#define NUM_APERTURES 128 // Tied to physical 3D printed apparatus", text);
        Assert.Contains("#define SESSION_CONTROLLER_TO_LUMEX_LCD_MSG_STRING_SIZE 32 + 1", text);
        Assert.Equal("5.0f", Get(file, "VREF").Value);
    }

    [Fact]
    public void SetValuePreservesAlignmentPadding()
    {
        var file = ParseDebug();
        Assert.True(file.TrySetValue("STM32_PERIPHERAL_GPIO_ENABLE", "0"));
        Assert.Contains("#define STM32_PERIPHERAL_GPIO_ENABLE      0", file.ToText());
    }

    [Fact]
    public void UntouchedTextRoundTripsExactly()
    {
        Assert.Equal(ConfigHeader, ParseConfig().ToText());
        Assert.Equal(DebugHeader, ParseDebug().ToText());
    }

    [Fact]
    public void SetValueRejectsBadInput()
    {
        var file = ParseConfig();
        Assert.False(file.TrySetValue("NOT_A_DEFINE", "1"));
        Assert.False(file.TrySetValue("VREF", ""));
        Assert.False(file.TrySetValue("VREF", "   "));
        Assert.False(file.TrySetValue("VREF", "1.0f // sneaky comment"));
        Assert.False(file.TrySetValue("VREF", "1.0f\n#define EVIL 1"));
        // Failed sets leave the text untouched.
        Assert.Equal(ConfigHeader, file.ToText());
    }
}
