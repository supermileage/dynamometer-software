namespace Dyno.Core.Serial;

/// <summary>
/// Minimal serial-port abstraction so the device client and parser can be exercised with
/// an in-memory fake in tests, with no real port. Implemented for hardware by
/// <see cref="SerialConnection"/>.
/// </summary>
public interface ISerialConnection : IDisposable
{
    string PortName { get; }
    bool IsOpen { get; }

    void Open();
    void Close();

    /// <summary>The byte stream for async reads/writes; valid only while open.</summary>
    Stream BaseStream { get; }

    void Write(ReadOnlySpan<byte> data);
}
