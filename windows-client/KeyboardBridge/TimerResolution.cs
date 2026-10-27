using System.Runtime.InteropServices;

namespace KeyboardBridge;

// Raises the Windows timer resolution to 1 ms while connected. The default ~15.6 ms
// tick made every Task.Delay/wait in the transport loop round up to 16 or 32 ms.
static class TimerResolution
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    const int ProcessPowerThrottling = 4;
    const uint ThrottleExecutionSpeed = 0x1;
    const uint ThrottleIgnoreTimerResolution = 0x4;
    static int active;
    static bool throttlingConfigured;
    public static bool Active => Volatile.Read(ref active) > 0;

    public static bool Begin()
    {
        try
        {
            if (!throttlingConfigured)
            {
                throttlingConfigured = true;
                // Windows 11 ignores timer requests from processes without a visible window
                // (this is a tray app) and may run them in EcoQoS. Opt out of both.
                var state = new PowerThrottlingState
                {
                    Version = 1,
                    ControlMask = ThrottleExecutionSpeed | ThrottleIgnoreTimerResolution,
                    StateMask = 0,
                };
                if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>()))
                    DiagnosticLog.Write("Power throttling opt-out unavailable (Windows error " + Marshal.GetLastWin32Error() + ").");
            }
            if (timeBeginPeriod(1) != 0)
            {
                DiagnosticLog.Warn("timeBeginPeriod(1) failed; mouse pacing limited to the default timer.");
                return false;
            }
            Interlocked.Increment(ref active);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Timer resolution unavailable: " + ex.GetType().Name);
            return false;
        }
    }

    public static void End()
    {
        try { timeEndPeriod(1); Interlocked.Decrement(ref active); }
        catch { /* Best effort. */ }
    }
}
