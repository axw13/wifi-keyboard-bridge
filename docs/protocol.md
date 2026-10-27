# Keyboard state protocol

Main PC connects to ESP8266 TCP port 7777. Send `AUTH <token>\n`, receive `OK\n` or `ERR\n`.

Every key-state change sends:
```text
S,<modifier>,<key1>,<key2>,<key3>,<key4>,<key5>,<key6>
```
Values are decimal USB HID keyboard usages, with zero for empty slots. The first byte is the HID modifier bitmap (Ctrl/Shift/Alt/GUI). NodeMCU forwards complete lines over UART at 115200 baud to Pico GP1. Pico sends USB boot-keyboard report: modifier, reserved byte, six usage bytes. `S,0,0,0,0,0,0,0` releases all keys.

NodeMCU releases keys after 1.5 s without a frame; Pico releases them after 2 s without a valid frame. These are fallbacks, not delivery acknowledgments.

**Security:** Token is plaintext over TCP; use only a trusted LAN, firewall port 7777, never port-forward it to the internet. No end-to-end acknowledgment or encryption. Reliability is constrained by UART 115200 baud, Wi-Fi and six-key HID rollover.

## Optional V2 Pico diagnostic probe (experimental)

With a matching V2 Windows client, NodeMCU and Pico firmware, Windows sends `PICO?,<id>\n` to the authenticated NodeMCU TCP connection approximately every 3 seconds. The NodeMCU transmits `D,<id>\n` to Pico on the **existing** UART GPIO1 TX → GP1 RX (115200 baud), then timestamps the request using `micros()`. Pico replies `A,<id>\n` on a existing Pico UART0 TX, GP0 → NodeMCU D5/GPIO14 software RX at **115200 baud**. NodeMCU returns `PICO,<id>,<microseconds>\n` to Windows when the ID matches. NodeMCU stops waiting after approximately 500 ms; Windows counts missing responses at the next probe.

This is a **UART probe round trip**, *not* a measurement of Windows-to-Pico, USB host acceptance or cursor/display response. The probe uses an independent diagnostic request rather than tagging every mouse report, to avoid affecting the stable HID packet parser. With the original one-way wiring, the probe times out while keyboard/mouse remain functional. Do not wire Pico TX directly to ESP8266 UART0 RX/GPIO3 on a NodeMCU with an onboard USB-to-UART TX driver; use the documented separate GPIO14 input.

## Full command reference (Windows → NodeMCU, after `OK`)

| Command | Reply | Purpose |
| --- | --- | --- |
| `S,<mod>,<k1>..<k6>` | — | Full keyboard state. Resent every 250 ms as a keepalive |
| `M,<buttons>,<dx>,<dy>,<wheel>` | — | Mouse report. `buttons` 0–7, deltas −127..127 |
| `PING` | `PONG` | Link RTT. Sent every 500 ms, one outstanding at a time |
| `STAT` | `STAT,<rssi>,<free heap>,<uart frames>,<uart bytes>` | Telemetry, every 2 s |
| `DIAG` | `DIAG,key=value,...` | Diagnostic counters (see `docs/troubleshooting.md`), at connect and every 60 s. Unknown keys must be ignored |
| `GAP?` | `GAP,<ms>` | Longest gap between data received from the client since the previous `GAP?`. The client asks after each stall to tell its direction (firmware 2.3+) |
| `PICO?,<id>` | `PICO,<id>,<µs>` | UART probe (above) |
| `IDLE,ON` / `IDLE,OFF` / `IDLE?` | `IDLE,ON` or `IDLE,OFF` | Persisted anti-idle setting |
| `IDLE,PAUSE` / `IDLE,RESUME` | — | Pause anti-idle while inputs are REMOTE. Cleared automatically when the session ends |

Pico → NodeMCU diagnostic lines: `A,<id>` (probe reply), then
`C,<uptime s>,<frames>,<bad frames>,<watchdog releases>,<usb errors>,<loop errors>`.

## Session timeouts

* NodeMCU closes a connection that has not authenticated within 2 s.
* NodeMCU closes an authenticated session after 5 s without any received data.
  Clients must send at least every 250 ms.
* The Windows client treats 10 s without a `PONG` as a dead link and reconnects.
  Replies can be lost for several seconds while input still arrives, so this
  only catches a NodeMCU that is gone. On reconnect it tries the last
  authenticated address for 1 s before falling back to mDNS.
  While more than 200 bytes it sent are unacknowledged (Windows `TCP_INFO`), it
  holds mouse motion (accumulated, bounded) so stale reports don't queue behind a
  Wi-Fi retransmission. Keepalives are always sent.
