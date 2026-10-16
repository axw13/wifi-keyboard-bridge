# Wiring — verified final setup

ESP8266 NodeMCU V3 (ESP8266MOD) + original Raspberry Pi Pico RP2040.

The receiving PC supplies power and receives USB keyboard and mouse HID
through **one USB cable plugged into the Pico**. The Pico powers the NodeMCU
through its VBUS pin; the NodeMCU's separate USB port is **not connected**
during normal use.

| Raspberry Pi Pico | NodeMCU V3 | Purpose |
|---|---|---|
| GP1, physical pin 2 (UART RX) | TX / GPIO1 | Input reports NodeMCU → Pico, 115200 baud |
| GP0, physical pin 1 (UART TX) | D5 / GPIO14 | Pico diagnostic ACK → NodeMCU, 115200 baud |
| GND, physical pin 3 | GND | Common electrical ground |
| **VBUS, physical pin 40 (USB 5V)** | **VIN / verified 5V input** | Power NodeMCU from Pico USB supply |

**Power safety:** Verify that your exact NodeMCU board's VIN pin accepts 5V
input before connecting it. Only VBUS (5V) goes to a verified 5V VIN pin,
never to ESP8266 3V3 or GPIO. Pico GP0/GP1 and NodeMCU GPIO pins use 3.3V
logic. Make all connections with USB power disconnected. Never connect
Pico VBUS to NodeMCU VIN while also powering NodeMCU separately over USB,
unless the board's power-path protection has been verified. Never short
VBUS to ground. Ensure the PC USB port/cable can support both boards,
including ESP8266 Wi-Fi transmit current peaks. Random Wi-Fi dropouts or
resets may indicate insufficient supply or faulty wiring.

The same ground wire used for UART signaling also completes the power
return path. Make sure it is secure and appropriate for the NodeMCU's
operating current.

Do not use NodeMCU GPIO3/RX for Pico's diagnostic TX: that line can be
driven by the onboard USB-to-serial converter. GP0 → D5 is the verified
return path.

The diagnostic wire is optional for input forwarding but is part of the
user-tested final configuration. Without it the Pico UART RTT diagnostic
will time out while basic keyboard and mouse can still work.

## Verification

1. Disconnect both boards' USB power before wiring.
2. Confirm NodeMCU VIN pinout, connect all four wires as above, and
   insulate exposed connections.
3. Plug **only the Pico USB** into the receiving computer.
4. Confirm ESP8266 joins Wi-Fi and that Windows client connects.
5. Test LOCAL/REMOTE keyboard and mouse switching, numpad, emergency
   Ctrl+Alt+F10, and Pico UART diagnostic acknowledgments.
6. Monitor for brownouts or disconnects over a prolonged session.

NodeMCU hardware TX may emit boot messages while resetting; Pico ignores
invalid protocol lines. Use only on computers that permit external USB HID.

## Alternative

For different NodeMCU hardware or if the shared supply is unstable, revert
to independent USB power (disconnect the VBUS → VIN wire first), keeping
the UART TX/RX and common ground wires. See [protocol](protocol.md).
