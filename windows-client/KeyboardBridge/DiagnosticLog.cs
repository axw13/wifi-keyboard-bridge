using System.Collections.Concurrent;
using System.Text;

namespace KeyboardBridge;

// Local rolling diagnostic log; intentionally never writes the shared token.
// Writes are queued and flushed on a background thread so logging never adds
// file I/O latency to the keyboard hook or the transport loop.
static class DiagnosticLog
{
    const long MaxBytes = 2 * 1024 * 1024;
    const int Archives = 3;  // bridge.1.log (newest) .. bridge.3.log (oldest)
    static readonly string directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WifiKeyboardBridge");
    static readonly string path = Path.Combine(directory, "bridge.log");
    static readonly BlockingCollection<string> pending = new(4096);
    static readonly ManualResetEventSlim idle = new(true);
    static long dropped;
    public static string LogPath => path;
    public static string LogDirectory => directory;

    static DiagnosticLog()
    {
        var worker = new Thread(WriteLoop) { IsBackground = true, Name = "DiagnosticLog", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    public static void Write(string message) => Enqueue("INFO", message);
    public static void Warn(string message) => Enqueue("WARN", message);
    public static void Error(string message) => Enqueue("ERROR", message);

    static void Enqueue(string level, string message)
    {
        string entry = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {level,-5} {message}";
        idle.Reset();
        if (!pending.TryAdd(entry)) Interlocked.Increment(ref dropped);
    }

    // Waits briefly for queued entries to reach disk (used on exit and before export).
    public static void Flush(int timeoutMs = 1000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (pending.Count > 0 && Environment.TickCount64 < deadline) Thread.Sleep(10);
        idle.Wait(Math.Max(0, (int)(deadline - Environment.TickCount64)));
    }

    // Last lines of the current log, for diagnostic exports.
    public static string Tail(int lines)
    {
        Flush();
        try
        {
            if (!File.Exists(path)) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var tail = new Queue<string>(lines);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == lines) tail.Dequeue();
                tail.Enqueue(line);
            }
            return string.Join(Environment.NewLine, tail);
        }
        catch (Exception ex) { return "Unable to read log: " + ex.Message; }
    }

    static void WriteLoop()
    {
        var batch = new StringBuilder();
        while (true)
        {
            string entry = pending.Take();
            batch.Clear().AppendLine(entry);
            while (batch.Length < 64 * 1024 && pending.TryTake(out var more)) batch.AppendLine(more);
            long lost = Interlocked.Exchange(ref dropped, 0);
            if (lost > 0) batch.AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] WARN  {lost} log entries dropped (queue full)");
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) Rotate();
                File.AppendAllText(path, batch.ToString(), Encoding.UTF8);
            }
            catch { /* Logging must never disable input recovery. */ }
            if (pending.Count == 0) idle.Set();
        }
    }

    static void Rotate()
    {
        for (int i = Archives - 1; i >= 1; i--)
        {
            string from = Path.Combine(directory, $"bridge.{i}.log");
            if (File.Exists(from)) File.Move(from, Path.Combine(directory, $"bridge.{i + 1}.log"), true);
        }
        File.Move(path, Path.Combine(directory, "bridge.1.log"), true);
    }
}
