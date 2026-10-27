using System.Net.Sockets;

namespace KeyboardBridge;

// Windows TCP_INFO (SIO_TCP_INFO, Windows 10 1703+). Shows whether data sent to the
// NodeMCU is still unacknowledged, and how much Windows had to retransmit.
readonly record struct TcpStats(uint BytesInFlight, uint BytesRetrans, uint TimeoutEpisodes, uint RttUs)
{
    const int SioTcpInfo = unchecked((int)0xD8000027);
    static readonly byte[] version0 = BitConverter.GetBytes(0);

    public static TcpStats? Read(Socket socket, byte[] buffer)
    {
        try
        {
            if (socket.IOControl(SioTcpInfo, version0, buffer) < 88) return null;
            // TCP_INFO_v0 offsets: RttUs 20, BytesInFlight 28, BytesRetrans 68, TimeoutEpisodes 80.
            return new TcpStats(BitConverter.ToUInt32(buffer, 28), BitConverter.ToUInt32(buffer, 68),
                BitConverter.ToUInt32(buffer, 80), BitConverter.ToUInt32(buffer, 20));
        }
        catch (Exception) { return null; }  // Older Windows: run without forward-path detection.
    }
}
