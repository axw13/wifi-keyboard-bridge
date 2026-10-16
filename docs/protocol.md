# Keyboard state protocol v0

Main PC connects to ESP8266 TCP port 7777. Send `AUTH <token>\n`, receive `OK\n` or `ERR\n`.

Every key-state change sends:
```text
S,<modifier>,<key1>,<key2>,<key3>,<key4>,<key5>,<key6>
```
Values are decimal USB HID keyboard usages, with zero for empty slots. The first byte is the HID modifier bitmap (Ctrl/Shift/Alt/GUI). NodeMCU forwards complete lines over UART at 115200 baud to Pico GP1. Pico sends USB boot-keyboard report: modifier, reserved byte, six usage bytes. `S,0,0,0,0,0,0,0` releases all keys.

NodeMCU releases keys after 1.5 s without a frame; Pico releases them after 2 s without a valid frame. These are fallbacks, not delivery acknowledgments.

**Security:** Token is plaintext over TCP; use only a trusted LAN, firewall port 7777, never port-forward it to the internet. No end-to-end acknowledgment or encryption. Reliability is constrained by UART 115200 baud, Wi-Fi and six-key HID rollover.

## Optional V2 Pico diagnostic probe (experimental)

With a matching V2 Windows client, NodeMCU and Pico firmware, Windows sends `PICO?,<id>\n` to the authenticated NodeMCU TCP connection approximately every 3 seconds. The NodeMCU transmits `D,<id>\n` to Pico on the **existing** UART GPIO1 TX → GP1 RX (115200 baud), then timestamps the request using `micros()`. Pico replies `A,<id>\n` on a **separate** diagnostic return UART, GP4 TX → NodeMCU D5/GPIO14 software RX at **38400 baud**. NodeMCU returns `PICO,<id>,<microseconds>\n` to Windows when the ID matches. NodeMCU stops waiting after approximately 500 ms; Windows counts missing responses at the next probe.

This is a **UART probe round trip**, *not* a measurement of Windows-to-Pico, USB host acceptance or cursor/display response. The probe uses an independent diagnostic request rather than tagging every mouse report, to avoid affecting the stable HID packet parser. With the original one-way wiring, the probe times out while keyboard/mouse remain functional. Do not wire Pico TX directly to ESP8266 UART0 RX/GPIO3 on a NodeMCU with an onboard USB-to-UART TX driver; use the documented separate GPIO14 input.
