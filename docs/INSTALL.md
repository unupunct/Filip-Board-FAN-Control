# Installation

## Requirements
- Windows 10 or 11, 64-bit. No .NET install needed (the app is self-contained).
- Administrator rights for motherboard sensors and fan control.

## Installer
1. Download `FilipBoardFanControl-Setup-1.0.0.exe` from the Releases page.
2. Run it and accept the UAC prompt. Windows SmartScreen may warn because the installer is not code-signed; choose *More info → Run anyway* only if you trust the source.
3. Start **Filip Board FAN Control** from the Start menu.
4. If the banner says *Limited access*, click **Restart as Administrator**.

## Where data lives
`%LOCALAPPDATA%\FilipBoardFanControl\data.db` (SQLite: settings, profiles, verification records, history, log).
Uninstalling asks whether to delete it.

## First run on the Gigabyte Z370 AORUS Gaming 7
1. Open **Hardware Information**. Confirm the board name and a Super I/O chip are listed.
2. Click **Export diagnostic report** and keep it (useful for bug reports).
3. Leave read-only mode on for a day and compare the readings with BIOS / GIGABYTE System Information Viewer.
4. Only then follow the *Verify* steps in the [user guide](USER_GUIDE.md).

## Portable use
Copy `FilipBoardFanControl.exe` anywhere and run it. Settings still go to `%LOCALAPPDATA%`.

## Diagnostic probe (optional)
`dotnet run --project src/FilipBoard.Probe -c Release` (Administrator). Read-only; writes `probe-report.json`.
