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
| `No PONG for N ms; link presumed dead` | NodeMCU stopped answering; the client reconnects instead of silently swallowing input |
| `Link stall: PONG after N ms, RSSI R dBm` | A Wi-Fi stall of 500 ms or more (at most one line per 5 s) |
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
| `rssi`, `ch` | Signal (dBm) and Wi-Fi channel | Below about −67 dBm expect stalls. Move the NodeMCU or the router, or use a less crowded channel |
| `wifidrop`, `wifireason` | Wi-Fi disconnects and the last 802.11 reason code | 200/201/202 beacon timeout, AP not found, auth fail: signal or router problem |
| `idletimeout` | Sessions dropped after 5 s without data | The PC or its Wi-Fi went away |
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
  recovers, instead of replaying a burst of stale movement.
* **Scheduler interval stuck at about 16/32 ms** was the Windows 15.6 ms timer.
  The client now raises the timer resolution to 1 ms while connected (the
  Diagnostics tab shows `timer 1 ms`), so 8–11 ms mouse intervals work.
* **Pico UART probe** above a few ms, or timeouts rising while connected, points
  to the UART wiring or a Pico that is busy or stuck.
