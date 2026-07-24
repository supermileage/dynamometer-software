namespace Dyno.Core.Derived;

/// <summary>One derived reading: what the dyno was doing at the instant a measurement landed.</summary>
/// <param name="Timestamp">Device timestamp of the force sample that produced it. Always the force
/// task's clock, never the encoder's, so a run of these is ordered.</param>
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
///   omega_geared  = ω ÷ gear_ratio
///   power = torque × ω
/// </code>
///
/// The gearing multiplies torque and divides speed — see <see cref="GearTorque"/> and
/// <see cref="GearVelocity"/>, which are a pair and are kept next to each other for that reason.
///
/// Force and encoder samples arrive from independent device tasks at different rates and never
/// share a timestamp. Readings are clocked off <em>force alone</em>, against the held ω and α of
/// the most recent encoder sample — the same sample-and-hold the firmware did, just on this side
/// of the wire. Nothing is emitted until both have been seen at least once: the firmware began
/// from a zeroed struct and so published torque derived from a force of zero before the load cell
/// had ever reported.
///
/// Deriving on either arrival would interleave two device clocks into one series, and they are
/// not mutually ordered — a few ms of inter-task skew is normal, so the timestamps could step
/// backwards. Everything downstream (the plot buffers, the decimator, the CSV merge) is built on
/// times that only ever increase. Force is the right clock to keep: it runs 10× faster than the
/// encoder (1 ms against 10 ms), and it carries the term that actually moves during a pull.
/// Nothing is lost by not emitting on the encoder, because ω and α are held values either way —
/// a fresh one simply applies from the next force sample, at most a millisecond later.
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

    /// <summary>Torque at the output shaft: a reduction multiplies it.</summary>
    public static double GearTorque(double torque, double gearRatio) => torque * gearRatio;

    /// <summary>Angular velocity at the output shaft: a reduction divides it. This is the exact
    /// reciprocal of what the gearing does to torque, which is what makes the geared pair carry the
    /// same power as the sensed pair — an ideal gearbox trades speed for torque, it does not create
    /// either. Multiplying both (as this used to) reported ratio² times the power actually measured.
    /// </summary>
    /// <remarks>A ratio that is not finite and positive is treated as direct drive rather than
    /// dividing by it: the value comes from a saved PC constant, which is range-checked as it is
    /// typed but not as it is loaded, so a 0 in the database would otherwise reach here and put an
    /// infinity on screen.</remarks>
    public static double GearVelocity(double angularVelocity, double gearRatio) =>
        gearRatio is > 0 and < double.PositiveInfinity
            ? angularVelocity / gearRatio
            : angularVelocity;

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

    /// <summary>Takes an encoder sample. Emits nothing by design — it updates the held ω and α,
    /// which the next force sample derives against (see the class remarks).</summary>
    public void OnEncoder(float angularVelocity, float angularAcceleration)
    {
        _angularVelocity = angularVelocity;
        _angularAcceleration = angularAcceleration;
        _haveEncoder = true;
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
            (float)GearTorque(torque, GearRatio),
            (float)(torque * _angularVelocity)
        );
    }
}
