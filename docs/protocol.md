# Keyboard state protocol v0

Main PC connects to ESP8266 TCP port 7777. Send `AUTH <token>\n`, receive `OK\n` or `ERR\n`.

Every key-state change sends:
```text
S,<modifier>,<key1>,<key2>,<key3>,<key4>,<key5>,<key6>
```
Values are decimal USB HID keyboard usages, with zero for empty slots. The first byte is the HID modifier bitmap (Ctrl/Shift/Alt/GUI). NodeMCU forwards complete lines over UART at 9600 baud to Pico GP1. Pico sends USB boot-keyboard report: modifier, reserved byte, six usage bytes. `S,0,0,0,0,0,0,0` releases all keys.

NodeMCU releases keys after 1.5 s without a frame; Pico releases them after 2 s without a valid frame. These are fallbacks, not delivery acknowledgments.

**Security:** Token is plaintext over TCP; use only a trusted LAN, firewall port 7777, never port-forward it to the internet. No end-to-end acknowledgment or encryption. Reliability is constrained by UART 9600 baud, Wi-Fi and six-key HID rollover.
