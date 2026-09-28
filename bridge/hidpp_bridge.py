"""HID++ bridge for the G502 X LIGHTSPEED on a 046d:c547 receiver (slot 1).

`--service` is the JSON-lines bridge used by the BetterGhub desktop app: it
enables volatile host mode (0x8100) and button spying (0x8110), streams
button events, and applies DPI (0x2201) and report rate (0x8060) commands.
Without `--service` it is a one-shot diagnostic. It never writes flash.
"""

import argparse
import json
import queue
import sys
import threading
import time

import hid


VID = 0x046D
PID = 0xC547
SLOT = 1
SOFTWARE_ID = 0x0D


def endpoint(usage: int):
    matches = [
        item for item in hid.enumerate(VID, PID)
        if item["usage_page"] == 0xFF00 and item["usage"] == usage
    ]
    if len(matches) != 1:
        raise RuntimeError(f"Expected one vendor HID usage {usage}; found {len(matches)}")
    device = hid.device()
    device.open_path(matches[0]["path"])
    return device


def call(short, long, feature: int, function: int, args: bytes) -> bytes:
    if len(args) != 3:
        raise ValueError("Short HID++ call needs three argument bytes")
    function_software = (function << 4) | SOFTWARE_ID
    request = bytes([0x10, SLOT, feature, function_software]) + args
    written = short.write(request)
    if written != len(request):
        raise RuntimeError(f"Wrote {written} of {len(request)} request bytes")

    deadline = time.monotonic() + 2.0
    while time.monotonic() < deadline:
        for device in (short, long):
            reply = bytes(device.read(64, 80))
            if len(reply) < 4 or reply[1] != SLOT:
                continue
            if reply[0] == 0x8F or (reply[0] == 0x10 and reply[2] == 0x8F):
                raise RuntimeError(f"HID++ error response: {reply.hex(' ')}")
            if reply[0] in (0x10, 0x11) and reply[2:4] == request[2:4]:
                return reply[4:]
    raise TimeoutError(f"No reply to HID++ feature={feature:#x} function={function}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--set-host", action="store_true", help="Switch volatile mode to host and verify")
    parser.add_argument("--listen-seconds", type=int, default=0, help="Read button-spy HID++ notifications")
    parser.add_argument("--service", action="store_true", help="JSON-line device bridge for BetterGhub.App")
    args = parser.parse_args()

    if args.service:
        run_service()
        return

    short = endpoint(1)
    long = endpoint(2)
    try:
        feature_info = call(short, long, 0, 0, bytes([0x81, 0x00, 0]))
        index = feature_info[0]
        if index == 0:
            raise RuntimeError("This mouse does not expose OnboardProfiles (0x8100)")
        print(f"OnboardProfiles 0x8100 is feature index {index}")
        mode = call(short, long, index, 2, b"\x00\x00\x00")[0]
        print(f"Current mode: {mode} ({'onboard' if mode == 1 else 'host' if mode == 2 else 'unknown'})")
        if args.set_host and mode != 2:
            call(short, long, index, 1, b"\x02\x00\x00")
            new_mode = call(short, long, index, 2, b"\x00\x00\x00")[0]
            if new_mode != 2:
                raise RuntimeError(f"Host-mode write did not persist in RAM: read back {new_mode}")
            print("Switched to host mode in device RAM. No onboard profile was written.")
        if args.listen_seconds:
            spy_info = call(short, long, 0, 0, bytes([0x81, 0x10, 0]))
            spy_index = spy_info[0]
            if spy_index == 0:
                raise RuntimeError("This mouse does not expose MouseButtonSpy (0x8110)")
            call(short, long, spy_index, 1, b"\x00\x00\x00")
            print(f"MouseButtonSpy 0x8110 is feature index {spy_index}; startSpy succeeded; listening now", flush=True)
            deadline = time.monotonic() + args.listen_seconds
            previous = 0
            while time.monotonic() < deadline:
                report = bytes(long.read(64, 200))
                if len(report) < 6 or report[0:3] != bytes([0x11, SLOT, spy_index]) or report[3] != 0:
                    continue
                mask = (report[4] << 8) | report[5]
                changed = mask ^ previous
                for bit in range(16):
                    flag = 1 << bit
                    if changed & flag:
                        edge = "DOWN" if mask & flag else "UP"
                        print(f"BUTTON bit=0x{flag:04x} {edge}", flush=True)
                previous = mask
    finally:
        short.close()
        long.close()


