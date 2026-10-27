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
# The default 64-byte RX buffer fills in ~5 ms at 115200 baud; send_report() can block that long.
uart = busio.UART(tx=board.GP0, rx=board.GP1, baudrate=115200, timeout=0, receiver_buffer_size=512)
line = bytearray()
last_frame = time.monotonic()
released = bytes(8)
stale = False
boot = time.monotonic()

# Diagnostic counters, reported after every probe reply as "C,<up>,<frames>,<bad>,<wd>,<usb>,<err>".
frames = 0
bad_frames = 0
watchdog_releases = 0
usb_errors = 0
loop_errors = 0


def send_keyboard(report):
    global usb_errors
    try:
        keyboard.send_report(report)
    except OSError:
        # Host not enumerated yet, suspended, or USB busy; the next full-state frame recovers.
        usb_errors += 1


def send_mouse(report):
    global usb_errors
    try:
        mouse.send_report(report)
    except OSError:
        usb_errors += 1


def mouse_report(buttons, dx, dy, wheel):
    # Default CircuitPython HID mouse: buttons, signed X, signed Y, signed wheel.
    send_mouse(bytes([buttons, dx & 255, dy & 255, wheel & 255]))


def handle_line(data):
    global mouse_buttons, last_frame, frames, bad_frames, stale
    try:
        parts = bytes(data).decode("ascii").strip().split(",")
        if len(parts) == 2 and parts[0] == "D":
            # Diagnostic request only; reply after UART receipt. This is
            # not a timestamp for USB HID delivery or display updates.
            probe = int(parts[1])
            if 0 <= probe <= 65535:
                uart.write(("A,%d\n" % probe).encode("ascii"))
                uart.write(("C,%d,%d,%d,%d,%d,%d\n" % (
                    int(time.monotonic() - boot), frames, bad_frames,
                    watchdog_releases, usb_errors, loop_errors)).encode("ascii"))
            return
        if len(parts) == 5 and parts[0] == "M":
            buttons, dx, dy, wheel = [int(v) for v in parts[1:]]
            if 0 <= buttons <= 7 and all(-127 <= v <= 127 for v in (dx, dy, wheel)):
                mouse_buttons = buttons
                mouse_report(buttons, dx, dy, wheel)
                last_frame = time.monotonic()
                stale = False
                frames += 1
                return
        elif len(parts) == 8 and parts[0] == "S":
            nums = [int(p) for p in parts[1:]]
            if all(0 <= n <= 255 for n in nums):
                send_keyboard(bytes([nums[0], 0] + nums[1:]))
                last_frame = time.monotonic()
                stale = False
                frames += 1
                return
    except (ValueError, UnicodeError):
        pass
    if len(data):
        bad_frames += 1


send_keyboard(released)
send_mouse(bytes(4))

while True:
    try:
        waiting = uart.in_waiting
        incoming = uart.read(waiting) if waiting else None
        if incoming:
            for b in incoming:
                if b == 10:
                    handle_line(line)
                    line = bytearray()  # CircuitPython does not support bytearray item deletion
                elif b != 13:
                    if len(line) < 80:
                        line.append(b)
                    else:
                        bad_frames += 1
                        line = bytearray()
        if not stale and time.monotonic() - last_frame > 2:
            # Release once per silent period instead of every 2 s while idle.
            send_keyboard(released)
            mouse_buttons = 0
            send_mouse(bytes(4))
            watchdog_releases += 1
            stale = True
        time.sleep(0.0005)
    except Exception:  # noqa: BLE001 - an unattended device must keep running
        # Keep the receiver alive; release keys so nothing stays held after a fault.
        loop_errors += 1
        line = bytearray()
        try:
            keyboard.send_report(released)
            mouse.send_report(bytes(4))
        except Exception:  # noqa: BLE001
            pass
        time.sleep(0.05)
