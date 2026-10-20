using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KeyboardBridge;

// Query the ESP8266's advertised hostname over mDNS, without Bonjour dependency.
// QU (unicast response) allows listening on an ephemeral port instead of 5353.
static class MdnsDiscovery
{
    const string Name = "wifi-keyboard-bridge.local";
    public static async Task<string?> ResolveAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = 1200;
            byte[] query = BuildQuery();
            await udp.SendAsync(query, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult response = await udp.ReceiveAsync(timeout.Token);
                var address = ParseAddress(response.Buffer);
                if (address != null) return address.ToString();
            }
        }
        catch (OperationCanceledException) { if (cancellationToken.IsCancellationRequested) throw; }
        catch (SocketException) { }
        catch (Exception ex) { DiagnosticLog.Write("mDNS discovery: " + ex.GetType().Name); }
        // On some Windows setups the OS resolver already supports .local.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1200));
            var ips = await Dns.GetHostAddressesAsync(Name, timeout.Token);
            return ips.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch (OperationCanceledException) { if (cancellationToken.IsCancellationRequested) throw; }
        catch (SocketException) { }
        return null;
    }
    static byte[] BuildQuery()
    {
        var bytes = new List<byte>(64);
        bytes.AddRange(new byte[12]); // mDNS ID 0; one question
        bytes[5] = 1;
        foreach (var label in Name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        bytes.AddRange(new byte[] { 0, 1, 0x80, 1 }); // A, IN with unicast-response bit
        return bytes.ToArray();
    }
    static IPAddress? ParseAddress(byte[] data)
    {
        if (data.Length < 12 || (data[2] & 0x80) == 0) return null;
        int questions = (data[4] << 8) | data[5];
        int answers = (data[6] << 8) | data[7];
        if (questions > 16 || answers > 64) return null;
        int offset = 12;
        for (int i = 0; i < questions; i++)
        {
            if (!SkipName(data, ref offset) || offset + 4 > data.Length) return null;
            offset += 4;
        }
        for (int i = 0; i < answers; i++)
        {
            if (!ReadName(data, ref offset, out var name) || offset + 10 > data.Length) return null;
            int type = (data[offset] << 8) | data[offset + 1];
            int klass = ((data[offset + 2] << 8) | data[offset + 3]) & 0x7fff;
            int size = (data[offset + 8] << 8) | data[offset + 9];
            offset += 10;
            if (offset + size > data.Length) return null;
            if (name.Equals(Name, StringComparison.OrdinalIgnoreCase) && type == 1 && klass == 1 && size == 4)
                return new IPAddress(data.AsSpan(offset, 4));
            offset += size;
        }
        return null;
    }
    static bool SkipName(byte[] data, ref int offset) => ReadName(data, ref offset, out _);
    static bool ReadName(byte[] data, ref int offset, out string name)
    {
        var labels = new List<string>();
        int pos = offset;
        bool jumped = false;
        for (int loops = 0; loops < 64; loops++)
        {
            if (pos >= data.Length) break;
            int length = data[pos++];
            if (length == 0)
            {
                if (!jumped) offset = pos;
                name = string.Join('.', labels);
                return true;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (pos >= data.Length) break;
                int next = ((length & 0x3F) << 8) | data[pos++];
                if (!jumped) offset = pos;
                jumped = true; pos = next;
                continue;
            }
            if ((length & 0xC0) != 0 || length > 63 || pos + length > data.Length) break;
            labels.Add(Encoding.ASCII.GetString(data, pos, length));
            pos += length;
        }
        name = "";
        return false;
    }
}
