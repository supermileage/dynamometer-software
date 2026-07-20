namespace Dyno.Core.Derived;

/// <summary>One derived reading: what the dyno was doing at the instant a measurement landed.</summary>
/// <param name="Timestamp">Device timestamp of the sample that produced it — the newly arrived
/// one, since that is the only value known to be current.</param>
public readonly record struct DerivedSample(
    uint Timestamp,
    float Torque,
    float TorqueGeared,
    float Power
);

/// <summary>
/// Derives torque and power on this PC from the raw force and encoder streams.
/// </summary>
/// <remarks>
/// The firmware used to compute these and stream them. Doing it here instead means the constants
/// are the app's, so getting one wrong is recoverable: fix the inertia or the lever arm and every
/// recorded run recomputes, where before the wrong numbers were already frozen into the recording
/// and only a rebuild and reflash would fix the next one.
///
/// <code>
///   torque = I·α + F·r
///   torque_geared = torque × gear_ratio
///   power = torque × ω
/// </code>
///
/// Force and encoder samples arrive from independent device tasks at different rates and never
/// share a timestamp, so a reading is produced whenever <em>either</em> arrives, pairing it with
/// the most recent value of the other — the same sample-and-hold the firmware did, just on this
/// side of the wire. Nothing is emitted until both have been seen at least once: the firmware
/// began from a zeroed struct and so published torque derived from a force of zero before the
/// load cell had ever reported.
///
/// Not thread-safe: drive it from one thread (the UI thread, alongside the readouts it feeds).
/// </remarks>
public sealed class DerivedQuantities
{
    /// <summary>Rotating assembly's moment of inertia (kg·m²). 0 drops the I·α term.</summary>
    public double MomentOfInertiaKgM2 { get; set; }

    /// <summary>Lever arm from the force sensor to the shaft centre (m).</summary>
    public double ForceLeverArmM { get; set; } = 1.0;

    /// <summary>Sensed shaft to output ratio; 1.0 is direct drive.</summary>
    public double GearRatio { get; set; } = 1.0;

    private float _force;
    private float _angularVelocity;
    private float _angularAcceleration;
    private bool _haveForce;
    private bool _haveEncoder;

    /// <summary>Whether both inputs have been seen, so readings are being produced.</summary>
    public bool IsPrimed => _haveForce && _haveEncoder;

    /// <summary>Takes a force sample; returns the reading it produces, or null while unprimed.</summary>
    public DerivedSample? OnForce(uint timestamp, float force)
    {
        _force = force;
        _haveForce = true;
        return Compute(timestamp);
    }

    /// <summary>Takes an encoder sample; returns the reading it produces, or null while unprimed.</summary>
    public DerivedSample? OnEncoder(
        uint timestamp,
        float angularVelocity,
        float angularAcceleration
    )
    {
        _angularVelocity = angularVelocity;
        _angularAcceleration = angularAcceleration;
        _haveEncoder = true;
        return Compute(timestamp);
    }

    /// <summary>Forgets the held samples, so nothing is derived until both arrive again. Used when
    /// the link drops: the next session's first force reading must not be paired with a velocity
    /// from the previous one.</summary>
    public void Reset()
    {
        _haveForce = false;
        _haveEncoder = false;
        _force = 0;
        _angularVelocity = 0;
        _angularAcceleration = 0;
    }

    private DerivedSample? Compute(uint timestamp)
    {
        if (!IsPrimed)
        {
            return null;
        }

        double torque = MomentOfInertiaKgM2 * _angularAcceleration + _force * ForceLeverArmM;
        return new DerivedSample(
            timestamp,
            (float)torque,
            (float)(torque * GearRatio),
            (float)(torque * _angularVelocity)
        );
    }
}
