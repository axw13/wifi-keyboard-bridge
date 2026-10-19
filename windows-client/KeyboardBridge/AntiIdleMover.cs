namespace KeyboardBridge;

// Settings mirror. The NodeMCU owns the persistent remote anti-idle scheduler.
sealed class AntiIdleMover
{
    public bool Enabled { get; set; }
    public string Status(bool keyboardRemote, bool mouseRemote, bool connected)
    {
        if (!Enabled) return "Remote anti-idle: OFF (NodeMCU setting)";
        if (!connected) return "Remote anti-idle: enabled on NodeMCU; Windows disconnected";
        if (keyboardRemote || mouseRemote) return "Remote anti-idle: paused while input is REMOTE";
        return "Remote anti-idle: NodeMCU active every 15 s, including when Windows is off";
    }
}
