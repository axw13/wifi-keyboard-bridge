Wi-Fi Keyboard Bridge 0.9.0 — V2 preview

Portable Windows x64 build. No separate .NET runtime required.

1. Extract all files to a permanent folder where you have write access.
2. Copy appsettings.example.json to appsettings.json beside KeyboardBridge.exe.
3. Set NodeMCU host/IP, port 7777 and the same shared token as ESP8266 secrets.h.
4. Flash the matching included firmware onto NodeMCU and copy Pico code.py onto CIRCUITPY.
5. Wire according to wiring.md; power down before changing GPIO wires.
6. Start KeyboardBridge.exe; check the system tray.

Keyboard toggle: Ctrl+Alt+F12 (default)
Mouse toggle: Ctrl+Alt+F11 (default)
Emergency LOCAL: Ctrl+Alt+F10 (always reserved)

System tray: red = disconnected; green = connected/local; blue = one or both inputs REMOTE.
Settings includes Connection, Input, Performance, Diagnostics and About tabs.
Diagnostic log: %LOCALAPPDATA%\WifiKeyboardBridge\bridge.log

When the application is running, a second instance will be rejected.
Use Settings > Connection > Start with Windows only after putting the executable
in its permanent destination. To stop startup, uncheck that option.

This remains a PREVIEW. Validate held mouse buttons/modifier keys and disconnect
recovery on your own hardware. Keep existing known-working firmware as a backup.
Traffic is encrypted and authenticated (firmware 3.0 + matching client); still do not expose ports 7777/7778.

Release procedure: first verify the GitHub Actions Windows build, then test the
downloaded artifact on Windows. Only after acceptance tag an actual release.
Do not include secrets.h or appsettings.json in public release files.
