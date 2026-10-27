using System.IO.Ports;
using System.Text;

namespace KeyboardBridge;

// A command line link as the connection loop sees it: the same lines ("S,...", "PING", "DIAG") whether
// they travel over the encrypted Wi-Fi session or over the NodeMCU's USB serial port.
public interface ILineLink : IDisposable
{
    Task WriteLineAsync(string line);
    Task<string?> ReadLineAsync(CancellationToken ct);
}

// Splits the bytes coming back over USB serial into replies. The NodeMCU's TX wire also carries the lines it
// sends the Pico and its boot messages; only lines that start with '#' are replies for the PC.
public sealed class UsbReplyParser
{
    public const int MaxLine = 600;  // the longest reply (DIAG) is well under this
    readonly StringBuilder partial = new();
    readonly Queue<string> ready = new();

    public void Feed(byte[] data, int count)
    {
        for (int i = 0; i < count; i++)
        {
            byte b = data[i];
            if (b == '\n')
            {
                if (partial.Length > 1 && partial[0] == '#') ready.Enqueue(partial.ToString(1, partial.Length - 1).TrimEnd('\r'));
                partial.Clear();
            }
            else if (partial.Length < MaxLine) partial.Append((char)b);
            else partial.Clear();
        }
    }

    public bool TryTake(out string line) => ready.TryDequeue(out line!);
}

// USB serial link to the NodeMCU (firmware 3.1+), see docs/protocol.md "USB serial". The PC writes the
// command lines to the board's own USB-UART; replies come back on the same wire as "#<reply>" lines, mixed
// with everything the NodeMCU sends the Pico (which the PC ignores). There is no handshake and no encryption:
// it is a cable between two machines you own.
public sealed class UsbLink : ILineLink
{
    public const int BaudRate = 115200;  // must match the NodeMCU's Serial and the Pico's UART
    readonly SerialPort port;
    readonly Stream stream;
    readonly SemaphoreSlim txGate = new(1, 1);
    readonly byte[] chunk = new byte[256];
    readonly UsbReplyParser parser = new();
    public string PortName { get; }
    public string Firmware { get; private set; } = "";

    UsbLink(SerialPort port)
    {
        this.port = port;
        PortName = port.PortName;
        stream = port.BaseStream;
    }

    public static string[] AvailablePorts() => SerialPort.GetPortNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Opens the port and checks that a NodeMCU with firmware 3.1+ answers. "auto" tries every port.</summary>
    public static async Task<UsbLink> ConnectAsync(string portName, CancellationToken ct, int timeoutMs = 1500)
    {
        var candidates = string.Equals(portName, "auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(portName)
            ? AvailablePorts() : new[] { portName };
        if (candidates.Length == 0) throw new IOException("No serial ports found");
        Exception? last = null;
        foreach (var name in candidates)
        {
            try { return await OpenAsync(name, ct, timeoutMs); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { last = ex; }
        }
        throw new IOException(candidates.Length == 1 ? $"{candidates[0]}: {last?.Message}" :
            "No NodeMCU answered on " + string.Join(", ", candidates) + (last is null ? "" : " (" + last.Message + ")"));
    }

    static async Task<UsbLink> OpenAsync(string name, CancellationToken ct, int timeoutMs)
    {
        // DTR/RTS stay low: the NodeMCU's auto-reset circuit would reboot the board if they toggled.
        var port = new SerialPort(name, BaudRate, Parity.None, 8, StopBits.One)
        {
            DtrEnable = false, RtsEnable = false, Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout, WriteTimeout = 1000,
            ReadBufferSize = 8192, WriteBufferSize = 2048,
        };
        UsbLink? link = null;
        try
        {
            port.Open();
            port.DiscardInBuffer();
            link = new UsbLink(port);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            // Ask until answered: the first bytes can be lost while the driver settles.
            var asker = Task.Run(async () =>
            {
                while (!timeout.IsCancellationRequested) { await link.WriteLineAsync("USB?"); await Task.Delay(250, timeout.Token); }
            }, timeout.Token);
            while (true)
            {
                string? reply = await link.ReadLineAsync(timeout.Token);
                if (reply is null) throw new IOException("port closed");
                if (reply.StartsWith("USB,"))
                {
                    link.Firmware = reply[4..];
                    timeout.Cancel();
                    try { await asker; } catch { /* the asker ends by cancellation */ }
                    return link;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (link is not null) link.Dispose(); else port.Dispose();
            throw new IOException("no reply from a NodeMCU running firmware 3.1 or newer");
        }
        catch
        {
            if (link is not null) link.Dispose(); else port.Dispose();
            throw;
        }
    }

    public async Task WriteLineAsync(string line)
    {
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        await txGate.WaitAsync();
        try
        {
            await stream.WriteAsync(bytes);  // one write per line so lines never interleave
            await stream.FlushAsync();
        }
        finally { txGate.Release(); }
    }

    /// <summary>Next "#" reply from the NodeMCU, without the "#". Other bytes on the wire are skipped.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            if (parser.TryTake(out var line)) return line;
            int n = await stream.ReadAsync(chunk, ct).AsTask().WaitAsync(ct);  // SerialStream ignores the token; Dispose ends the read
            if (n == 0) return null;
            parser.Feed(chunk, n);
        }
    }

    public void Dispose()
    {
        try { port.Close(); } catch { /* already gone */ }
        port.Dispose();
        txGate.Dispose();
    }
}
