# G502 X LIGHTSPEED button feasibility test

Tested on Windows with the attached LIGHTSPEED receiver `046d:c547`. The probe registers for the receiver's ordinary mouse/keyboard Raw Input and vendor-defined HID collections (`usage page 0xff00`, usages 1 and 2). It only reads events.

## Observed

- With G HUB running, receiver HID collection 2 emitted a 20-byte `11 01 0a ...` report with a distinct button bit changing on press and release. Observed active bits: `0x0001`, `0x0004`, `0x0008`, `0x0010`, `0x0020`, `0x0040`, `0x0080`, `0x0100`, `0x0200`, and `0x0400`. The user confirmed they pressed the extra buttons during this capture.
- `0x0001` coincided with ordinary left click, `0x0004` with middle click, `0x0200` with X2, and `0x0400` with X1. A `0x0100` press coincided with G HUB's virtual Alt+Tab keyboard output.
- After closing G HUB and its agent, independent down/up pairs were still observed for `0x0008`, `0x0010`, `0x0020`, and `0x0040`, as well as left click, X1, and X2. G HUB was restarted afterward.
- With G HUB closed, the user turned the mouse off and on. The mouse then reported mode `1` (onboard), and the independent button-spy reports stopped. Normal mouse input remained available.
- The diagnostic resolved HID++ feature `0x8100` at index `9` and switched to mode `2` (host) in RAM, verifying the read-back. Host mode alone did **not** restore the button-spy reports.
- The diagnostic then resolved HID++ feature `0x8110` at index `10` and called function `1` (`startSpy`). With G HUB still closed, the direct HID listener received independent down/up pairs for `0x0008`, `0x0010`, `0x0020`, `0x0040`, `0x0080`, and `0x0100`, plus the ordinary button bits that were pressed. G HUB was restarted after the capture.

## What this proves

The receiver exposes extra physical button state to a separate Windows application, including after a power cycle with G HUB closed, once the app enables host mode and `startSpy`. A BetterGhub app can react to those observed bits directly rather than trying to infer which button fired from a generated click or keystroke. **The button detection needed for this single-mouse project is feasible.**

## Still to verify

- Map every bit to the exact physical G-button by pressing one labelled control at a time.
- Integrate host-mode and `startSpy` activation into the eventual Windows app and repeat both after reconnect or mouse power cycle.
- Verify suppression of any default mouse action before using a button for a custom macro. Reading a bit alone does not stop Windows from also handling the original click.

The full event captures from this test are `capture.log`, `capture-no-ghub.log`, `capture-power-cycle.log`, `capture-host-no-ghub.log`, and `capture-spy-no-ghub.log` in `docs/captures`. They are local diagnostic files and ignored by Git. Protocol details for `0x8100` were checked against OpenLogi PR #459; the `0x8110.startSpy` command was checked against the G502 X HID++ write-up at https://gist.github.com/myaiexp/ce62498e0d702f2a3b289be58218822c and then verified on this receiver.
