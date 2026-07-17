using System.Globalization;
using Dyno.Core.Messages;
using Dyno.Core.Protocol;

namespace Dyno.Core;

/// <summary>
/// Records the device's streaming measurement samples to a CSV file for later analysis — the
/// host-side successor to the firmware-era per-module data logger. Emits one long-format row per
/// sample with unrelated columns left blank, so independently-streamed channels (e.g. speed and
/// force) can be re-joined on the device timestamp. Diagnostic and control traffic (task-monitor
/// health, faults, responses) is not measurement data and is ignored here — that goes to the
/// structured event log instead.
/// </summary>
/// <remarks>
/// Not thread-safe: feed it from a single reader (the <see cref="DeviceClient"/> read loop). The
/// writer is flushed per row so a crash mid-session still leaves a readable file.
/// </remarks>
public sealed class TelemetryLogger : IDisposable
{
    /// <summary>CSV column order; kept in one place so the header and rows can't drift.</summary>
    public const string Header =
        "host_time,device_ts,source,angular_velocity,angular_acceleration,force,duty_cycle,torque,power,raw_value";

    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;

    /// <summary>
    /// Wraps an arbitrary <see cref="TextWriter"/> (used directly by tests). When
    /// <paramref name="writeHeader"/> is set the CSV header row is written immediately.
    /// </summary>
    public TelemetryLogger(TextWriter writer, bool writeHeader = true, bool ownsWriter = false)
    {
        _writer = writer;
        _ownsWriter = ownsWriter;
        if (writeHeader)
        {
            _writer.WriteLine(Header);
        }
    }

    /// <summary>
    /// Opens (creating parent directories) or appends to a CSV file at <paramref name="path"/>.
    /// The header is written only for a new/empty file so appended sessions stay valid CSV. The
    /// stream auto-flushes so rows survive an unclean shutdown.
    /// </summary>
    public static TelemetryLogger CreateFile(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        bool hasContent = File.Exists(path) && new FileInfo(path).Length > 0;
        var stream = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
        )
        {
            AutoFlush = true,
        };
        return new TelemetryLogger(stream, writeHeader: !hasContent, ownsWriter: true);
    }

    /// <summary>Writes a CSV row for measurement samples; every other message type is ignored.</summary>
    public void Log(DeviceMessage message)
    {
        switch (message)
        {
            case OpticalEncoderSample s:
                Write(
                    s.Data.timestamp,
                    "OPTICAL_ENCODER",
                    angularVelocity: s.Data.angular_velocity,
                    angularAcceleration: s.Data.angular_acceleration,
                    rawValue: s.Data.raw_value
                );
                break;
            case ForceSensorSample s:
                Write(
                    s.Data.timestamp,
                    Friendly(s.Source),
                    force: s.Data.force,
                    rawValue: s.Data.raw_value
                );
                break;
            case BpmSample s:
                Write(
                    s.Data.timestamp,
                    "BPM_CONTROLLER",
                    dutyCycle: s.Data.duty_cycle,
                    rawValue: s.Data.raw_value
                );
                break;
            case SessionControllerSample s:
                Write(
                    s.Data.timestamp,
                    "SESSION_CONTROLLER",
                    torque: s.Data.torque,
                    power: s.Data.power
                );
                break;
        }
    }

    private void Write(
        uint deviceTs,
        string source,
        float? angularVelocity = null,
        float? angularAcceleration = null,
        float? force = null,
        float? dutyCycle = null,
        float? torque = null,
        float? power = null,
        uint? rawValue = null
    )
    {
        var c = CultureInfo.InvariantCulture;
        _writer.WriteLine(
            string.Join(
                ',',
                DateTimeOffset.Now.ToString("O", c),
                deviceTs.ToString(c),
                source,
                Fmt(angularVelocity, c),
                Fmt(angularAcceleration, c),
                Fmt(force, c),
                Fmt(dutyCycle, c),
                Fmt(torque, c),
                Fmt(power, c),
                rawValue?.ToString(c) ?? string.Empty
            )
        );
    }

    private static string Fmt(float? value, IFormatProvider provider) =>
        value?.ToString("R", provider) ?? string.Empty;

    private static string Friendly(task_offset_t offset) =>
        offset.ToString().Replace("TASK_OFFSET_", string.Empty);

    public void Dispose()
    {
        if (_ownsWriter)
        {
            _writer.Dispose();
        }
    }
}
