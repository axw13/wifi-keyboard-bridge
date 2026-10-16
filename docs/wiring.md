# Wiring

ESP8266 NodeMCU V3 (ESP8266MOD) + original Raspberry Pi Pico (RP2040).

| NodeMCU | Pico | Signal |
|---|---|---|
| TX / GPIO1 | GP1 / physical pin 2 | UART 115200 8N1 |
| GND | GND / physical pin 3 | Common ground |

Power the two boards independently through USB; **do not wire their 5V or 3V3 pins together**. Pico's USB connector goes to the receiving PC; NodeMCU uses a separate USB power source. Secure and insulate wires before permanent use. NodeMCU's hardware TX pin can emit boot messages during reset; the Pico parser ignores invalid lines. Do not attach to machines that prohibit external USB devices.

## Experimental two-way Pico diagnostics (V2 feature branch only)

The existing TX connection and common GND remain unchanged. Optional additional signal:

| Pico | NodeMCU | Purpose |
|---|---|---|
| GP0 / physical pin 1 (UART TX) | RX / GPIO3 (UART0 RX) | Pico diagnostic acknowledgments at 115200 baud |

**IMPORTANT HARDWARE CAUTION:** On common NodeMCU V3 boards, GPIO3/RX is already connected to the on-board USB-to-UART adapter TX output. Attaching Pico GP0 TX can create **two actively driven outputs on the same line**, potentially damaging hardware. **Do not connect GP0 directly to NodeMCU GPIO3 while the onboard USB serial adapter TX is connected/active.** Use a UART-receive input that has been verified electrically isolated from the USB adapter, or add appropriate hardware isolation/multiplexing. A NodeMCU board's USB connector being used only for power does **not** guarantee that the adapter's TX pin is high-impedance. Unplugging USB to flash is necessary but not sufficient isolation.

Before attempting any return wiring, inspect the specific board schematic or use a different isolated RX GPIO with software serial support. The repository implementation currently assumes GPIO3/UART0 RX; **do not wire it until the electrical contention is resolved**. Both boards must share ground; connect only 3.3 V logic signals, never power rails. The original one-way configuration remains fully functional without this wire; Pico UART telemetry will show no acknowledgments.
