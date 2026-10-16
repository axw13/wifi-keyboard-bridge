"""Pico CircuitPython HID keyboard receiver. UART GP1 RX at 115200 baud."""
import board
import busio
import time
import usb_hid

keyboard = None
for device in usb_hid.devices:
    if device.usage_page == 1 and device.usage == 6:
        keyboard = device
        break
if keyboard is None:
    raise RuntimeError("USB HID keyboard device not enabled")
mouse = None
for device in usb_hid.devices:
    if device.usage_page == 1 and device.usage == 2:
        mouse = device
        break
if mouse is None:
    raise RuntimeError("USB HID mouse device not enabled")
mouse_buttons = 0
uart = busio.UART(tx=board.GP0, rx=board.GP1, baudrate=115200, timeout=0)
line = bytearray()
last_frame = time.monotonic()
released = bytes(8)
keyboard.send_report(released)
mouse.send_report(bytes(4))

def mouse_report(buttons, dx, dy, wheel):
    # Default CircuitPython HID mouse: buttons, signed X, signed Y, signed wheel.
    mouse.send_report(bytes([buttons, dx & 255, dy & 255, wheel & 255]))

while True:
    incoming = uart.read(64)
    if incoming:
        for b in incoming:
            if b == 10:
                try:
                    parts = bytes(line).decode("ascii").strip().split(",")
                    if len(parts) == 5 and parts[0] == "M":
                        values = [int(v) for v in parts[1:]]
                        buttons, dx, dy, wheel = values
                        if 0 <= buttons <= 7 and all(-127 <= v <= 127 for v in (dx, dy, wheel)):
                            mouse_buttons = buttons
                            mouse_report(buttons, dx, dy, wheel)
                            last_frame = time.monotonic()
                    elif len(parts) == 8 and parts[0] == "S":
                        nums = [int(p) for p in parts[1:]]
                        if all(0 <= n <= 255 for n in nums):
                            keyboard.send_report(bytes([nums[0], 0] + nums[1:]))
                            last_frame = time.monotonic()
                except (ValueError, UnicodeError):
                    pass
                line = bytearray()  # CircuitPython does not support bytearray item deletion
            elif b != 13:
                if len(line) < 80:
                    line.append(b)
                else:
                    line = bytearray()
    if time.monotonic() - last_frame > 2:
        keyboard.send_report(released)
        mouse_buttons = 0
        mouse.send_report(bytes(4))
        last_frame = time.monotonic()
    time.sleep(0.0005)
