using Dyno.Core;
using Xunit;

namespace Dyno.Core.Tests;

public class ForceSensorSampleRateTests
{
    // Locks the ADS1115 code→SPS mapping (hardcoded from the datasheet, so worth guarding).
    [Theory]
    [InlineData(ForceSensorSampleRate.Sps8, 0, 8)]
    [InlineData(ForceSensorSampleRate.Sps64, 3, 64)]
    [InlineData(ForceSensorSampleRate.Sps128, 4, 128)]
    [InlineData(ForceSensorSampleRate.Sps860, 7, 860)]
    public void Maps_WireCode_And_SamplesPerSecond(ForceSensorSampleRate rate, byte code, int sps)
    {
        Assert.Equal(code, (byte)rate);
        Assert.Equal(sps, rate.SamplesPerSecond());
        Assert.Equal($"{sps} SPS", rate.ToLabel());
    }
}
