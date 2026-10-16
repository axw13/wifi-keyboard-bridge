# Wi-Fi Keyboard Bridge

Experimental keyboard sharing: Windows 11 → Wi-Fi → ESP8266 NodeMCU V3 → UART → Raspberry Pi Pico RP2040 → USB HID → second computer. No software is installed on the receiving PC.

**Status:** The prior fixed-text transport was verified on hardware. This new live-keyboard prototype has **not** yet been compiled or verified end-to-end. Test with Notepad and keep a physical keyboard available.

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

Use the tray menu or **Ctrl+Alt+F12** to switch between local and remote input. Defaults to local. Publish standalone using `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`.

## Latency tuning\n\nESP8266 and Pico firmware both use **115200 baud** UART. Update **both** devices together; mismatched baud rates cause lost keys. Windows TCP client uses `NoDelay` to avoid Nagle coalescing. Pico polls UART every 0.5 ms; this is an experimental tuning baseline, not a measured latency guarantee. For testing, remove `print()` debug logging from the Pico's USB serial console. Keep the previous firmware available as a rollback.\n\n## Known limitations and safety

- Experimental: layout mappings assume a US HID keyboard; non-US layouts, dead keys and IMEs may vary.
- Windows protected desktops, elevated applications and OS-reserved shortcuts may not be forwarded.
- USB HID boot-keyboard report supports six simultaneous non-modifier keys.
- TCP/7777 uses a **plaintext shared token**, not encrypted or strong authentication. Trusted LAN only; never expose to the internet.
- Disconnect watchdog releases keys after an inactivity interval; event loss and transient stuck keys remain possible.
- Do not test on secure input/password dialogs. Confirm device use is allowed by organizational policy.

Licensed MIT.
