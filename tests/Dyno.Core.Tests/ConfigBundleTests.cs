using Dyno.Core.SysConfig;
using Xunit;

namespace Dyno.Core.Tests;

public class ConfigBundleJsonTests
{
    private static ConfigBundle Bundle() =>
        new(
            new Dictionary<string, double> { ["K_P"] = 2.5, ["PID_TASK_OSDELAY"] = 10 },
            new Dictionary<string, double> { ["GEAR_RATIO"] = 4.0 },
            new Dictionary<string, string> { ["USB_TX_BUFFER_SIZE"] = "512", ["VREF"] = "3.3f" }
        );

    [Fact]
    public void RoundTripsEverySection()
    {
        var read = ConfigBundleJson.Read(ConfigBundleJson.Write(Bundle()));

        Assert.Empty(read.Problems);
        Assert.Equal(2.5, read.Bundle.Runtime["K_P"]);
        Assert.Equal(10, read.Bundle.Runtime["PID_TASK_OSDELAY"]);
        Assert.Equal(4.0, read.Bundle.PcConstants["GEAR_RATIO"]);
        Assert.Equal("512", read.Bundle.CompileTime["USB_TX_BUFFER_SIZE"]);
        Assert.Equal("3.3f", read.Bundle.CompileTime["VREF"]);
    }

    [Fact]
    public void CompileTimeValuesStayVerbatimRatherThanBecomingNumbers()
    {
        // These are C tokens pasted into a header, not quantities. "0.95f" parsed as a number and
        // written back as 0.95 would no longer be the float literal the firmware was built with.
        var bundle = new ConfigBundle(
            ConfigBundle.Empty.Runtime,
            ConfigBundle.Empty.PcConstants,
            new Dictionary<string, string> { ["MAX_DUTY"] = "0.95f", ["LCD_SIZE"] = "16 + 1" }
        );

        var read = ConfigBundleJson.Read(ConfigBundleJson.Write(bundle));

        Assert.Equal("0.95f", read.Bundle.CompileTime["MAX_DUTY"]);
        Assert.Equal("16 + 1", read.Bundle.CompileTime["LCD_SIZE"]);
    }

    [Fact]
    public void AnAbsentSectionIsNotAProblem()
    {
        // A hand-written file that sets two gains and nothing else is the normal case; every
        // setting it leaves out is reported by the import as missing, not by the parser as broken.
        var read = ConfigBundleJson.Read("""{ "runtime": { "K_P": 1 } }""");

        Assert.Empty(read.Problems);
        Assert.Equal(1, read.Bundle.Runtime["K_P"]);
        Assert.Empty(read.Bundle.PcConstants);
        Assert.Empty(read.Bundle.CompileTime);
    }

    [Fact]
    public void OneUnusableEntryCostsOnlyThatEntry()
    {
        var read = ConfigBundleJson.Read(
            """{ "runtime": { "K_P": "not a number", "K_I": 2 }, "pcConstants": [1, 2] }"""
        );

        Assert.Equal(2, read.Bundle.Runtime["K_I"]);
        Assert.False(read.Bundle.Runtime.ContainsKey("K_P"));
        Assert.Contains(read.Problems, p => p.Contains("K_P"));
        Assert.Contains(read.Problems, p => p.Contains("pcConstants"));
    }

    [Fact]
    public void HandWrittenScalarsAreAcceptedForCompileTimeSettings()
    {
        // Someone typing this file will write 512 and true, not "512" and "true".
        var read = ConfigBundleJson.Read("""{ "compileTime": { "SIZE": 512, "ENABLED": true } }""");

        Assert.Empty(read.Problems);
        Assert.Equal("512", read.Bundle.CompileTime["SIZE"]);
        Assert.Equal("true", read.Bundle.CompileTime["ENABLED"]);
    }

    [Fact]
    public void ANewerFormatVersionIsReadAndReported()
    {
        var read = ConfigBundleJson.Read(
            $$"""{ "formatVersion": {{ConfigBundleJson.FormatVersion + 1}}, "runtime": { "K_P": 1 } }"""
        );

        Assert.Equal(1, read.Bundle.Runtime["K_P"]); // what it does understand still lands
        Assert.Contains(read.Problems, p => p.Contains("format version"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("42")]
    public void ADocumentThatIsNotAnObjectIsRejectedWhole(string text)
    {
        // Nothing is staged from these, which is the point: a half-applied import leaves the page
        // holding a mixture nobody chose.
        Assert.Throws<InvalidDataException>(() => ConfigBundleJson.Read(text));
    }
}
