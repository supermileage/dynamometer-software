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

        calc.OnEncoder(5f, 0f);
        Assert.True(calc.IsPrimed);
        Assert.NotNull(calc.OnForce(3, 10f)); // primed: the next force sample derives
    }

    [Fact]
    public void EncoderFirstAlsoWaitsForForce()
    {
        var calc = Calculator();

        calc.OnEncoder(5f, 0f);
        Assert.NotNull(calc.OnForce(2, 10f));
    }

    /// <summary>Readings are clocked off force alone. An encoder sample updates the held ω and α
    /// but produces nothing itself, so the series carries one device clock and cannot step
    /// backwards when the two tasks' timestamps interleave.</summary>
    [Fact]
    public void EncoderArrivalProducesNoReadingOfItsOwn()
    {
        var calc = Calculator(leverArm: 1.0);
        calc.OnForce(1, 10f);
        calc.OnEncoder(2f, 0f);

        // Only force emits, so every reading is stamped from the force task.
        Assert.Equal(100u, calc.OnForce(100, 10f)!.Value.Timestamp);
        calc.OnEncoder(3f, 0f);
        Assert.Equal(101u, calc.OnForce(101, 10f)!.Value.Timestamp);
    }

    [Fact]
    public void TorqueIsForceTimesLeverArmWhenInertiaIsZero()
    {
        var calc = Calculator(inertia: 0, leverArm: 0.25);
        calc.OnEncoder(4f, 100f); // acceleration must not matter while I is 0

        var d = calc.OnForce(2, 40f);

        Assert.NotNull(d);
        Assert.Equal(10f, d!.Value.Torque, 4); // 40 N × 0.25 m
    }

    [Fact]
    public void InertiaContributesTheAlphaTerm()
    {
        var calc = Calculator(inertia: 0.5, leverArm: 2.0);
        calc.OnEncoder(0f, 10f);

        var d = calc.OnForce(1, 3f);

        // 0.5 × 10 + 3 × 2 = 11
        Assert.Equal(11f, d!.Value.Torque, 4);
    }

    [Fact]
    public void PowerIsTorqueTimesAngularVelocity()
    {
        var calc = Calculator(leverArm: 1.0);
        calc.OnEncoder(3f, 0f);

        var d = calc.OnForce(1, 5f);

        Assert.Equal(5f, d!.Value.Torque, 4);
        Assert.Equal(15f, d.Value.Power, 4); // 5 N·m × 3 rad/s
    }

    [Fact]
    public void GearRatioScalesTorqueButNotTheSensedValueOrPower()
    {
        var calc = Calculator(leverArm: 1.0, gearRatio: 4.0);
        calc.OnEncoder(2f, 0f);

        var d = calc.OnForce(2, 10f);

        Assert.Equal(10f, d!.Value.Torque, 4); // what the sensors say
        Assert.Equal(40f, d.Value.TorqueGeared, 4); // ×4 at the output
        Assert.Equal(20f, d.Value.Power, 4); // from the sensed torque
    }

    [Fact]
    public void GearingTradesSpeedForTorqueRatherThanCreatingBoth()
    {
        // The invariant that says the pair is reciprocal, and the one that was violated when both
        // torque and velocity were multiplied: an ideal gearbox moves power across, it does not
        // manufacture it. Multiplying both reported ratio² times the power actually measured.
        const double ratio = 4.0;
        const double sensedTorque = 10.0;
        const double sensedVelocity = 2.0;

        double gearedTorque = DerivedQuantities.GearTorque(sensedTorque, ratio);
        double gearedVelocity = DerivedQuantities.GearVelocity(sensedVelocity, ratio);

        Assert.Equal(40.0, gearedTorque, 6);
        Assert.Equal(0.5, gearedVelocity, 6);
        Assert.Equal(sensedTorque * sensedVelocity, gearedTorque * gearedVelocity, 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnusableGearRatioLeavesTheVelocityAloneRatherThanReportingInfinity(double ratio)
    {
        // PC constants are range-checked as they are typed but not as they are loaded, so a bad
        // row in the database reaches the derivation. Direct drive is the honest fallback; an
        // infinity on the readout is not.
        Assert.Equal(3.0, DerivedQuantities.GearVelocity(3.0, ratio), 6);
    }

    /// <summary>Each reading is stamped with the force sample that triggered it — the only value
    /// known to be current, and the one clock the whole series is on.</summary>
    [Fact]
    public void EachReadingCarriesTheTimestampOfTheSampleThatProducedIt()
    {
        var calc = Calculator();
        calc.OnEncoder(1f, 0f);

        Assert.Equal(200u, calc.OnForce(200, 1f)!.Value.Timestamp);
        Assert.Equal(300u, calc.OnForce(300, 2f)!.Value.Timestamp);
    }

    /// <summary>Sample-and-hold: each force sample is paired with the latest encoder values, and a
    /// new encoder sample applies from the next force sample onwards.</summary>
    [Fact]
    public void ForceIsDerivedAgainstTheHeldEncoderValues()
    {
        var calc = Calculator(leverArm: 1.0);
        calc.OnEncoder(2f, 0f);
        calc.OnForce(2, 10f);

        // Force moves; the held velocity still applies.
        var afterForce = calc.OnForce(3, 20f);
        Assert.Equal(20f, afterForce!.Value.Torque, 4);
        Assert.Equal(40f, afterForce.Value.Power, 4); // still ω = 2

        // Velocity moves; it takes effect on the next force sample, against the held force.
        calc.OnEncoder(5f, 0f);
        var afterEncoder = calc.OnForce(4, 20f);
        Assert.Equal(20f, afterEncoder!.Value.Torque, 4);
        Assert.Equal(100f, afterEncoder.Value.Power, 4);
    }

    [Fact]
    public void ResetStopsDerivingUntilBothInputsReturn()
    {
        var calc = Calculator();
        calc.OnForce(1, 10f);
        calc.OnEncoder(5f, 0f);
        Assert.True(calc.IsPrimed);

        calc.Reset();

        Assert.False(calc.IsPrimed);
        Assert.Null(calc.OnForce(3, 10f)); // a stale velocity must not be reused across the gap
        calc.OnEncoder(5f, 0f);
        Assert.NotNull(calc.OnForce(5, 10f));
    }

    [Fact]
    public void ConstantsTakeEffectOnTheNextSample()
    {
        var calc = Calculator(leverArm: 1.0);
        calc.OnEncoder(1f, 0f);
        Assert.Equal(10f, calc.OnForce(2, 10f)!.Value.Torque, 4);

        // Correcting a constant changes what follows, without a reconnect or a reflash.
        calc.ForceLeverArmM = 2.0;

        Assert.Equal(20f, calc.OnForce(3, 10f)!.Value.Torque, 4);
    }
}