def emit(kind: str, **fields) -> None:
    print(json.dumps({"type": kind, **fields}), flush=True)


def run_service() -> None:
    """Keep the volatile host configuration alive across receiver reconnects."""
    commands: queue.Queue[dict] = queue.Queue()

    def read_commands() -> None:
        for line in sys.stdin:
            try:
                commands.put(json.loads(line))
            except json.JSONDecodeError:
                emit("error", message="Invalid command JSON")
        commands.put({"command": "quit"})

    threading.Thread(target=read_commands, daemon=True).start()
    while True:
        short = long = None
        try:
            short, long = endpoint(1), endpoint(2)
            mode_index = call(short, long, 0, 0, b"\x81\x00\x00")[0]
            spy_index = call(short, long, 0, 0, b"\x81\x10\x00")[0]
            dpi_index = call(short, long, 0, 0, b"\x22\x01\x00")[0]
            rate_index = call(short, long, 0, 0, b"\x80\x60\x00")[0]
            if not mode_index or not spy_index:
                raise RuntimeError("G502 X host mode or button spying unavailable")
            mode = call(short, long, mode_index, 2, b"\x00\x00\x00")[0]
            if mode != 2:
                call(short, long, mode_index, 1, b"\x02\x00\x00")
                if call(short, long, mode_index, 2, b"\x00\x00\x00")[0] != 2:
                    raise RuntimeError("Host mode did not persist in device RAM")
            call(short, long, spy_index, 1, b"\x00\x00\x00")
            dpi = None
            if dpi_index:
                payload = call(short, long, dpi_index, 2, b"\x00\x00\x00")
                dpi = (payload[1] << 8) | payload[2]
            rate = call(short, long, rate_index, 1, b"\x00\x00\x00")[0] if rate_index else None
            emit("connected", dpi=dpi, report_interval_ms=rate, dpi_supported=bool(dpi_index), rate_supported=bool(rate_index))
            previous = 0
            last_report = time.monotonic()
            while True:
                try:
                    command = commands.get_nowait()
                except queue.Empty:
                    command = None
                if command:
                    name = command.get("command")
                    if name == "quit":
                        return
                    try:
                        if name == "set_dpi":
                            value = int(command["value"])
                            if not dpi_index or not 100 <= value <= 25600:
                                raise ValueError("DPI unavailable or outside 100–25600")
                            call(short, long, dpi_index, 3, bytes([0, value >> 8, value & 255]))
                            payload = call(short, long, dpi_index, 2, b"\x00\x00\x00")
                            emit("dpi", value=(payload[1] << 8) | payload[2])
                        elif name == "set_report_interval":
                            value = int(command["value"])
                            if not rate_index or value not in (1, 2, 4, 8):
                                raise ValueError("Report interval unavailable or unsupported")
                            call(short, long, rate_index, 2, bytes([value, 0, 0]))
                            emit("report_interval", value=call(short, long, rate_index, 1, b"\x00\x00\x00")[0])
                        else:
                            raise ValueError(f"Unknown command {name}")
                    except Exception as exc:
                        emit("error", message=str(exc))
                report = bytes(long.read(64, 100))
                if report:
                    last_report = time.monotonic()
                if len(report) >= 6 and report[0:3] == bytes([0x11, SLOT, spy_index]) and report[3] == 0:
                    mask = (report[4] << 8) | report[5]
                    changed = mask ^ previous
                    for bit in range(16):
                        flag = 1 << bit
                        if changed & flag:
                            emit("button", bit=flag, down=bool(mask & flag))
                    previous = mask
                if time.monotonic() - last_report > 5:
                    # The receiver can remain open after the mouse sleeps. Re-arm
                    # spying periodically so wake and power cycles recover.
                    mode = call(short, long, mode_index, 2, b"\x00\x00\x00")[0]
                    if mode != 2:
                        call(short, long, mode_index, 1, b"\x02\x00\x00")
                    call(short, long, spy_index, 1, b"\x00\x00\x00")
                    last_report = time.monotonic()
        except Exception as exc:
            emit("disconnected", message=str(exc))
            try:
                if commands.queue and commands.queue[0].get("command") == "quit":
                    return
            except (IndexError, AttributeError):
                pass
            time.sleep(2)
        finally:
            if short:
                short.close()
            if long:
                long.close()


if __name__ == "__main__":
    main()
