"""Optional CircuitPython boot-time USB configuration for a deployed Pico.

Hides the CIRCUITPY USB storage volume from the receiving computer while
preserving USB HID keyboard and mouse interfaces. Install only after firmware
configuration is complete, because normal USB file editing will be unavailable.

Remove boot.py in CircuitPython safe mode (boot.py is skipped there) or
reinstall CircuitPython firmware to restore normal access.
"""
import storage
storage.disable_usb_drive()
