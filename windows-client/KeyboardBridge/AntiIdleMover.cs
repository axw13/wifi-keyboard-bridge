namespace KeyboardBridge;

// Sends low-frequency movement to the *remote USB HID device* while both
// inputs are LOCAL; local Windows activity must not inhibit the schedule.
sealed class AntiIdleMover
{
    const long IntervalMs = 15_000;
    bool positive = true;
    long lastAttempt;
    long sent;
    public bool Enabled { get; set; }
    public long SentCount => sent;

    public string Status(bool keyboardRemote, bool mouseRemote, bool connected)
    {
        if (!Enabled) return "Remote anti-idle: OFF";
        if (!connected) return "Remote anti-idle: paused (disconnected)";
        if (keyboardRemote || mouseRemote) return "Remote anti-idle: paused (REMOTE input)";
        return $"Remote anti-idle: active | movements queued: {sent} | interval: 15 s";
    }

    public void Tick(bool keyboardRemote, bool mouseRemote, bool connected, Func<int, bool> sendRemoteMovement)
    {
        if (!Enabled || keyboardRemote || mouseRemote || !connected)
        {
            lastAttempt = 0; // restart cadence after REMOTE or reconnection
            return;
        }

        long now = Environment.TickCount64;
        if (lastAttempt != 0 && now - lastAttempt < IntervalMs) return;
        // Deliberately send distinct movement reports through the USB HID path.
        // Alternating a few pixels prevents cumulative drift and is more likely
        // to be registered by the remote OS than a sub-pixel adjustment.
        if (sendRemoteMovement(positive ? 4 : -4))
        {
            positive = !positive;
            sent++;
        }
        lastAttempt = now;
    }
}
