# Wiring

ESP8266 NodeMCU V3 (ESP8266MOD) + original Raspberry Pi Pico (RP2040).

| NodeMCU | Pico | Signal |
|---|---|---|
| TX / GPIO1 | GP1 / physical pin 2 | UART 115200 8N1 |
| GND | GND / physical pin 3 | Common ground |

Power the two boards independently through USB; **do not wire their 5V or 3V3 pins together**. Pico's USB connector goes to the receiving PC; NodeMCU uses a separate USB power source. Secure and insulate wires before permanent use. NodeMCU's hardware TX pin can emit boot messages during reset; the Pico parser ignores invalid lines. Do not attach to machines that prohibit external USB devices.

## Experimental Pico diagnostic return (V2 feature branch only)

The existing 115200-baud NodeMCU GPIO1 TX → Pico GP1 RX and shared GND stay exactly as before. Add **one optional 3.3 V signal wire**:

| Pico | NodeMCU | Purpose |
|---|---|---|
| GP0 / physical pin 1 (diagnostic UART TX) | D5 / GPIO14 (software UART RX) | Pico diagnostic ACK, 115200 baud |

GP0 is the existing Pico UART0 TX pin; no second Pico UART is needed. Do not connect Pico GP0 TX to NodeMCU GPIO3/RX: GPIO3 is already connected to the onboard USB-to-serial converter on common NodeMCU boards, risking electrical output contention. NodeMCU D5/GPIO14 is a separate digital input used by the optional software UART.

To test: first deploy both matching V2 firmware revisions, with power disconnected before changing wires. Keep separate USB power to each board and the common ground. Only after wiring D5 ← GP0, power up and open the Windows Diagnostics tab. Without the optional wire (or firmware), mouse/keyboard continue functioning normally; Pico telemetry times out. ESP8266 software UART reception at 115200 baud can be unreliable under Wi-Fi load; assess ACK timeouts. Firmware compatibility and timing must be verified on actual hardware before relying on the telemetry.

**Meaning of the number:** this probe measures on-NodeMCU elapsed time between transmitting a diagnostic request on its hardware UART (115200 baud) and receiving Pico's ACK over the independent return UART (115200 baud). It includes UART serialization, Pico scheduling and ACK transmission. It does **not** measure end-to-end Windows-to-USB input/display latency, and does not prove a HID report was accepted by the host.
