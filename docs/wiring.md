# Wiring

ESP8266 NodeMCU V3 (ESP8266MOD) + original Raspberry Pi Pico (RP2040).

| NodeMCU | Pico | Signal |
|---|---|---|
| TX / GPIO1 | GP1 / physical pin 2 | UART 9600 8N1 |
| GND | GND / physical pin 3 | Common ground |

Power the two boards independently through USB; **do not wire their 5V or 3V3 pins together**. Pico's USB connector goes to the receiving PC; NodeMCU uses a separate USB power source. Secure and insulate wires before permanent use. NodeMCU's hardware TX pin can emit boot messages during reset; the Pico parser ignores invalid lines. Do not attach to machines that prohibit external USB devices.
