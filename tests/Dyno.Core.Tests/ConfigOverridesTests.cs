using Dyno.Core.Firmware;
using Xunit;

namespace Dyno.Core.Tests;

public class ConfigOverridesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dyno-overrides-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ConfigOverridesPath => Path.Combine(_dir, "config_overrides.h");

    private string DebugOverridesPath => Path.Combine(_dir, "debug_overrides.h");

    private static ConfigOverride Vref(string value) => new("VREF", "config.h", "3.3f", value);

    [Fact]
    public void AnOverrideIsUndefinedThenRedefined()
    {
        // #undef first: the value it shadows is already defined by the time this header is included,
        // and redefining it without the #undef is a diagnostic, not an override.
        var text = ConfigOverrides.Render("config_overrides.h", [Vref("5.0f")]);

        Assert.Contains("#undef  VREF", text);
        Assert.Contains("#define VREF 5.0f", text);
        Assert.True(text.IndexOf("#undef  VREF") < text.IndexOf("#define VREF"));
        // And the reader can see what it replaced.
        Assert.Contains("// was 3.3f", text);
    }

    [Fact]
    public void ValuesThatArentNumbersSurviveIntact()
    {
        // A #define body is a C token sequence, not a number: an enum name and an expression are
        // both ordinary values here, and quoting or parsing them would corrupt them.
        var text = ConfigOverrides.Render(
            "config_overrides.h",
            [
                new("ADS1115_SAMPLE_SPEED", "config.h", "ADS1115_RATE_475", "ADS1115_RATE_860"),
                new("LCD_MSG_SIZE", "config.h", "16 + 1", "32 + 1"),
            ]
        );

        Assert.Contains("#define ADS1115_SAMPLE_SPEED ADS1115_RATE_860", text);
        Assert.Contains("#define LCD_MSG_SIZE 32 + 1", text);
    }

    [Fact]
    public void NothingOverridden_IsStillAValidHeader()
    {
        var text = ConfigOverrides.Render("debug_overrides.h", []);

        Assert.Contains("#ifndef INC_CONFIG_DEBUG_OVERRIDES_H_", text);
        Assert.Contains("#endif", text);
        Assert.DoesNotContain("#define STM32", text);
    }

    [Fact]
    public void TheFileIsStable_SoAnUnchangedBuildIsNotAChangedBuild()
    {
        // Same overrides in a different order must produce byte-identical text: ninja keys off
        // content/mtime, and a file that churned would rebuild the whole firmware every time.
        var a = ConfigOverrides.Render(
            "config_overrides.h",
            [Vref("5.0f"), new("K_P", "config.h", "1.0f", "2.0f")]
        );
        var b = ConfigOverrides.Render(
            "config_overrides.h",
            [new("K_P", "config.h", "1.0f", "2.0f"), Vref("5.0f")]
        );

        Assert.Equal(a, b);
    }

    [Fact]
    public void EachOverrideGoesToTheHeaderItBelongsTo()
    {
        ConfigOverrides.Write(
            _dir,
            [Vref("5.0f"), new("LED_BLINK_TASK_ENABLE", "debug.h", "0", "1")]
        );

        Assert.Contains("#define VREF 5.0f", File.ReadAllText(ConfigOverridesPath));
        Assert.DoesNotContain("LED_BLINK", File.ReadAllText(ConfigOverridesPath));
        Assert.Contains("#define LED_BLINK_TASK_ENABLE 1", File.ReadAllText(DebugOverridesPath));
    }

    [Fact]
    public void BothFilesAreWritten_EvenWithNothingToOverride()
    {
        // The dangerous case: a file left from a previous build would keep applying a setting the
        // user has since reset, and the board would come back holding it with nothing on screen
        // saying so.
        ConfigOverrides.Write(_dir, [Vref("5.0f")]);
        Assert.Contains("#define VREF 5.0f", File.ReadAllText(ConfigOverridesPath));

        ConfigOverrides.Write(_dir, []);
        Assert.DoesNotContain("#define VREF", File.ReadAllText(ConfigOverridesPath));
        Assert.True(File.Exists(DebugOverridesPath));
    }

    [Fact]
    public void RewritingTheSameOverrides_LeavesTheFileAlone()
    {
        ConfigOverrides.Write(_dir, [Vref("5.0f")]);
        var firstWrite = File.GetLastWriteTimeUtc(ConfigOverridesPath);

        var written = ConfigOverrides.Write(_dir, [Vref("5.0f")]);

        Assert.Empty(written);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(ConfigOverridesPath));
    }

    [Fact]
    public void ChangingAnOverride_RewritesTheFile()
    {
        ConfigOverrides.Write(_dir, [Vref("5.0f")]);
        var written = ConfigOverrides.Write(_dir, [Vref("4.2f")]);

        Assert.Equal("config_overrides.h", Assert.Single(written));
        Assert.Contains("#define VREF 4.2f", File.ReadAllText(ConfigOverridesPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0f // sneaky")]
    [InlineData("1.0f /* sneaky */")]
    [InlineData("1.0f\n#define EVIL 1")]
    public void AValueThatCouldRewriteTheHeaderAroundIt_IsRefused(string value)
    {
        // The generated file is C that nobody reviews, so a value carrying a comment or a newline
        // could define anything it liked. Fail the build instead.
        Assert.Throws<InvalidOperationException>(() =>
            ConfigOverrides.Render("config_overrides.h", [Vref(value)])
        );
    }
}
