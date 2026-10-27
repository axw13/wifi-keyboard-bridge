# Keyboard state protocol

Main PC connects to ESP8266 TCP port 7777 and runs the v3 handshake (see **Security** below). After it, every
command in this document is an encrypted, authenticated line; the examples show the plaintext of each line.

Every key-state change sends:
```text
S,<modifier>,<key1>,<key2>,<key3>,<key4>,<key5>,<key6>
```
Values are decimal USB HID keyboard usages, with zero for empty slots. The first byte is the HID modifier bitmap (Ctrl/Shift/Alt/GUI). NodeMCU forwards complete lines over UART at 115200 baud to Pico GP1. Pico sends USB boot-keyboard report: modifier, reserved byte, six usage bytes. `S,0,0,0,0,0,0,0` releases all keys.

NodeMCU releases keys after 1.5 s without a frame; Pico releases them after 2 s without a valid frame. These are fallbacks, not delivery acknowledgments.

**Reliability:** there is no end-to-end acknowledgment of key delivery; reliability is constrained by UART 115200 baud, Wi-Fi and six-key HID rollover.

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

## Security (protocol v3, firmware 3.0+)

The shared token **never crosses the network** and nothing after the handshake is readable. The plaintext
`AUTH <token>` of earlier versions is gone: firmware 3.0 refuses it and a v3 client refuses older firmware.

**Handshake** (the only plaintext, newline-terminated hex lines):

```text
client -> HELLO2 <cn>                                              cn: 16 random bytes
server -> CHAL <sn> <HMAC-SHA256(token, "WKB3-srv" | cn | sn)>     sn: 16 random bytes (hardware RNG)
client -> PROOF <HMAC-SHA256(token, "WKB3-cli" | cn | sn)>
```

* The client checks the server's proof first, so an impostor NodeMCU (or an mDNS spoof) that does not know the token is
  refused before the client reveals anything. The server then checks the client's proof in constant time.
* Both sides derive `okm = HKDF-SHA256(salt = cn | sn, ikm = token, info = "WKB3 keys", 64 bytes)`:
  `okm[0..32]` encrypts client-to-server traffic, `okm[32..64]` server-to-client. Keys are fresh per session
  and wiped from the NodeMCU's RAM when it ends.
* The server's first encrypted frame is `OK`; decrypting it proves both sides derived the same keys. A failed login gets
  a bare `ERR` and the socket is closed. Consecutive failures delay the next accepted connection
  (0.5 s, 1 s, 2 s, ... up to 8 s), so online guessing is impractical.

**TCP framing** after `OK`: `[length: u16 LE][ciphertext][tag: 16]`, ChaCha20-Poly1305 (RFC 8439). The nonce is
`01 00 00 00 | frame counter (u64 LE)`, counted separately per direction from 0; the two length bytes are authenticated
as associated data. A frame that fails authentication, is replayed, reordered, dropped or longer than 128 bytes
(client to NodeMCU) ends the session immediately; the NodeMCU records `lastend=bad-tag` and counts `badtag` in `DIAG`.

**What this protects against:** eavesdropping on keystrokes, token capture, forged or injected input (TCP and UDP),
replay, and a fake NodeMCU. **What it does not:** someone who already knows the token, a compromised PC or NodeMCU,
traffic analysis (the timing and size of packets still show *that* you are typing), or denial of service by jamming or
flooding. The token is a pre-shared secret, so use 16+ random characters: an attacker who records a handshake can try
guesses offline. Keep `secrets.h` and `appsettings.json` private. Do not forward ports 7777/7778 to the internet.

## UDP fast path (firmware 2.4+, optional; encrypted since 3.0)

TCP delivers in order, so one lost Wi-Fi packet delays every key and mouse report behind it, and
TCP's retransmit backoff keeps them waiting after the link has recovered. The UDP fast path avoids
that: every datagram is a complete, idempotent snapshot, so a lost datagram is repaired by the next
one and a late one is dropped by sequence number.

1. After the handshake the client sends `UDP?`. The NodeMCU answers `UDP,<port>` (7778).
2. The client sends datagrams to that port, little endian, encrypted with the same session keys:

   | Type | Layout | Size |
   | --- | --- | --- |
   | State `0xC1` | `C1 seq(4) enc(mods keys(6) buttons x(2) y(2) wheel(2)) tag(16)` | 35 B |
   | Ping `0xC2` | `C2 seq(4) enc(payload(4)) tag(16)`; answered by `C3` with the NodeMCU's own counter | 25 B |

   The first 5 bytes are authenticated as associated data. The nonce is `02 00 00 00 | seq (u64 LE)`; **one
   sequence counter covers state and ping packets**, so a (key, nonce) pair is never reused. The NodeMCU drops a
   datagram unless it is from the TCP peer's address, newer than the last accepted one, and authentic; the replay
   window only moves after the tag verifies, so forged packets cannot lock the real client out.
   `x`, `y` and `wheel` are cumulative signed 16-bit counters (wrapping) starting at zero when `UDP?` is answered.
   The NodeMCU forwards the difference as ordinary `M,` reports (at most 16 per datagram; the rest is dropped) and
   forwards `S,` only when the keys change or every 250 ms as the Pico's keepalive.
3. While UDP is active the client sends no `S,`/`M,` over TCP (a delayed TCP frame could undo a newer state).
   TCP still carries `PING`, `STAT`, `DIAG`, `IDLE` and the other commands.
4. The client sends a state datagram on every change (repeated for the next 3 mouse slots), every mouse slot with
   motion, and at least every 250 ms.
5. `UACK?` -> `UACK,<datagrams accepted>,<lost by sequence gap>,<stale or duplicate>`. If the accepted count does not
   move for three checks (6 s) while the client keeps sending, the client logs a warning and falls back to TCP for the
   rest of the session (for example when the router filters UDP).
6. `SELFTEST` -> `SELFTEST,OK` (or `SELFTEST,FAIL,<mask>`): the firmware runs RFC 4231, RFC 5869 and RFC 8439
   known-answer tests.

`DIAG` gains `sec=3`, `badtag=`, `udp=`, `udplost=`, `udpstale=` and `udpbad=` (datagrams that were forged, malformed or
arrived with no UDP session). Set `"UseUdp": false` in `appsettings.json` to force TCP.

## Tests

`dotnet run --project windows-client/KeyboardBridge.Tests` runs the published test vectors, key derivation, tamper
detection (every single-bit change), replay/reorder/oversize handling and a handshake against an in-process server.
Add `-- --device` (Windows client closed, Pico plugged into the same PC) to also test the real NodeMCU: cross-implementation
handshake, firmware self-test, wrong-token and back-off behaviour, tampered and replayed TCP and UDP traffic, a wire capture
showing no token or command text in the clear, and key/mouse delivery through the Pico with latency.

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
