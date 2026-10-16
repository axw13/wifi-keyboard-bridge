# First-run setup and optional Pico USB storage hiding

## Windows setup
On a new installation, launch `KeyboardBridge.exe`. A connection dialog opens if no valid
`appsettings.json` is found. Enter NodeMCU host/IP, TCP port (default 7777), and the
shared authentication token (16+ characters). Click **Save and connect**.

New settings are written to:
`%LOCALAPPDATA%\WifiKeyboardBridge\appsettings.json`

Existing installations with `appsettings.json` beside the executable keep using that file.
The settings window can still change the connection settings; the client then restarts.
Don't include your actual token in screenshots, public releases, or support logs.

## Pico USB: hide CIRCUITPY storage
If the Pico is running CircuitPython, the visible USB storage device is normally the
`CIRCUITPY` drive, not the Pico's BOOTSEL/UF2 bootloader disk (`RPI-RP2`).
It is normal and does not affect HID operation.

To hide the storage volume on the target computer, **optionally** copy
`firmware/pico/boot.py` to the root of the Pico `CIRCUITPY` volume.
Reboot/reconnect the Pico. CircuitPython runs `boot.py` before `code.py` and
`storage.disable_usb_drive()` stops advertising the mass-storage interface.

**IMPORTANT**: Do this only after the keyboard/mouse firmware is known to work.
Hiding storage also makes ordinary USB editing of `code.py` inaccessible. To restore
editing, start CircuitPython in safe mode (which skips `boot.py`) and remove
`boot.py`; if safe mode is unavailable, use the BOOTSEL UF2 recovery process and
reinstall CircuitPython. Back up `code.py` and `boot.py` first.

This option keeps **both HID keyboard and mouse** enabled. Making the Pico a
strictly keyboard-only USB device would remove mouse HID and break mouse forwarding.
On some hosts, the USB device may still be labeled Raspberry Pi Pico / CircuitPython
in Device Manager, because disabling the mass-storage interface is not the same as
changing USB manufacturer/product descriptors. This does not conceal the hardware
identity. The serial interface, if enabled, is a separate USB interface.
