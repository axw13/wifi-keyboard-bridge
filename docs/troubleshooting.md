# Troubleshooting lag, disconnects and stuck input

## Where the logs are

The Windows client writes `%LOCALAPPDATA%\WifiKeyboardBridge\bridge.log`.
Older logs rotate to `bridge.1.log` … `bridge.3.log` (2 MiB each, roughly a
week or more of normal use). To find them, use **Open log folder** in the tray
menu or on the Diagnostics tab. **Export diagnostics** saves one text file with
the current statistics, the RTT history, the firmware counters and the last 400
log lines. Attach that file when you report a problem. The shared token is never
logged.

## What gets logged

| Entry | Meaning |
| --- | --- |
| `Application starting` / `Configuration:` | Version, Windows build, host/port/mDNS setting |
| `Connected to … TCP connect X ms, authenticated Y ms` | Each successful session |
| `Connect attempt failed: …` | Host unreachable, auth timeout, or `token mismatch?` |
| `Connection ended after hh:mm:ss: …` | Why the session ended, followed by a link summary |
| `No PONG for N ms; link presumed dead` | NodeMCU stopped answering for 10 s; the client reconnects instead of silently swallowing input |
| `Link stall: PONG after N ms, <direction>, RSSI R dBm` | A Wi-Fi stall of 500 ms or more (at most one line per 5 s). After each stall the client asks the NodeMCU (`GAP?`) how long it went without data. `PC->NodeMCU stalled` means your input really stopped arriving. `NodeMCU->PC replies delayed; input kept arriving` means typing and mouse still reached the remote PC and only the replies were late. With firmware older than 2.3 the line says `likely …`, a guess from Windows TCP retransmits. That guess is unreliable, because Windows also retransmits when only the NodeMCU's ACKs are lost |
| `Link summary (periodic)` | Every 60 s: RTT avg/max, stall count, RSSI, scheduler pacing, Pico probe, dropped frames |
| `Firmware: fw=…` | NodeMCU `DIAG` counters, at connect and every 60 s (see below) |
| `Keyboard REMOTE/LOCAL`, `Mouse REMOTE/LOCAL`, `Emergency LOCAL: reason` | Every mode change and why |
| `Windows power event` / `network availability changed` | Sleep, resume and adapter changes, which commonly explain a disconnect |
| `ERROR Unhandled …` | Client crashes, with the stack trace |

## Firmware counters (`DIAG`)

| Field | Meaning | If it keeps rising |
| --- | --- | --- |
| `up` | NodeMCU uptime (s) | Resets to a low value: NodeMCU rebooted. Check `reset` |
| `reset` | Reason for the last NodeMCU boot | `Hardware/Software Watchdog` or `Exception`: firmware crash or brown-out (power supply) |
| `rssi`, `ch`, `phy` | Signal (dBm), Wi-Fi channel, PHY mode (1 = b, 2 = g, 3 = n) | Below about −67 dBm expect stalls. Move the NodeMCU or the router, or use a less crowded channel |
| `wifidrop`, `wifireason` | Wi-Fi disconnects and the last 802.11 reason code | 200/201/202 beacon timeout, AP not found, auth fail: signal or router problem |
| `idletimeout` | Sessions dropped after 5 s without data | The PC or its Wi-Fi went away |
| `rxgap` | Longest silence from the PC since the previous `DIAG` (ms). Normally ≤ 250 | Values of 500 ms or more confirm a PC → NodeMCU stall |
| `authfail`, `authtimeout` | Bad token, or a connection that never authenticated | Something else on the LAN is connecting to port 7777 |
| `wdrelease` | Keys released by the 1.5 s NodeMCU watchdog | Stalls longer than 1.5 s. Held keys were released for safety |
| `badframe`, `overflow` | Rejected frames, over-long lines | Client/firmware version mismatch |
| `lastend` | Why the previous session ended | `closed`, `idle-timeout`, `auth-failed`, `auth-timeout`, `line-overflow` |
| `pico` | `uptime/frames/bad/watchdog/usb-errors/loop-errors` from the Pico | `bad` means UART corruption (wiring, ground). `usb` means the receiving PC rejected HID reports (asleep, or USB suspended). `errors` are recovered faults |

`pico` needs the D5/GPIO14 return wire. Without it, the field reads `none`.

## Reading the Diagnostics tab when input lags

* **RTT graph spikes of hundreds of ms to seconds** come from the Wi-Fi link,
  not the PC or the Pico. TCP holds every frame behind a lost packet until it is
  retransmitted. Check RSSI first. The NodeMCU firmware now disables Wi-Fi modem
  sleep, which removes most periodic 100–300 ms spikes. While a stall lasts, the
  client now holds mouse motion and sends it as one report when the link
  recovers, instead of replaying a burst of stale movement. Motion is held only
  while Windows reports its own frames to the NodeMCU as unacknowledged. A reply
  that is merely late does not freeze input.
* **The Link line** splits stalls into `PC→NodeMCU` and `NodeMCU→PC`, and shows
  TCP retransmissions. Mostly PC→NodeMCU stalls with the PC on Wi-Fi usually
  means the PC's own adapter is scanning or roaming (see below).
* **Mostly NodeMCU→PC stalls** mean packets *sent by the ESP8266* get lost. Its
  TCP stack retransmits only after about 1 s, then 2 s, then 4 s, which matches
  1–2 s stalls. Input is unaffected. If they are frequent, try a less crowded
  channel (fixed 1, 6 or 11 on the router), move the NodeMCU, or uncomment
  `#define WIFI_FORCE_11G` in `secrets.h` and reflash. `phy=2` in the firmware
  line confirms 11g is active.
* **Periodic 1–2 s stalls with good RSSI and `wifidrop=0`** are usually
  background Wi-Fi scans by the *PC*, or by the router changing channel. If the
  PC is on Wi-Fi, set the adapter's *Roaming Aggressiveness* to *Lowest*, turn
  off adapter power saving, or use Ethernet. Run `ping -t <router IP>` and
  `ping -t <NodeMCU IP>` side by side. If both spike together, the PC's Wi-Fi is
  the cause.
* **Scheduler interval stuck at about 16/32 ms** was the Windows 15.6 ms timer.
  The client now raises the timer resolution to 1 ms while connected (the
  Diagnostics tab shows `timer 1 ms`), so 8–11 ms mouse intervals work.
* **Pico UART probe** above a few ms, or timeouts rising while connected, points
  to the UART wiring or a Pico that is busy or stuck.
