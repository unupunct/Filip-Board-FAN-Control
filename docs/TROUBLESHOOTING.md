# Troubleshooting

**No CPU temperatures or fans, banner says "Limited access".**
Run as Administrator (button in the banner). Sensor access to the Super I/O chip needs elevation.

**Fans are listed but "Monitoring only (no control channel)".**
The driver exposes RPM but no writable channel for that header. Use BIOS Smart Fan 5 or GIGABYTE System Information Viewer to set speeds.

**Verify says "Fan did not follow the command".**
The firmware is overriding the channel, the header is in a mode the chip does not honour, or the fan is DC/PWM-mismatched. Control stays disabled. Set the header's mode (Auto/PWM/Voltage) in BIOS and retry.

**Verify says "No fan RPM detected".**
Check the cable, the header, and that the fan is not on a splitter without a tach signal.

**Three fans show as one.**
They share a header. See *Splitters* in the user guide.

**Fan speeds look wrong after the app crashed or was killed.**
Exit the app normally (tray → Exit) or reboot: the BIOS regains control on reboot.

**Another monitoring tool is running.**
Two programs touching the same chip can conflict. Close HWiNFO, SIV, FanControl or GIGABYTE tools while using this app.

**Antivirus or Windows flags the driver.**
Sensor access uses LibreHardwareMonitor's signed kernel driver. Allow it only if you trust the build you installed.

**Reset everything.**
Settings → Restore defaults, or delete `%LOCALAPPDATA%\FilipBoardFanControl`.

**Report a problem.**
Hardware Information → Export diagnostic report, and attach it to a GitHub issue (it contains your board model and sensor values).
