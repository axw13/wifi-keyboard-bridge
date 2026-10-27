using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeyboardBridge;

// Self-contained test runner (no test framework needed, so it also runs in CI without extra packages).
//   dotnet run --project windows-client/KeyboardBridge.Tests              unit + in-process protocol tests
//   dotnet run --project windows-client/KeyboardBridge.Tests -- --device  also test the real NodeMCU and Pico
// --device needs the Windows client closed (the NodeMCU serves one client), the settings in
// %LOCALAPPDATA%\WifiKeyboardBridge\appsettings.json, and for the HID tests the Pico plugged into this PC.

static class Tests
{
    static int passed, failed;
    static readonly List<string> failures = new();

    static void Check(bool condition, string name, string detail = "")
    {
        if (condition) { passed++; Console.WriteLine("  PASS  " + name); }
        else { failed++; failures.Add(name); Console.WriteLine("  FAIL  " + name + (detail.Length > 0 ? "  -> " + detail : "")); }
    }

    static async Task<bool> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return true; }
        catch { return false; }
        return false;
    }

    static byte[] Hex(string s) => Convert.FromHexString(s);

    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("== Crypto primitives (published test vectors) ==");
        Primitives();
        Console.WriteLine("== Key derivation and proofs ==");
        Derivation();
        Console.WriteLine("== AEAD and datagram integrity ==");
        Integrity();
        Console.WriteLine("== Secure TCP link against an in-process server ==");
        await LinkAgainstFakeServer();
        Console.WriteLine("== USB serial reply parsing ==");
        UsbParsing();
        if (args.Contains("--device"))
        {
            Console.WriteLine("== Real NodeMCU ==");
            await Device.Run();
        }
        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed");
        foreach (var f in failures) Console.WriteLine("  failed: " + f);
        return failed == 0 ? 0 : 1;
    }

    public static void Report(bool ok, string name, string detail = "") => Check(ok, name, detail);

    static void Primitives()
    {
        // RFC 4231 test case 1
        var mac = SecureCrypto.Hmac(Enumerable.Repeat((byte)0x0b, 20).ToArray(), Encoding.ASCII.GetBytes("Hi There"));
        Check(SecureCrypto.Hex(mac) == "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7", "HMAC-SHA256 RFC 4231 case 1");

        // RFC 5869 test case 1
        var ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
        var salt = Enumerable.Range(0, 13).Select(i => (byte)i).ToArray();
        var info = Enumerable.Range(0, 10).Select(i => (byte)(0xf0 + i)).ToArray();
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        Check(SecureCrypto.Hex(prk) == "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5", "HKDF-Extract RFC 5869 case 1");
        var okm = HKDF.Expand(HashAlgorithmName.SHA256, prk, 42, info);
        Check(SecureCrypto.Hex(okm) == "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", "HKDF-Expand RFC 5869 case 1");

        // RFC 8439 section 2.8.2
        Check(Aead.IsSupported, "ChaCha20-Poly1305 supported on this Windows");
        var key = Enumerable.Range(0, 32).Select(i => (byte)(0x80 + i)).ToArray();
        var nonce = Hex("070000004041424344454647");
        var aad = Hex("50515253c0c1c2c3c4c5c6c7");
        var plain = Encoding.ASCII.GetBytes("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var ct = new byte[plain.Length];
        var tag = new byte[16];
        using (var c = new ChaCha20Poly1305(key)) c.Encrypt(nonce, plain, ct, tag, aad);
        Check(SecureCrypto.Hex(tag) == "1ae10b594f09e26a7e902ecbd0600691", "ChaCha20-Poly1305 RFC 8439 tag");
        Check(SecureCrypto.Hex(ct.AsSpan(0, 8)) == "d31a8d34648e60db", "ChaCha20-Poly1305 RFC 8439 ciphertext");
    }

    static void Derivation()
    {
        string token = "test-token-0123456789abcdef";
        var cn = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var sn = Enumerable.Range(101, 16).Select(i => (byte)i).ToArray();
        var keys = SecureCrypto.DeriveKeys(token, cn, sn);
        var again = SecureCrypto.DeriveKeys(token, cn, sn);
        Check(keys.ClientToServer.SequenceEqual(again.ClientToServer) && keys.ServerToClient.SequenceEqual(again.ServerToClient), "key derivation is deterministic");
        Check(!keys.ClientToServer.SequenceEqual(keys.ServerToClient), "client->server and server->client keys differ");
        Check(!SecureCrypto.DeriveKeys(token, sn, cn).ClientToServer.SequenceEqual(keys.ClientToServer), "swapping the nonces changes the keys");
        Check(!SecureCrypto.DeriveKeys(token + "x", cn, sn).ClientToServer.SequenceEqual(keys.ClientToServer), "a different token gives different keys");
        var cn2 = (byte[])cn.Clone(); cn2[0] ^= 1;
        Check(!SecureCrypto.DeriveKeys(token, cn2, sn).ServerToClient.SequenceEqual(keys.ServerToClient), "one flipped nonce bit gives different keys (fresh keys per session)");
        Check(!SecureCrypto.ServerProof(token, cn, sn).SequenceEqual(SecureCrypto.ClientProof(token, cn, sn)),
            "server and client proofs differ (a captured proof cannot be reflected)");
        Check(!SecureCrypto.ClientProof(token + "x", cn, sn).SequenceEqual(SecureCrypto.ClientProof(token, cn, sn)), "a wrong token gives a wrong proof");
        Check(SecureCrypto.ClientProof(token, cn, sn).Length == 32, "client proof length is 32");
    }

    static void Integrity()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var a = new Aead(key);
        var aad = new byte[] { 1, 2, 3, 4, 5 };
        var plain = Encoding.ASCII.GetBytes("S,0,115,0,0,0,0,0");
        var sealedBytes = a.Seal(SecureCrypto.ChanTcp, 7, aad, plain);
        Check(sealedBytes.Length == plain.Length + 16, "sealed length is plaintext + 16");
        Check(!sealedBytes.AsSpan(0, plain.Length).SequenceEqual(plain), "ciphertext differs from plaintext");
        Check(a.Open(SecureCrypto.ChanTcp, 7, aad, sealedBytes, out var back) && back.SequenceEqual(plain), "round trip");
        Check(!a.Seal(SecureCrypto.ChanTcp, 8, aad, plain).SequenceEqual(sealedBytes), "a new counter gives a different ciphertext");

        int tamperedAccepted = 0;
        for (int i = 0; i < sealedBytes.Length; i++)
            for (int bit = 0; bit < 8; bit++)
            {
                var t = (byte[])sealedBytes.Clone();
                t[i] ^= (byte)(1 << bit);
                if (a.Open(SecureCrypto.ChanTcp, 7, aad, t, out _)) tamperedAccepted++;
            }
        Check(tamperedAccepted == 0, $"every single-bit change is rejected ({sealedBytes.Length * 8} variants)");
        Check(!a.Open(SecureCrypto.ChanTcp, 8, aad, sealedBytes, out _), "wrong counter rejected (replay/reorder)");
        Check(!a.Open(SecureCrypto.ChanUdp, 7, aad, sealedBytes, out _), "wrong channel rejected");
        Check(!a.Open(SecureCrypto.ChanTcp, 7, new byte[] { 1, 2, 3, 4, 6 }, sealedBytes, out _), "altered associated data rejected");
        using (var other = new Aead(RandomNumberGenerator.GetBytes(32)))
            Check(!other.Open(SecureCrypto.ChanTcp, 7, aad, sealedBytes, out _), "wrong key rejected");
        Check(!a.Open(SecureCrypto.ChanTcp, 7, aad, sealedBytes.AsSpan(0, 10), out _), "truncated data rejected");
        Check(!a.Open(SecureCrypto.ChanTcp, 7, aad, ReadOnlySpan<byte>.Empty, out _), "empty data rejected");

        // UDP datagrams
        var payload = RandomNumberGenerator.GetBytes(14);
        var packet = SecureUdp.Build(a, SecureUdp.State, 42, payload);
        Check(packet.Length == SecureUdp.StateLength && packet[0] == SecureUdp.State, "state datagram is 35 bytes");
        Check(SecureUdp.TryOpen(a, packet, out var type, out var seq, out var got) && type == SecureUdp.State && seq == 42 && got.SequenceEqual(payload), "datagram round trip");
        int bad = 0;
        for (int i = 0; i < packet.Length; i++)
        {
            var t = (byte[])packet.Clone();
            t[i] ^= 0x01;
            if (SecureUdp.TryOpen(a, t, out _, out _, out _)) bad++;
        }
        Check(bad == 0, "flipping any datagram byte (type, sequence, ciphertext, tag) is rejected");
        Check(!SecureUdp.TryOpen(a, packet.AsSpan(0, 20), out _, out _, out _), "truncated datagram rejected");
        var ping = SecureUdp.Build(a, SecureUdp.Ping, 43, new byte[4]);
        Check(ping.Length == SecureUdp.PingLength, "ping datagram is 25 bytes");
        // Keystream reuse would leak plaintext XORs: two packets must never share a (key, nonce).
        var p1 = SecureUdp.Build(a, SecureUdp.State, 50, new byte[14]);
        var p2 = SecureUdp.Build(a, SecureUdp.State, 51, new byte[14]);
        Check(!p1.AsSpan(5, 14).SequenceEqual(p2.AsSpan(5, 14)), "consecutive sequence numbers use different keystreams");
        // Plaintext must not appear on the wire.
        var secretKeys = new byte[] { 0, 0x73, 0, 0, 0, 0, 0, 0 };
        var wire = SecureUdp.Build(a, SecureUdp.State, 60, secretKeys.Concat(new byte[6]).ToArray());
        Check(wire.AsSpan(5, 14).SequenceEqual(secretKeys.Concat(new byte[6]).ToArray()) == false, "key state is not visible in the datagram");
    }

    // --- an in-process server speaking the NodeMCU side of the protocol ---
    sealed class FakeServer : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Func<Stream, byte[], Task>? AfterHandshake;
        public string Token = "";
        public bool LieAboutProof;
        public bool RejectClient;
        public SessionKeys? Keys;
        public FakeServer() => listener.Start();
        public async Task RunOnce()
        {
            using var c = await listener.AcceptTcpClientAsync();
            var s = c.GetStream();
            string hello = await ReadLine(s);
            var cn = Hex(hello["HELLO2 ".Length..]);
            var sn = RandomNumberGenerator.GetBytes(16);
            var proof = SecureCrypto.ServerProof(LieAboutProof ? Token + "x" : Token, cn, sn);
            await Write(s, "CHAL " + SecureCrypto.Hex(sn) + " " + SecureCrypto.Hex(proof) + "\n");
            string line = await ReadLine(s);
            var want = SecureCrypto.ClientProof(Token, cn, sn);
            if (RejectClient || line != "PROOF " + SecureCrypto.Hex(want)) { await Write(s, "ERR\n"); return; }
            Keys = SecureCrypto.DeriveKeys(Token, cn, sn);
            if (AfterHandshake != null) await AfterHandshake(s, Keys.ServerToClient);
        }
        public async Task RunSilent(int ms)
        {
            using var c = await listener.AcceptTcpClientAsync();
            await Task.Delay(ms);
        }
        public void Dispose() => listener.Stop();
    }

    static async Task<string> ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (await s.ReadAsync(one) == 1 && one[0] != '\n') sb.Append((char)one[0]);
        return sb.ToString();
    }

    static Task Write(Stream s, string text) => s.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    static byte[] Frame(Aead a, ulong counter, string text)
    {
        var plain = Encoding.UTF8.GetBytes(text);
        var header = new byte[] { (byte)plain.Length, (byte)(plain.Length >> 8) };
        return header.Concat(a.Seal(SecureCrypto.ChanTcp, counter, header, plain)).ToArray();
    }

    static async Task<SecureLink> Connect(FakeServer server, string token)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        try { return await SecureLink.ConnectAsync(client.GetStream(), token, CancellationToken.None, 2000); }
        catch { client.Dispose(); throw; }
    }

    static async Task LinkAgainstFakeServer()
    {
        const string token = "fake-server-token-123456";
        // Normal session: handshake, encrypted OK, bidirectional lines, split writes.
        {
            using var server = new FakeServer { Token = token };
            string? received = null;
            server.AfterHandshake = async (s, key) =>
            {
                using var tx = new Aead(key);
                using var rx = new Aead(server.Keys!.ClientToServer);
                await s.WriteAsync(Frame(tx, 0, "OK"));
                // read one client frame
                var hdr = new byte[2]; await s.ReadExactlyAsync(hdr);
                var body = new byte[(hdr[0] | hdr[1] << 8) + 16]; await s.ReadExactlyAsync(body);
                if (rx.Open(SecureCrypto.ChanTcp, 0, hdr, body, out var plain)) received = Encoding.UTF8.GetString(plain);
                // answer in 1-byte pieces to prove the reader reassembles frames
                foreach (var b in Frame(tx, 1, "PONG")) { await s.WriteAsync(new[] { b }); await s.FlushAsync(); }
                await Task.Delay(100);
            };
            var run = server.RunOnce();
            using var link = await Connect(server, token);
            await link.WriteLineAsync("PING");
            string? reply = await link.ReadLineAsync();
            await run;
            Check(received == "PING", "server decrypts the client's command");
            Check(reply == "PONG", "client decrypts a reply delivered one byte at a time");
        }
        // The server proves it knows the token: an impostor is refused before the client reveals its own proof.
        {
            using var server = new FakeServer { Token = token, LieAboutProof = true };
            var run = server.RunOnce();
            bool refused = await Throws<IOException>(async () => { using var l = await Connect(server, token); });
            try { await run; } catch { }
            Check(refused, "client refuses a server that cannot prove it knows the token");
        }
        // Wrong client token: the client notices at the server proof (the server key differs).
        {
            using var server = new FakeServer { Token = token };
            var run = server.RunOnce();
            bool refused = await Throws<IOException>(async () => { using var l = await Connect(server, "a-completely-wrong-token-1"); });
            try { await run; } catch { }
            Check(refused, "wrong token on the client is refused");
        }
        // Server rejects the client proof (it replies ERR in the clear).
        {
            using var server = new FakeServer { Token = token, RejectClient = true };
            var run = server.RunOnce();
            bool refused = await Throws<IOException>(async () => { using var l = await Connect(server, token); });
            try { await run; } catch { }
            Check(refused, "a rejected login surfaces as an error");
        }
        // Tampered, replayed and reordered frames are fatal.
        foreach (var (name, mutate) in new (string, Func<byte[][], byte[][]>)[]
        {
            ("flipped ciphertext bit", f => { var c = (byte[])f[1].Clone(); c[3] ^= 0x10; return new[] { f[0], c }; }),
            ("flipped tag bit", f => { var c = (byte[])f[1].Clone(); c[^1] ^= 0x01; return new[] { f[0], c }; }),
            ("replayed frame", f => new[] { f[0], f[0] }),
            ("reordered frames", f => new[] { f[1], f[0] }),
            ("dropped first frame", f => new[] { f[1] }),
        })
        {
            using var server = new FakeServer { Token = token };
            server.AfterHandshake = async (s, key) =>
            {
                using var tx = new Aead(key);
                await s.WriteAsync(Frame(tx, 0, "OK"));
                var frames = new[] { Frame(tx, 1, "A,first"), Frame(tx, 2, "B,second") };
                foreach (var f in mutate(frames)) await s.WriteAsync(f);
                await Task.Delay(200);
            };
            var run = server.RunOnce();
            using var link = await Connect(server, token);
            bool rejected = await Throws<IOException>(async () => { for (int i = 0; i < 3; i++) await link.ReadLineAsync(); });
            await run;
            Check(rejected, $"client rejects {name}");
        }
        // An oversized length field cannot make the client allocate or wait.
        {
            using var server = new FakeServer { Token = token };
            server.AfterHandshake = async (s, key) =>
            {
                using var tx = new Aead(key);
                await s.WriteAsync(Frame(tx, 0, "OK"));
                await s.WriteAsync(new byte[] { 0xFF, 0xFF, 1, 2, 3 });
                await Task.Delay(200);
            };
            var run = server.RunOnce();
            using var link = await Connect(server, token);
            bool rejected = await Throws<IOException>(async () => await link.ReadLineAsync());
            await run;
            Check(rejected, "client rejects an oversized frame length");
        }
        // Random bytes after the handshake never decrypt.
        {
            using var server = new FakeServer { Token = token };
            server.AfterHandshake = async (s, key) =>
            {
                using var tx = new Aead(key);
                await s.WriteAsync(Frame(tx, 0, "OK"));
                var junk = RandomNumberGenerator.GetBytes(64); junk[0] = 20; junk[1] = 0;
                await s.WriteAsync(junk);
                await Task.Delay(200);
            };
            var run = server.RunOnce();
            using var link = await Connect(server, token);
            bool rejected = await Throws<IOException>(async () => await link.ReadLineAsync());
            await run;
            Check(rejected, "client rejects random bytes posing as a frame");
        }
        // A server that stays silent cannot hang the client.
        {
            using var server = new FakeServer { Token = token };
            var accept = Task.Run(async () => { try { await server.RunSilent(3000); } catch { } });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool timedOut = await Throws<IOException>(async () => { using var l = await Connect(server, token); });
            Check(timedOut && sw.ElapsedMilliseconds < 4000, $"handshake times out on a silent server ({sw.ElapsedMilliseconds} ms)");
        }
    }


    static void UsbParsing()
    {
        string[] Parse(params byte[][] chunks)
        {
            var p = new UsbReplyParser();
            foreach (var c in chunks) p.Feed(c, c.Length);
            var all = new List<string>();
            while (p.TryTake(out var l)) all.Add(l);
            return all.ToArray();
        }
        byte[] B(string s) => Encoding.Latin1.GetBytes(s);
        Check(Parse(B("#PONG\n")).SequenceEqual(new[] { "PONG" }), "a '#' line is a reply, without the '#'");
        Check(Parse(B("S,0,115,0,0,0,0,0\r\n" + "M,0,0,0,0\r\n" + "#PONG\r\n")).SequenceEqual(new[] { "PONG" }), "lines meant for the Pico are skipped; CRLF is stripped");
        Check(Parse(B("#PO"), B("NG\n#STAT,-5"), B("0,1,2,3\n")).SequenceEqual(new[] { "PONG", "STAT,-50,1,2,3" }), "replies split across reads are reassembled");
        Check(Parse(B("\xff\xfe junk \n#USB,3.1\n")).SequenceEqual(new[] { "USB,3.1" }), "boot noise before a reply is ignored");
        Check(Parse(B(new string('x', 5000) + "\n#OK\n")).SequenceEqual(new[] { "OK" }), "an over-long line is dropped without losing the next reply");
        Check(Parse(B("#\n#A\n")).SequenceEqual(new[] { "A" }), "an empty reply is ignored");
        var diag = "DIAG,fw=3.1," + string.Join(",", Enumerable.Range(0, 40).Select(i => "k" + i + "=" + i * 1000));
        Check(Parse(B("#" + diag + "\n")).SequenceEqual(new[] { diag }) && diag.Length < UsbReplyParser.MaxLine, $"a full DIAG reply ({diag.Length} chars) fits the line limit");
    }

    // ===================================== real hardware =====================================
    static class Device
    {
        sealed record Cfg(string Host, int Port, string Token);

        static Cfg Load()
        {
            string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WifiKeyboardBridge", "appsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            return new Cfg(r.GetProperty("Host").GetString()!, r.GetProperty("Port").GetInt32(), r.GetProperty("Token").GetString()!);
        }

        static async Task<SecureLink> Open(Cfg c, string? token = null)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                await tcp.ConnectAsync(c.Host, c.Port);
                return await SecureLink.ConnectAsync(tcp.GetStream(), token ?? c.Token, CancellationToken.None, 3000);
            }
            catch { tcp.Dispose(); throw; }
        }

        static async Task<Dictionary<string, string>> Diag(SecureLink link)
        {
            await link.WriteLineAsync("DIAG");
            for (int i = 0; i < 20; i++)
            {
                var line = await link.ReadLineAsync();
                if (line != null && line.StartsWith("DIAG,"))
                    return line[5..].Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            }
            throw new IOException("no DIAG reply");
        }

        static long N(Dictionary<string, string> d, string k) => long.Parse(d[k]);

        static async Task<string?> Ask(SecureLink link, string command, string prefix)
        {
            await link.WriteLineAsync(command);
            for (int i = 0; i < 20; i++)
            {
                var line = await link.ReadLineAsync();
                if (line != null && line.StartsWith(prefix)) return line;
            }
            return null;
        }

        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        struct POINT { public int X, Y; }
        static (int, int) Pos() { GetCursorPos(out var p); return (p.X, p.Y); }
        static bool F24Down() => (GetAsyncKeyState(0x87) & 0x8000) != 0;
        static double? WaitFor(Func<bool> cond, double limitMs = 1000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < limitMs) if (cond()) return sw.Elapsed.TotalMilliseconds;
            return null;
        }

        static async Task WaitBackoff() => await Task.Delay(9000);  // lets the NodeMCU's login back-off expire

        public static async Task Run()
        {
            var cfg = Load();
            Console.WriteLine($"  target {cfg.Host}:{cfg.Port}");

            // 1. Cross-implementation handshake + primitives on the device.
            {
                using var link = await Open(cfg);
                Report(true, "handshake with the NodeMCU (C# <-> C++ HMAC, HKDF and ChaCha20-Poly1305 agree)");
                var self = await Ask(link, "SELFTEST", "SELFTEST");
                Report(self == "SELFTEST,OK", "firmware known-answer tests (RFC 4231 / 5869 / 8439)", self ?? "no reply");
                var d = await Diag(link);
                Report(d.GetValueOrDefault("sec") == "3", "firmware reports protocol v3 (fw " + d.GetValueOrDefault("fw") + ")");
                var pong = await Ask(link, "PING", "PONG");
                Report(pong == "PONG", "encrypted PING/PONG");
                for (int i = 0; i < 200; i++) await link.WriteLineAsync(i % 2 == 0 ? "PING" : "STAT");
                int replies = 0;
                using var cts = new CancellationTokenSource(3000);
                try { while (replies < 200) { if (await link.ReadLineAsync(cts.Token) != null) replies++; } } catch (OperationCanceledException) { }
                Report(replies == 200, $"200 back-to-back encrypted commands answered in order ({replies}/200)");
            }

            // 2. Wrong credentials.
            await WaitBackoff();
            {
                bool refused = await Throws<IOException>(async () => { using var l = await Open(cfg, cfg.Token + "x"); });
                Report(refused, "wrong token refused (client detects the NodeMCU's proof does not match)");
            }
            {   // Wrong client proof: speak the protocol by hand so the server sees a bad PROOF.
                var tcp = new TcpClient(); await tcp.ConnectAsync(cfg.Host, cfg.Port);
                var s = tcp.GetStream();
                await Write(s, "HELLO2 " + SecureCrypto.Hex(RandomNumberGenerator.GetBytes(16)) + "\n");
                string chal = await ReadLine(s);
                await Write(s, "PROOF " + new string('0', 64) + "\n");
                var buf = new byte[16]; int n = 0;
                try { n = await s.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                string answer = Encoding.ASCII.GetString(buf, 0, n).Trim();
                Report(chal.StartsWith("CHAL ") && (answer == "ERR" || n == 0), "server rejects a bad PROOF without giving anything away", answer);
            }
            {   // Back-off: an immediate retry is dropped, a later one works.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool droppedFast = await Throws<IOException>(async () => { using var l = await Open(cfg); });
                Report(droppedFast, "login attempts right after a failure are dropped (back-off)", sw.ElapsedMilliseconds + " ms");
                await Task.Delay(1500);
                using var ok = await Open(cfg);
                Report(true, "correct login works again after the back-off");
            }
            {   // The old plaintext login no longer works and never sees the token.
                await WaitBackoff();
                var tcp = new TcpClient(); await tcp.ConnectAsync(cfg.Host, cfg.Port);
                var s = tcp.GetStream();
                await Write(s, "AUTH not-the-real-token-but-same-shape\n");
                var buf = new byte[16]; int n = 0;
                try { n = await s.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                string answer = Encoding.ASCII.GetString(buf, 0, n).Trim();
                Report(answer != "OK", "legacy plaintext AUTH is no longer accepted", answer);
                await WaitBackoff();
            }

            // 3. Tampering on the live TCP stream ends the session.
            foreach (var attack in new[] { "flipped bit", "replayed frame", "wrong key", "plaintext command" })
            {
                long before;
                using (var probe = await Open(cfg)) before = N(await Diag(probe), "badtag");
                var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(cfg.Host, cfg.Port);
                var stream = tcp.GetStream();
                var link = await SecureLink.ConnectAsync(stream, cfg.Token, CancellationToken.None, 3000);
                using var tx = new Aead(link.Keys.ClientToServer);
                using var wrong = new Aead(RandomNumberGenerator.GetBytes(32));
                // counter 0 is unused by SecureLink only if we never wrote; use the raw stream with our own counter
                byte[] f0 = Frame(attack == "wrong key" ? wrong : tx, 0, "PING");
                switch (attack)
                {
                    case "flipped bit": f0[4] ^= 0x04; await stream.WriteAsync(f0); break;
                    case "replayed frame": await stream.WriteAsync(f0); await stream.WriteAsync(f0); break;
                    case "wrong key": await stream.WriteAsync(f0); break;
                    case "plaintext command": await stream.WriteAsync(Encoding.ASCII.GetBytes("S,0,0,0,0,0,0,0\n")); break;
                }
                await Task.Delay(500);
                using var after = await Open(cfg);
                var d = await Diag(after);
                string reason = d["lastend"];
                bool ended = attack == "replayed frame" ? reason == "bad-tag" : reason is "bad-tag" or "frame-too-long";
                Report(ended, $"live session ends on {attack}", "lastend=" + reason);
                tcp.Dispose();
                await Task.Delay(300);
            }

            // 4. UDP hardening.
            {
                using var link = await Open(cfg);
                var d0 = await Diag(link);
                using var stray = new UdpClient();
                // before UDP? is answered the NodeMCU must ignore datagrams entirely
                var junk = RandomNumberGenerator.GetBytes(35); junk[0] = SecureUdp.State;
                stray.Send(junk, junk.Length, cfg.Host, 7778);
                await Task.Delay(150);
                var d1 = await Diag(link);
                Report(N(d1, "udpbad") > N(d0, "udpbad") && N(d1, "udp") == N(d0, "udp"), "datagrams before UDP? is answered are counted as bad and ignored");

                var reply = await Ask(link, "UDP?", "UDP,");
                int port = int.Parse(reply!.Split(',')[1]);
                using var send = new Aead(link.Keys.ClientToServer);
                using var recv = new Aead(link.Keys.ServerToClient);
                using var udp = new UdpClient(); udp.Connect(cfg.Host, port);
                uint seq = 0;
                byte[] State(byte key) { var p = new byte[14]; p[1] = key; return SecureUdp.Build(send, SecureUdp.State, ++seq, p); }

                long Count(string k, Dictionary<string, string> d) => N(d, k);
                var a = await Diag(link);
                var good = State(0);
                udp.Send(good, good.Length);
                await Task.Delay(120);
                var b = await Diag(link);
                Report(Count("udp", b) == Count("udp", a) + 1, "authentic datagram accepted");

                udp.Send(good, good.Length);   // exact replay
                await Task.Delay(120);
                var c = await Diag(link);
                Report(Count("udpstale", c) == Count("udpstale", b) + 1 && Count("udp", c) == Count("udp", b), "replayed datagram dropped");

                var forged = State(0); forged[8] ^= 0x01;   // flipped ciphertext bit
                udp.Send(forged, forged.Length);
                var forgedHeader = State(0); forgedHeader[1] ^= 0x01;   // altered sequence number
                udp.Send(forgedHeader, forgedHeader.Length);
                using (var attacker = new Aead(RandomNumberGenerator.GetBytes(32)))
                {
                    var wrongKey = SecureUdp.Build(attacker, SecureUdp.State, ++seq, new byte[14]);
                    udp.Send(wrongKey, wrongKey.Length);
                }
                var hugeSeq = new byte[35]; hugeSeq[0] = SecureUdp.State; BitConverter.TryWriteBytes(hugeSeq.AsSpan(1), 0x7FFFFFF0u);
                udp.Send(hugeSeq, hugeSeq.Length);
                await Task.Delay(200);
                var e = await Diag(link);
                Report(Count("udpbad", e) == Count("udpbad", c) + 4 && Count("udp", e) == Count("udp", c), "4 forged datagrams rejected (bit flip, altered seq, wrong key, forged huge seq)");

                var next = State(0);   // a forged huge sequence must not have locked the real client out
                udp.Send(next, next.Length);
                await Task.Delay(120);
                var f = await Diag(link);
                Report(Count("udp", f) == Count("udp", e) + 1, "real client keeps working after forged datagrams (no sequence lock-out)");

                var ping = SecureUdp.Build(send, SecureUdp.Ping, ++seq, BitConverter.GetBytes(0xA1B2C3D4u));
                udp.Send(ping, ping.Length);
                var echo = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
                bool echoOk = SecureUdp.TryOpen(recv, echo.Buffer, out var t, out var serverSeq, out var pl) && t == SecureUdp.Pong && BitConverter.ToUInt32(pl) == 0xA1B2C3D4u;
                Report(echoOk, "encrypted ping echoed and authenticated");
                var rawBytes = echo.Buffer;
                Report(!rawBytes.AsSpan(5, 4).SequenceEqual(BitConverter.GetBytes(0xA1B2C3D4u)), "echo payload is not plaintext on the wire");
                await link.WriteLineAsync("S,0,0,0,0,0,0,0");
            }

            // 5. Nothing sensitive crosses the network in the clear (TCP is captured by a relay).
            await Task.Delay(500);
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                int relayPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                var captured = new MemoryStream();
                var relay = Task.Run(async () =>
                {
                    using var inbound = await listener.AcceptTcpClientAsync();
                    using var outbound = new TcpClient(); await outbound.ConnectAsync(cfg.Host, cfg.Port);
                    async Task Pump(NetworkStream from, NetworkStream to)
                    {
                        var buf = new byte[512];
                        try { int n; while ((n = await from.ReadAsync(buf)) > 0) { lock (captured) captured.Write(buf, 0, n); await to.WriteAsync(buf.AsMemory(0, n)); } } catch { }
                    }
                    await Task.WhenAny(Pump(inbound.GetStream(), outbound.GetStream()), Pump(outbound.GetStream(), inbound.GetStream()));
                });
                var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(IPAddress.Loopback, relayPort);
                using (var link = await SecureLink.ConnectAsync(tcp.GetStream(), cfg.Token, CancellationToken.None, 3000))
                {
                    for (int i = 0; i < 20; i++) await link.WriteLineAsync("S,0,115,0,0,0,0,0");
                    await link.WriteLineAsync("PICO?,7"); await link.WriteLineAsync("DIAG"); await link.WriteLineAsync("PING");
                    await Task.Delay(500);
                    await link.WriteLineAsync("S,0,0,0,0,0,0,0");
                }
                tcp.Dispose();
                await Task.Delay(300);
                byte[] wire; lock (captured) wire = captured.ToArray();
                string text = Encoding.Latin1.GetString(wire);
                Report(wire.Length > 500, $"captured {wire.Length} bytes of the session");
                Report(!text.Contains(cfg.Token), "the token never appears on the wire");
                foreach (var word in new[] { "S,0,115", "PING", "PONG", "DIAG", "STAT", "fw=", "rssi", "PICO", "AUTH", "OK\n" })
                    if (text.Contains(word)) Report(false, $"'{word}' appears in the clear on the wire");
                Report(!new[] { "S,0,115", "PING", "PONG", "DIAG", "STAT", "fw=", "rssi", "PICO", "AUTH" }.Any(w => text.Contains(w)), "no command, reply or diagnostic text is readable on the wire");
                // The only readable parts are the two handshake lines.
                int newlines = text.Count(ch => ch == '\n');
                Report(text.Contains("HELLO2 ") && text.Contains("CHAL ") && text.Contains("PROOF "), "only the handshake nonces and proofs are readable");
                _ = relay; _ = newlines;
            }

            // 6. End to end through the Pico: keys and mouse still arrive, and how fast.
            await Task.Delay(1200);
            await EndToEnd(cfg);

            // 7. The same NodeMCU over its USB serial port.
            await Task.Delay(1500);
            await UsbTests(cfg);
        }

        static async Task UsbTests(Cfg cfg)
        {
            Console.WriteLine("== USB serial (the NodeMCU's own USB port) ==");
            UsbLink usb;
            try { usb = await UsbLink.ConnectAsync("auto", CancellationToken.None, 3000); }
            catch (Exception ex) { Report(false, "NodeMCU answers on its USB serial port", ex.Message); return; }
            using var usbOwner = usb;
            Report(usb.Firmware.StartsWith("3."), $"NodeMCU answers on {usb.PortName} (firmware {usb.Firmware})");

            // A background pump owns the read side; commands wait for their reply there.
            var replies = new System.Collections.Concurrent.ConcurrentQueue<string>();
            _ = Task.Run(async () => { try { while (true) { var l = await usb.ReadLineAsync(CancellationToken.None); if (l == null) break; replies.Enqueue(l); } } catch { } });
            async Task<string?> Ask(string command, string prefix, int waitMs = 1500)
            {
                while (replies.TryDequeue(out _)) { }
                await usb.WriteLineAsync(command);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < waitMs)
                {
                    while (replies.TryDequeue(out var l)) if (l.StartsWith(prefix)) return l;
                    await Task.Delay(2);
                }
                return null;
            }
            async Task<Dictionary<string, string>> UsbDiag()
            {
                var line = await Ask("DIAG", "DIAG,");
                if (line == null) throw new IOException("no DIAG reply over USB");
                return line[5..].Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            }
            long PicoBad(Dictionary<string, string> d) => long.Parse(d["pico"].Split('/')[2]);

            Report(await Ask("PING", "PONG") == "PONG", "PING answered over USB");
            Report(await Ask("SELFTEST", "SELFTEST,") == "SELFTEST,OK", "firmware self-test over USB");
            var d0 = await UsbDiag();
            Report(N(d0, "usb") > 0 && d0["fw"] == usb.Firmware, "DIAG over USB reports the USB counters", $"usb={d0.GetValueOrDefault("usb")}");

            // Garbage and an over-long line must not hurt the link.
            await usb.WriteLineAsync("");
            await usb.WriteLineAsync("hello\nnot a command\n" + new string('A', 300));
            Report(await Ask("PING", "PONG") == "PONG", "link survives garbage and an over-long line");
            var d1 = await UsbDiag();
            Report(N(d1, "usbbad") > N(d0, "usbbad"), "the over-long line is counted, not forwarded", $"usbbad {d0["usbbad"]} -> {d1["usbbad"]}");

            // Replies share the wire with the Pico; the Pico must ignore every one of them.
            long badBefore = PicoBad(d1);
            for (int i = 0; i < 20; i++) { await Ask("STAT", "STAT,", 500); await Ask("PICO?," + (100 + i), "PICO,", 800); }
            await UsbDiag(); await Task.Delay(600);
            var d2 = await UsbDiag();
            Report(PicoBad(d2) == badBefore, "the Pico ignores the NodeMCU's replies (no bad frames)", $"bad {badBefore} -> {PicoBad(d2)}");

            // Keys and mouse through the Pico.
            var keyMs = new List<double>(); var moveMs = new List<double>(); int keyFail = 0, moveFail = 0;
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(150);
                await usb.WriteLineAsync("S,0,115,0,0,0,0,0");
                var t = WaitFor(F24Down);
                if (t is { } k) keyMs.Add(k); else keyFail++;
                await usb.WriteLineAsync("S,0,0,0,0,0,0,0");
                WaitFor(() => !F24Down());
                await Task.Delay(100);
                var p0 = Pos();
                await usb.WriteLineAsync("M,0,20,0,0");
                var m = WaitFor(() => Pos() != p0);
                if (m is { } mm) moveMs.Add(mm); else moveFail++;
                await Task.Delay(100);
                await usb.WriteLineAsync("M,0,-20,0,0");
                await Task.Delay(100);
            }
            Report(keyFail == 0 && keyMs.Count == 30, $"USB key press reaches Windows through the Pico ({keyMs.Count}/30)");
            Report(moveFail == 0 && moveMs.Count == 30, $"USB mouse move reaches Windows through the Pico ({moveMs.Count}/30)");
            if (keyMs.Count > 0) { keyMs.Sort(); Console.WriteLine($"        key latency  median {keyMs[keyMs.Count / 2]:F1} ms, max {keyMs[^1]:F1} ms"); }
            if (moveMs.Count > 0) { moveMs.Sort(); Console.WriteLine($"        move latency median {moveMs[moveMs.Count / 2]:F1} ms, max {moveMs[^1]:F1} ms"); }
            Report(keyMs.Count == 0 || keyMs[keyMs.Count / 2] < 40, "median key latency under 40 ms");

            // Dead-man release: a key held by the USB client is released when the client goes silent.
            await usb.WriteLineAsync("S,0,115,0,0,0,0,0");
            bool down = WaitFor(F24Down) != null;
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            bool released = WaitFor(() => !F24Down(), 4000) != null;
            Report(down && released && sw2.ElapsedMilliseconds is > 1000 and < 3500, $"a held key is released when USB goes silent ({sw2.ElapsedMilliseconds} ms)");
            Report(!F24Down(), "no key left pressed");

            // USB has priority over Wi-Fi while it is active, so only one source writes to the Pico.
            await usb.WriteLineAsync("S,0,0,0,0,0,0,0");
            var before = await UsbDiag();
            using (var wifi = await Open(cfg))
            {
                await wifi.WriteLineAsync("S,0,115,0,0,0,0,0");
                bool leaked = WaitFor(F24Down, 400) != null;
                await wifi.WriteLineAsync("S,0,0,0,0,0,0,0");
                var after = await UsbDiag();
                Report(!leaked && N(after, "usbdrop") > N(before, "usbdrop"), "Wi-Fi input is ignored while USB is active", $"usbdrop {before["usbdrop"]} -> {after["usbdrop"]}");
                await Task.Delay(1800);  // USB goes quiet; Wi-Fi may drive the Pico again
                await wifi.WriteLineAsync("S,0,115,0,0,0,0,0");
                bool wifiBack = WaitFor(F24Down, 500) != null;
                await wifi.WriteLineAsync("S,0,0,0,0,0,0,0");
                WaitFor(() => !F24Down());
                Report(wifiBack, "Wi-Fi input works again once USB is quiet");
            }
            await usb.WriteLineAsync("S,0,0,0,0,0,0,0");
            await usb.WriteLineAsync("M,0,0,0,0");
            Report(!F24Down(), "no key left pressed after the priority test");
        }

        static async Task EndToEnd(Cfg cfg)
        {
            using var link = await Open(cfg);
            var reply = await Ask(link, "UDP?", "UDP,");
            // From here a background task owns the read side; another keeps the NodeMCU session alive.
            var reader = Task.Run(async () => { try { while (await link.ReadLineAsync() != null) { } } catch { } });
            var keepalive = Task.Run(async () => { try { while (true) { await Task.Delay(400); await link.WriteLineAsync("PING"); } } catch { } });
            int port = int.Parse(reply!.Split(',')[1]);
            using var send = new Aead(link.Keys.ClientToServer);
            using var udp = new UdpClient(); udp.Connect(cfg.Host, port);
            uint seq = 0; short cx = 0;
            void Udp(byte key, short moveX) { cx = moveX; var p = new byte[14]; p[1] = key; BitConverter.TryWriteBytes(p.AsSpan(8), cx); var pk = SecureUdp.Build(send, SecureUdp.State, ++seq, p); udp.Send(pk, pk.Length); }

            var keyMs = new List<double>(); var moveMs = new List<double>(); int keyFail = 0, moveFail = 0;
            short pos = 0;
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(150);
                Udp(0x73, pos);
                var t = WaitFor(F24Down);
                if (t is { } k) keyMs.Add(k); else keyFail++;
                for (int r = 0; r < 4; r++) { Udp(0, pos); await Task.Delay(8); }
                WaitFor(() => !F24Down());
                await Task.Delay(100);
                var p0 = Pos();
                pos += 20; Udp(0, pos);
                var m = WaitFor(() => Pos() != p0);
                if (m is { } mm) moveMs.Add(mm); else moveFail++;
                await Task.Delay(150);
                pos -= 20; Udp(0, pos); Udp(0, pos);
                await Task.Delay(150);
            }
            Report(keyFail == 0 && keyMs.Count == 30, $"encrypted UDP key press reaches Windows through the Pico ({keyMs.Count}/30)");
            Report(moveFail == 0 && moveMs.Count == 30, $"encrypted UDP mouse move reaches Windows through the Pico ({moveMs.Count}/30)");
            if (keyMs.Count > 0) { keyMs.Sort(); Console.WriteLine($"        key latency  median {keyMs[keyMs.Count / 2]:F1} ms, max {keyMs[^1]:F1} ms"); }
            if (moveMs.Count > 0) { moveMs.Sort(); Console.WriteLine($"        move latency median {moveMs[moveMs.Count / 2]:F1} ms, max {moveMs[^1]:F1} ms"); }
            Report(keyMs.Count == 0 || keyMs[keyMs.Count / 2] < 40, "median key latency under 40 ms");
            Report(!F24Down(), "no key left pressed");

            // TCP path through the Pico as well.
            int tcpKey = 0;
            for (int i = 0; i < 10; i++)
            {
                await link.WriteLineAsync("S,0,115,0,0,0,0,0");
                if (WaitFor(F24Down) != null) tcpKey++;
                await link.WriteLineAsync("S,0,0,0,0,0,0,0");
                WaitFor(() => !F24Down());
                await Task.Delay(100);
            }
            Report(tcpKey == 10, $"encrypted TCP key press reaches Windows through the Pico ({tcpKey}/10)");
            await link.WriteLineAsync("S,0,0,0,0,0,0,0");
            await link.WriteLineAsync("M,0,0,0,0");
            _ = reader; _ = keepalive;
        }
    }
}
