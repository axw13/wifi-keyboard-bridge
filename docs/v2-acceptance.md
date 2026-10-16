# V2 hardware acceptance checklist

Run against the packaged Windows executable and matching firmware before tagging a release.
The software cannot simulate physical USB HID host behavior in CI.

## First launch and packaging
- Extract ZIP to permanent writable folder; configure `appsettings.json`.
- Confirm app starts without installing the .NET runtime.
- Launch a second copy: it must refuse to start. The first tray icon stays available.
- Enable Start with Windows, log off/on, check only one instance starts; disable it afterward.
- Check About version, GitHub links, and local log path.

## Normal input
- Keyboard only REMOTE: type characters, Shift/Ctrl/Alt, press and release modifiers. Switch back LOCAL.
- Mouse only REMOTE: continuous motion at manual 90 Hz, click/hold/release left/right/middle, wheel. Return LOCAL.
- Check linked switching. Confirm Ctrl+Alt+F10 switches both devices LOCAL from each state.
- Observe tray: disconnected red, connected local green, remote blue.
- Compare mouse smoothness to the baseline before and after installing V2.

## Faults and recovery (do not skip)
- While holding a remote modifier, unplug NodeMCU power or disconnect Wi-Fi; release physical key; verify the remote HID state clears after watchdog timeout, and local typing resumes.
- While holding a mouse button remotely, disconnect NodeMCU; verify the remote PC does not retain a stuck button and mouse returns LOCAL on main PC.
- Repeat while resetting Pico / unplugging its USB; verify it comes back without spurious held input.
- Kill the Windows app while REMOTE and verify the Pico/ESP8266 watchdog eventually releases both keyboard and mouse.
- Restore connections and check automatic reconnection; do not automatically switch input mode back to REMOTE.
- Run the 10-second benchmark and verify Pico UART diagnostic ACK timeouts remain zero or investigate.
- Review `%LOCALAPPDATA%\WifiKeyboardBridge\bridge.log` for disconnection/reconnect messages and ensure the shared token is never present.

## Ship criteria
- CI Windows build and ZIP packaging successful.
- Full manual recovery and no-stuck-input acceptance pass.
- README/wiring instructions correct for Pico GP0 TX → NodeMCU D5/GPIO14 RX.
- Confirm only example config files are included in the public ZIP, no secrets.
- Tag `v0.9.0-rc1` only after user approves hardware validation; the release workflow publishes a pre-release ZIP.
