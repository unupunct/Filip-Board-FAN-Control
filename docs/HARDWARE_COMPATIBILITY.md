# Hardware compatibility report

## Status: target hardware NOT yet verified
v1.0.0 was developed on an HP PC (i7-6700, RTX 4060), **not** the Gigabyte Z370 AORUS Gaming 7. Everything below about the Gigabyte board is expected, not confirmed. Please run the app (or `src/FilipBoard.Probe`) elevated on that board and open an issue with the diagnostic report.

## Interfaces evaluated
| Interface | Read | Write | Verdict |
|---|---|---|---|
| LibreHardwareMonitorLib 0.9.6 | Yes (Administrator needed for Super I/O and CPU) | Only where a chip driver exposes control channels | **Primary backend** |
| OpenHardwareMonitor | Same chips, unmaintained | Limited | Not used |
| GIGABYTE System Information Viewer | GUI only, no public API | GUI only | Documented fallback |
| BIOS Smart Fan 5 | n/a | Yes, firmware level | Safest fallback; survives crashes |

## Expected for this board (unverified)
- Super I/O is believed to be an ITE IT8686E (the app reads and shows the real chip name).
- LibreHardwareMonitor added "IT8686E Controls" in v0.9.3. No confirmed report was found of working PWM control on this exact board; some Gigabyte boards expose RPM only.
- Three P14 fans on a splitter appear as one tach and one control channel.

## How the app decides to write
1. Read-only mode is off (user choice), and the process is elevated.
2. The channel passed `FanWriteVerifier`: 40 % then 70 % commands with RPM rising by at least 10 %. The record is bound to the board/chip fingerprint.
3. Otherwise the channel is left to the BIOS and the UI explains why.

## Validation checklist (to complete on the Z370)
- [ ] Board and Super I/O chip identified
- [ ] Temperature sensors listed (CPU, board, VRM/chipset if exposed)
- [ ] Fan RPM per header
- [ ] Control channels exposed
- [ ] Verify passes on each channel; PWM vs DC mode noted
- [ ] RPM read back after a speed change
- [ ] Channel mapping (header to RPM sensor) correct
- [ ] Recovery to BIOS control after exit
- [ ] Behaviour when a sensor disappears

## Confirmed on a real Gigabyte Z370 AORUS Gaming 7 (v1.0.1)

- Super I/O chips detected by LibreHardwareMonitor: **ITE IT8686E** (5 fan channels) and **ITE IT8791E/IT8792E/IT8795E** (3 fan channels). Fan RPM and temperatures read correctly while running as Administrator.
- By matching RPM against the BIOS (Smart Fan 5), these channels are *probably*: IT8686E Fan #1 = CPU_OPT, IT8686E Fan #2 = SYS_FAN1, IT8792E Fan #3 = SYS_FAN4. The app labels them that way on this board; a name you set always wins.
- IT8686E exposes no readable duty cycle (shown as n/a). Reported percentages on the IT8792E did not match the BIOS curves and are not used for mapping.
- Headers that read 0 RPM in the BIOS (CPU_FAN, SYS_FAN2, SYS_FAN3 in the tested setup) also read 0 in the app; SYS_FAN2 is off below 90 C by its BIOS curve.
- **Fan control (write) is still unverified** on this board. A draft profile "BIOS replica (draft)" is created, but nothing is written until a channel passes the on-board verification.
