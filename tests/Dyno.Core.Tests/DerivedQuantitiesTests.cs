using Dyno.Core.Derived;
using Xunit;

namespace Dyno.Core.Tests;

/// <summary>
/// Torque and power are derived here rather than on the device, from constants the user owns. The
/// arithmetic is <c>torque = I·α + F·r</c>, <c>power = torque · ω</c>, with the gear ratio applied
/// to torque afterwards.
/// </summary>
public class DerivedQuantitiesTests
{
    private static DerivedQuantities Calculator(
        double inertia = 0,
        double leverArm = 1,
        double gearRatio = 1
    ) =>
        new()
        {
            MomentOfInertiaKgM2 = inertia,
            ForceLeverArmM = leverArm,
            GearRatio = gearRatio,
        };

    [Fact]
    public void NothingIsDerivedUntilBothInputsHaveArrived()
    {
        var calc = Calculator();

        Assert.Null(calc.OnForce(1, 10f));
        Assert.False(calc.IsPrimed);

        Assert.NotNull(calc.OnEncoder(2, 5f, 0f));
        Assert.True(calc.IsPrimed);
    }

    [Fact]
    public void EncoderFirstAlsoWaitsForForce()
    {
        var calc = Calculator();

        Assert.Null(calc.OnEncoder(1, 5f, 0f));
        Assert.NotNull(calc.OnForce(2, 10f));
    }

    [Fact]
    public void TorqueIsForceTimesLeverArmWhenInertiaIsZero()
    {
        var calc = Calculator(inertia: 0, leverArm: 0.25);
        calc.OnEncoder(1, 4f, 100f); // acceleration must not matter while I is 0

        var d = calc.OnForce(2, 40f);

        Assert.NotNull(d);
        Assert.Equal(10f, d!.Value.Torque, 4); // 40 N × 0.25 m
    }

    [Fact]
    public void InertiaContributesTheAlphaTerm()
    {
        var calc = Calculator(inertia: 0.5, leverArm: 2.0);
        calc.OnForce(1, 3f);

        var d = calc.OnEncoder(2, 0f, 10f);

        // 0.5 × 10 + 3 × 2 = 11
        Assert.Equal(11f, d!.Value.Torque, 4);
    }

    [Fact]
    public void PowerIsTorqueTimesAngularVelocity()
    {
        var calc = Calculator(leverArm: 1.0);
        calc.OnForce(1, 5f);

        var d = calc.OnEncoder(2, 3f, 0f);

        Assert.Equal(5f, d!.Value.Torque, 4);
        Assert.Equal(15f, d.Value.Power, 4); // 5 N·m × 3 rad/s
    }

    [Fact]
    public void GearRatioScalesTorqueButNotTheSensedValueOrPower()
    {
        var calc = Calculator(leverArm: 1.0, gearRatio: 4.0);
        calc.OnEncoder(1, 2f, 0f);

        var d = calc.OnForce(2, 10f);

        Assert.Equal(10f, d!.Value.Torque, 4); // what the sensors say
        Assert.Equal(40f, d.Value.TorqueGeared, 4); // ×4 at the output
        Assert.Equal(20f, d.Value.Power, 4); // from the sensed torque
    }

    /// <summary>Force and encoder samples never share a timestamp, so each derived reading is
    /// stamped with the measurement that triggered it — the only value known to be current.</summary>
    [Fact]
    public void EachReadingCarriesTheTimestampOfTheSampleThatProducedIt()
    {
        var calc = Calculator();
        calc.OnForce(100, 1f);

        Assert.Equal(200u, calc.OnEncoder(200, 1f, 0f)!.Value.Timestamp);
        Assert.Equal(300u, calc.OnForce(300, 2f)!.Value.Timestamp);
    }

    /// <summary>Sample-and-hold: a new sample of either kind is paired with the latest of the
    /// other, so a reading comes out at the combined rate of both streams.</summary>
    [Fact]
    public void EitherStreamProducesAReadingAgainstTheHeldValueOfTheOther()
    {
        var calc = Calculator(leverArm: 1.0);
        calc.OnEncoder(1, 2f, 0f);
        calc.OnForce(2, 10f);

        // Force moves; the held velocity still applies.
        var afterForce = calc.OnForce(3, 20f);
        Assert.Equal(20f, afterForce!.Value.Torque, 4);
        Assert.Equal(40f, afterForce.Value.Power, 4); // still ω = 2

        // Velocity moves; the held force still applies.
        var afterEncoder = calc.OnEncoder(4, 5f, 0f);
        Assert.Equal(20f, afterEncoder!.Value.Torque, 4);
        Assert.Equal(100f, afterEncoder.Value.Power, 4);
    }

    [Fact]
    public void ResetStopsDerivingUntilBothInputsReturn()
    {
        var calc = Calculator();
        calc.OnForce(1, 10f);
        calc.OnEncoder(2, 5f, 0f);
        Assert.True(calc.IsPrimed);

        calc.Reset();

        Assert.False(calc.IsPrimed);
        Assert.Null(calc.OnForce(3, 10f)); // a stale velocity must not be reused across the gap
        Assert.NotNull(calc.OnEncoder(4, 5f, 0f));
    }

    [Fact]
    public void ConstantsTakeEffectOnTheNextSample()
    {
        var calc = Calculator(leverArm: 1.0);
        calc.OnEncoder(1, 1f, 0f);
        Assert.Equal(10f, calc.OnForce(2, 10f)!.Value.Torque, 4);

        // Correcting a constant changes what follows, without a reconnect or a reflash.
        calc.ForceLeverArmM = 2.0;

        Assert.Equal(20f, calc.OnForce(3, 10f)!.Value.Torque, 4);
    }
}
