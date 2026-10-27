using System.Security.Cryptography;
using System.Text;

namespace KeyboardBridge;

// Protocol v3 security (docs/protocol.md). The shared token never crosses the network:
//   client -> HELLO2 <cn>                     16 random bytes, hex
//   server -> CHAL <sn> <HMAC(token,"WKB3-srv"|cn|sn)>      the client verifies it: the NodeMCU must know the token
//   client -> PROOF <HMAC(token,"WKB3-cli"|cn|sn)>
// Both sides derive two 32-byte keys with HKDF-SHA256 (salt cn|sn, info "WKB3 keys") and every later byte is
// ChaCha20-Poly1305: TCP as [len(2)][ciphertext][tag(16)] with a per-direction frame counter as the nonce,
// UDP as [type][seq(4)][ciphertext][tag(16)] with the sequence number as the nonce.

public sealed record SessionKeys(byte[] ClientToServer, byte[] ServerToClient);

public static class SecureCrypto
{
    public const byte ChanTcp = 1, ChanUdp = 2;
    public const int TagLength = 16, KeyLength = 32, NonceLength = 12;

    public static byte[] Hmac(byte[] key, params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        foreach (var part in parts) h.AppendData(part);
        return h.GetHashAndReset();
    }

    public static byte[] ServerProof(string token, byte[] cn, byte[] sn) =>
        Hmac(Encoding.UTF8.GetBytes(token), Encoding.ASCII.GetBytes("WKB3-srv"), cn, sn);

    public static byte[] ClientProof(string token, byte[] cn, byte[] sn) =>
        Hmac(Encoding.UTF8.GetBytes(token), Encoding.ASCII.GetBytes("WKB3-cli"), cn, sn);

    public static SessionKeys DeriveKeys(string token, byte[] cn, byte[] sn)
    {
        var salt = new byte[32];
        cn.CopyTo(salt, 0);
        sn.CopyTo(salt, 16);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(token), salt);
        var okm = HKDF.Expand(HashAlgorithmName.SHA256, prk, 64, Encoding.ASCII.GetBytes("WKB3 keys"));
        return new SessionKeys(okm[..32], okm[32..]);
    }

    public static byte[] Nonce(byte channel, ulong counter)
    {
        var nonce = new byte[NonceLength];
        nonce[0] = channel;  // TCP and UDP never share a nonce even though they share a key
        BitConverter.TryWriteBytes(nonce.AsSpan(4), counter);
        return nonce;
    }

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

// One direction's cipher. Seal returns ciphertext || tag; Open returns false on any alteration.
public sealed class Aead : IDisposable
{
    readonly ChaCha20Poly1305 cipher;
    public Aead(byte[] key) => cipher = new ChaCha20Poly1305(key);

    public static bool IsSupported => ChaCha20Poly1305.IsSupported;

    public byte[] Seal(byte channel, ulong counter, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[plaintext.Length + SecureCrypto.TagLength];
        cipher.Encrypt(SecureCrypto.Nonce(channel, counter), plaintext, output.AsSpan(0, plaintext.Length),
            output.AsSpan(plaintext.Length), aad);
        return output;
    }

    public bool Open(byte channel, ulong counter, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> sealedData, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        if (sealedData.Length < SecureCrypto.TagLength) return false;
        int length = sealedData.Length - SecureCrypto.TagLength;
        var output = new byte[length];
        try
        {
            cipher.Decrypt(SecureCrypto.Nonce(channel, counter), sealedData[..length], sealedData[length..], output, aad);
        }
        catch (CryptographicException) { return false; }
        plaintext = output;
        return true;
    }

    public void Dispose() => cipher.Dispose();
}

// Authenticated, encrypted line transport over a stream. Lines are the same commands as before
// ("S,...", "PING", ...); the framing hides them and makes replay, reordering and tampering fatal.
public sealed class SecureLink : IDisposable
{
    public const int MaxFramePlaintext = 1024;
    readonly Stream stream;
    readonly Aead tx, rx;
    readonly SemaphoreSlim txGate = new(1, 1);
    ulong txCounter, rxCounter;
    public SessionKeys Keys { get; }

    SecureLink(Stream stream, SessionKeys keys)
    {
        this.stream = stream;
        Keys = keys;
        tx = new Aead(keys.ClientToServer);
        rx = new Aead(keys.ServerToClient);
    }

