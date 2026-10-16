# Wi-Fi Keyboard Bridge

Experimental keyboard and mouse sharing: Windows 11 → Wi-Fi → ESP8266 NodeMCU V3 → UART → Raspberry Pi Pico RP2040 → USB HID → second computer. No software is installed on the receiving PC.

**Status:** The prior fixed-text transport was verified on hardware. This new live-keyboard prototype has **not** yet been compiled or verified end-to-end. Test with Notepad and keep a physical keyboard available.

## Mouse support (experimental)

The Windows tray client supports **independent mouse forwarding** (`Ctrl+Alt+F11` by default) and keyboard forwarding (`Ctrl+Alt+F12` by default). In **Settings & Connection Statistics**, both shortcuts can be changed independently. Turn on **Switch keyboard + mouse together** to toggle both with the keyboard shortcut; the mouse shortcut is inactive in linked mode. Mouse forwarding defaults **off** at launch. Keyboard-only behavior remains unchanged until mouse mode is enabled.

Mouse reports travel as `M,<buttons>,<relativeX>,<relativeY>,<wheel>` and are handled by the Pico default CircuitPython mouse USB HID device. This requires **flashing updated ESP8266 firmware AND copying updated Pico code.py**. Mouse movement is aggregated and rate-limited with an **adjustable 30/60/90/125 Hz target rate** with bounded deltas, preventing unbounded TCP/UART report queues. Mouse sensitivity is adjustable from **25% to 300%** in the Settings panel and saved locally. Start with 100% at 60 Hz, then try 90 or 125 Hz if the NodeMCU RTT stays stable. Button and wheel events bypass movement coalescing. The Windows client now reads **relative mouse movement from Windows Raw Input**, independently of the locally suppressed pointer position; the low-level mouse hook handles click/wheel suppression. This corrects cursor movement snapping back when local pointer events are blocked.  high-DPI, raw-input games, cursor acceleration, multi-monitor edges, and relative movement near display borders may not behave like a true KVM. Test movement, click, scroll and switching in Notepad first and retain a physical mouse on the second computer. The remote mouse is released on disconnect or when switching it off.

## Hardware

- NodeMCU **TX/GPIO1** → Pico **GP1** (physical pin 2)
- NodeMCU **GND** → Pico **GND** (physical pin 3)
- Both boards powered separately via USB. Do not join 5 V or 3.3 V rails.
- NodeMCU and main PC on a trusted, mutually reachable LAN.

See [wiring](docs/wiring.md) and [protocol](docs/protocol.md).

## Firmware

1. Install CircuitPython 10.x on an original Raspberry Pi Pico.
2. Copy `firmware/pico/code.py` to `CIRCUITPY/code.py`.
3. In Arduino IDE install ESP8266 board support and select **NodeMCU 1.0 (ESP-12E Module)**.
4. Copy `firmware/esp8266/secrets.example.h` to `firmware/esp8266/secrets.h` and configure SSID, password and a unique long token. Never commit secrets.
5. Upload `firmware/esp8266/esp8266.ino`. Locate its IP from router DHCP leases.

## Windows 11 client

Install .NET 8 SDK on the main PC, then:

```powershell
cd windows-client/KeyboardBridge
copy appsettings.example.json appsettings.json
# Edit Host and Token to match your ESP8266
dotnet run
```

Right-click the tray icon and choose **Settings & Connection Statistics** for a live connection panel. The panel allows editing the NodeMCU host/IP, TCP port, and shared token; **Save connection & restart** saves them to the active appsettings.json and restarts the Windows client in LOCAL mode. Never change the NodeMCU token without flashing the matching firmware. Change the shortcut to **Ctrl+Alt+F1** through **Ctrl+Alt+F12** (default F12); the preference is stored at `%LOCALAPPDATA%\\WifiKeyboardBridge\\preferences.json`. The panel shows connection state, active mode, time since connection, successfully written keyboard-state reports, disconnection count, and a measured Windows-to-NodeMCU TCP round-trip time (RTT) using PING/PONG. **Reflash the updated ESP8266 firmware** to enable RTT readings. The Pico firmware is unchanged. Reports are counted client-side and do **not** indicate delivery. RTT is not end-to-end keystroke latency. The NodeMCU IP, port and token remain in `appsettings.json` and require an app restart to change.\n\nUse the tray menu or **Ctrl+Alt+F12** (or your selected shortcut) to switch between local and remote input. Defaults to local. Publish standalone using `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`.

## Latency tuning\n\nESP8266 and Pico firmware both use **115200 baud** UART. Update **both** devices together; mismatched baud rates cause lost keys. Windows TCP client uses `NoDelay` to avoid Nagle coalescing. Pico polls UART every 0.5 ms; this is an experimental tuning baseline, not a measured latency guarantee. For testing, remove `print()` debug logging from the Pico's USB serial console. Keep the previous firmware available as a rollback.\n\n## Known limitations and safety

- Experimental: layout mappings assume a US HID keyboard; non-US layouts, dead keys and IMEs may vary.
- Windows protected desktops, elevated applications and OS-reserved shortcuts may not be forwarded.
- USB HID boot-keyboard report supports six simultaneous non-modifier keys.
- TCP/7777 uses a **plaintext shared token**, not encrypted or strong authentication. Trusted LAN only; never expose to the internet.
- Disconnect watchdog releases keys after an inactivity interval; event loss and transient stuck keys remain possible.
- Do not test on secure input/password dialogs. Confirm device use is allowed by organizational policy.

Licensed MIT.
