using System.IO.Ports;

namespace Dyno.Core.Serial;

/// <summary>
/// <see cref="ISerialConnection"/> over <see cref="SerialPort"/>. Cross-platform by virtue
/// of System.IO.Ports: the same code drives <c>COM3</c> on Windows and <c>/dev/ttyACM0</c>
/// on Linux (where the user must be in the <c>dialout</c> group). Defaults match the
/// firmware's USB-CDC link: 115200 8-N-1.
/// </summary>
public sealed class SerialConnection : ISerialConnection
{
    private readonly SerialPort _port;

    public SerialConnection(string portName, int baudRate = 115200)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
            // Hold DTR for as long as the port is open. On a USB-CDC link there is no other way to
            // tell the device a session began or ended — the cable stays enumerated across a close —
            // so the firmware watches this line (CDC_SET_CONTROL_LINE_STATE) to know the host left
            // and that it must start announcing itself again. Left at its default of false, the
            // device would never see the assert→deassert edge and would never re-announce.
            DtrEnable = true,
        };
    }

    public string PortName => _port.PortName;
    public bool IsOpen => _port.IsOpen;
    public Stream BaseStream => _port.BaseStream;

    public void Open() => _port.Open();

    public void Close()
    {
        if (_port.IsOpen)
        {
            _port.Close();
        }
    }

    public void Write(ReadOnlySpan<byte> data) => _port.BaseStream.Write(data);

    public void Dispose() => _port.Dispose();

    /// <summary>Serial ports currently visible to the OS (e.g. for a connection picker).</summary>
    public static string[] AvailablePorts() => SerialPort.GetPortNames();
}
