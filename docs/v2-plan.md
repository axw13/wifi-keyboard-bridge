# V2 delivery plan and compatibility

This is an incremental release. **Do not replace a working main-branch installation with all changes at once.**

## Implemented on the v2 feature branch (Windows only)
- Ctrl+Alt+F10 emergency LOCAL fallback, independently of linked switching.
- Optional autostart through the current user's Windows Run registry key. Enable only from a published executable, not `dotnet run`.
- GitHub Actions Windows Release build + self-contained win-x64 publish artifact (not code-signed).
- Save diagnostics to a user-chosen text report for comparison.
- **Tabbed settings**: Connection, Input, Performance, Diagnostics.
- Existing 90 Hz mouse forwarding and NodeMCU/Pico firmware remain unchanged.

## Additional milestones to implement and test before merge
1. **Recovery hardening:** Ctrl+Alt+F10 is now reserved, shortcut collisions are rejected and connection loss switches both devices to LOCAL. Hardware regression testing is still required for disconnects, held modifier/mouse-button ownership and repeated emergency hotkey presses. Verify fail-local behavior and that both the Windows client and Pico release state.
2. **Desktop UX:** tabbed Connection/Input/Performance/Diagnostics settings; connection/error feedback and stronger tray status indication. Validate single-file publish and autostart with per-user config paths.
3. **Performance history/adaptive mode:** rolling graph of authenticated TCP RTT samples and opt-in experimental adaptive scheduler with 8-second hysteresis are implemented. Manual 90 Hz preference remains the default. Future improvement: graph scheduler/backlog alongside RTT and validate adaptive behavior on hardware.
4. **Two-way Pico diagnostics (requires hardware changes):** add Pico GP0 (UART TX) to ESP8266 GPIO3 (UART0 RX), sharing existing ground. GPIO3 conflicts with NodeMCU serial/programming, so test with USB flashing disconnected and account for boot chatter. Add numbered diagnostic probe and return ACK that records Pico receipt/processing timestamps; report round-trip and duration without claiming synchronized one-way latency. Receiver/USB display latency cannot be measured without extra instrumentation.
5. **Additional input:** side mouse buttons require a custom USB HID report descriptor on the Pico rather than simply sending button flags with the default three-button mouse descriptor. Add separate keyboard layout and scroll speed tests.

## Verification checklist
- Build the .NET Windows target using CI, then run on Windows 11.
- Test raw movement at 90 Hz, clicks, wheel, keyboard modifiers, hotkey changes and two simultaneous shortcuts.
- Test abrupt ESP8266 power loss, temporary Wi-Fi disconnect, and app Exit during held keys.
- Test published executable in its final folder; create `appsettings.json` alongside executable before launch.
- Only after separate firmware testing should two-way UART wiring and USB HID descriptor changes be made.

## Recovery/rollback
`main` is the previously tested baseline. For this feature branch: `git fetch origin; git switch feature/v2-reliability-diagnostics`. To return: exit the app; `git switch main`; `dotnet build`; `dotnet run`.

### Experimental performance controls

The Diagnostics tab includes a rolling RTT line graph (up to 256 TCP PONG samples; typical sampling 2 seconds, faster during benchmark). This is **Windows-to-NodeMCU RTT**, not end-to-end USB input lag. The Performance tab offers an **Experimental adaptive mouse pacing** checkbox, OFF by default. Its 8-second evaluation windows adjust the effective mouse report interval between the selected manual interval and 33 ms, using scheduler lateness and pending movement. It never increases the mouse update rate beyond the manual choice. Turning it OFF immediately restores the manual interval. The checkbox persists separately from the manual update rate. Do not enable for production until you have compared continuous motion quality with the stable 90 Hz manual setting.
