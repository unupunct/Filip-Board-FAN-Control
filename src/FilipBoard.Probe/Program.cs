// Filip Board Fan Control - Phase 1 READ-ONLY hardware probe.
// Never writes to any fan/control channel. Produces probe-report.json + console summary.
using System.Security.Principal;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
Console.WriteLine("Filip Board Fan Control - read-only probe");
Console.WriteLine($"Elevated: {admin}  (SuperIO/EC access needs Administrator; without it most sensors will be missing)");

var pc = new Computer
{
    IsMotherboardEnabled = true,
    IsCpuEnabled = true,
    IsGpuEnabled = true,
    IsControllerEnabled = true,
};
pc.Open();

var report = new List<SensorRow>();
void Walk(IHardware hw, int depth)
{
    hw.Update();
    var pad = new string(' ', depth * 2);
    Console.WriteLine($"{pad}[{hw.HardwareType}] {hw.Name}");
    foreach (var s in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
    {
        // Control sensors: only the mode/limits are read; nothing is ever written.
        var row = new SensorRow(hw.Name, hw.HardwareType.ToString(), s.SensorType.ToString(), s.Name,
            s.Identifier.ToString(), s.Value, s.Control?.ControlMode.ToString(),
            s.Control?.SoftwareValue, s.Control?.MinSoftwareValue, s.Control?.MaxSoftwareValue);
        report.Add(row);
        if (s.SensorType is SensorType.Temperature or SensorType.Fan or SensorType.Control)
            Console.WriteLine($"{pad}  {row.SensorType,-11} {row.Name,-28} = {row.Value?.ToString("0.#") ?? "n/a"}" +
                              (row.ControlMode != null ? $"  mode={row.ControlMode} (channel exposed)" : ""));
    }
    foreach (var sub in hw.SubHardware) Walk(sub, depth + 1);
}
foreach (var hw in pc.Hardware) Walk(hw, 0);

var fans = report.Count(r => r.SensorType == "Fan");
var controls = report.Count(r => r.SensorType == "Control" && r.ControlMode != null);
Console.WriteLine($"\nSummary: {fans} fan RPM sensors, {controls} control channels exposed by the library.");
Console.WriteLine("NOTE: 'exposed' does NOT mean trusted. Control stays disabled until a verified write/readback test passes.");

var path = Path.Combine(AppContext.BaseDirectory, "probe-report.json");
File.WriteAllText(path, JsonSerializer.Serialize(new { utc = DateTime.UtcNow, elevated = admin, sensors = report },
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Report: {path}");
pc.Close();
if (!Console.IsInputRedirected) { Console.WriteLine("Press any key to close..."); Console.ReadKey(true); }

record SensorRow(string Hardware, string HardwareType, string SensorType, string Name, string Identifier,
    float? Value, string? ControlMode, float? ControlSoftware, float? ControlMin, float? ControlMax);