    /// <summary>Runs the handshake. Throws IOException with a specific reason when it fails.</summary>
    public static async Task<SecureLink> ConnectAsync(Stream stream, string token, CancellationToken ct, int timeoutMs = 3000)
    {
        if (!Aead.IsSupported) throw new PlatformNotSupportedException("ChaCha20-Poly1305 is not available on this Windows version.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            var cn = RandomNumberGenerator.GetBytes(16);
            await WritePlainAsync(stream, "HELLO2 " + SecureCrypto.Hex(cn), timeout.Token);
            string chal = await ReadPlainLineAsync(stream, timeout.Token);
            var fields = chal.Split(' ');
            if (fields.Length != 3 || fields[0] != "CHAL" || fields[1].Length != 32 || fields[2].Length != 64)
                throw new IOException(chal == "ERR" ? "NodeMCU rejected the handshake" :
                    "Unexpected reply to HELLO2 (is the NodeMCU running firmware 3.0 or newer?)");
            byte[] sn, theirProof;
            try { sn = Convert.FromHexString(fields[1]); theirProof = Convert.FromHexString(fields[2]); }
            catch (FormatException) { throw new IOException("Malformed challenge from NodeMCU"); }
            // Mutual authentication: a device that does not know the token (a fake NodeMCU) is refused here.
            if (!CryptographicOperations.FixedTimeEquals(theirProof, SecureCrypto.ServerProof(token, cn, sn)))
                throw new IOException("NodeMCU failed to prove it knows the token (wrong token or an impostor)");
            await WritePlainAsync(stream, "PROOF " + SecureCrypto.Hex(SecureCrypto.ClientProof(token, cn, sn)), timeout.Token);
            var link = new SecureLink(stream, SecureCrypto.DeriveKeys(token, cn, sn));
            string? ok;
            try { ok = await link.ReadLineAsync(timeout.Token, allowPlainError: true); }
            catch { link.Dispose(); throw; }
            if (ok == "ERR") { link.Dispose(); throw new IOException("Authentication rejected by NodeMCU (token mismatch?)"); }
            if (ok != "OK") { link.Dispose(); throw new IOException("Authentication failed (reply: " + (ok ?? "connection closed") + ")"); }
            return link;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"Authentication timed out (no reply within {timeoutMs} ms)");
        }
    }

    static async Task WritePlainAsync(Stream s, string line, CancellationToken ct)
    {
        await s.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), ct);
        await s.FlushAsync(ct);
    }

    static async Task<string> ReadPlainLineAsync(Stream s, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < 200)
        {
            if (await s.ReadAsync(one, ct) == 0) throw new IOException("Connection closed by NodeMCU during handshake");
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
        }
        throw new IOException("Handshake line too long");
    }

    async Task<bool> ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int got = 0;
        while (got < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset + got, count - got), ct);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    /// <summary>Next decrypted line, or null at end of stream. A frame that fails authentication throws.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct = default, bool allowPlainError = false)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(header, 0, 2, ct)) return null;
        if (allowPlainError && header[0] == (byte)'E' && header[1] == (byte)'R') return "ERR";  // the NodeMCU's only plaintext reply
        int length = header[0] | header[1] << 8;
        if (length > MaxFramePlaintext) throw new IOException("Encrypted frame too long");
        var body = new byte[length + SecureCrypto.TagLength];
        if (!await ReadExactAsync(body, 0, body.Length, ct)) return null;
        if (!rx.Open(SecureCrypto.ChanTcp, rxCounter, header, body, out var plaintext))
            throw new IOException("Encrypted frame failed authentication (altered, replayed or out of order)");
        rxCounter++;
        return Encoding.UTF8.GetString(plaintext).TrimEnd('\r', '\n');
    }

    public async Task WriteLineAsync(string line)
    {
        var plaintext = Encoding.UTF8.GetBytes(line);
        if (plaintext.Length > 128) throw new ArgumentException("Command too long for the NodeMCU", nameof(line));
        await txGate.WaitAsync();
        try
        {
            var header = new byte[] { (byte)plaintext.Length, (byte)(plaintext.Length >> 8) };
            var body = tx.Seal(SecureCrypto.ChanTcp, txCounter++, header, plaintext);
            var frame = new byte[2 + body.Length];
            header.CopyTo(frame, 0);
            body.CopyTo(frame, 2);
            await stream.WriteAsync(frame);  // one write per frame, like the old AutoFlush writer
            await stream.FlushAsync();
        }
        finally { txGate.Release(); }
    }

    public void Dispose() { tx.Dispose(); rx.Dispose(); txGate.Dispose(); stream.Dispose(); }
}

// Datagram format helpers shared by the client and the tests.
public static class SecureUdp
{
    public const byte State = 0xC1, Ping = 0xC2, Pong = 0xC3;
    public const int StateLength = 5 + 14 + SecureCrypto.TagLength, PingLength = 5 + 4 + SecureCrypto.TagLength;

    public static byte[] Build(Aead cipher, byte type, uint seq, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[5 + payload.Length + SecureCrypto.TagLength];
        packet[0] = type;
        BitConverter.TryWriteBytes(packet.AsSpan(1), seq);
        var body = cipher.Seal(SecureCrypto.ChanUdp, seq, packet.AsSpan(0, 5), payload);
        body.CopyTo(packet, 5);
        return packet;
    }

    public static bool TryOpen(Aead cipher, ReadOnlySpan<byte> packet, out byte type, out uint seq, out byte[] payload)
    {
        type = 0; seq = 0; payload = Array.Empty<byte>();
        if (packet.Length < 5 + SecureCrypto.TagLength) return false;
        type = packet[0];
        seq = BitConverter.ToUInt32(packet.Slice(1, 4));
        return cipher.Open(SecureCrypto.ChanUdp, seq, packet[..5], packet[5..], out payload);
    }
}
