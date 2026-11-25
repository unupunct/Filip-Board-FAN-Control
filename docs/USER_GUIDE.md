# User guide

## Pages
| Page | Purpose |
|---|---|
| Dashboard | Gauges for CPU/GPU/board temperature and load, one card per detected fan, live chart. |
| Fan Control | Rename fans, choose a mode, manual %, **Verify control**. |
| Fan Curves | Edit a curve, pick the temperature source, hysteresis and ramp delays. |
| Temperature Monitor | Every temperature sensor with now/min/max/avg and a chart. |
| Profiles | Silent, Balanced, Gaming, Performance, Custom; duplicate, rename, import/export, auto-select rules. |
| History & Logs | Stored history charts, CSV export, event log (commands, errors, alerts). |
| Hardware Information | Board, Super I/O chip, every sensor, compatibility notes, diagnostic export. |
| Settings | Safety, tray/startup, units, thresholds, logging, restore defaults. |

## Enabling fan control (opt-in)
1. Run as Administrator.
2. **Settings → uncheck Read-only mode** and confirm.
3. **Fan Control → Verify control...** on a channel. The fan runs at 40 % then 70 % for about 20 seconds and returns to BIOS control. The result says what was measured.
4. Only a *confirmed* channel can be controlled. If the RPM does not follow, the channel stays with the BIOS (Smart Fan 5).
5. Choose a mode and click **Apply**, or build a curve and **Save curve**.

Modes: *Auto* (BIOS), *Manual*, *Curve*, *Quiet*, *Balanced*, *Performance*, *Max*.

## Curves
Drag points; double-click empty space to add; right-click a point to remove. The dashed amber line is the minimum safe speed. Speeds must not fall as temperature rises and should reach 100 % by 90 °C. **Hysteresis** stops the speed dropping until the temperature has fallen by that many degrees; **ramp delays** require the change to persist before it is applied. Temperatures are smoothed; the **Emergency at** value uses the raw reading and forces 100 %.

## Splitters and daisy chains
The motherboard reports one RPM and one control channel per header. Three ARCTIC P14 fans on a splitter appear as a single fan; you cannot read or set them separately. Connect each fan to its own header (CHA_FAN) if you want separate readings. Rename a header to e.g. "Case Fan 1 - ARCTIC P14" on the Fan Control page.

## Emergency
**FULL SPEED (emergency)** sets 100 % on every verified channel. If none is verified it tells you so; the BIOS is still in charge.

## Demo mode
Settings → Demo mode (or run with `--demo`) shows simulated `[MOCK]` data. It is never stored.
