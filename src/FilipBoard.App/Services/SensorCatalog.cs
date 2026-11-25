using FilipBoard.Core;

namespace FilipBoard.App.Services;

/// <summary>Classifies raw readings into the roles the UI and automation care about.</summary>
public static class SensorCatalog
{
    public static IEnumerable<SensorReading> Temps(IEnumerable<SensorReading> r) => r.Where(x => x.Kind == SensorKind.Temperature);
    public static IEnumerable<SensorReading> Fans(IEnumerable<SensorReading> r) =>
        r.Where(x => x.Kind == SensorKind.FanRpm && !IsGpu(x));

    private static bool IsGpu(SensorReading r) =>
        r.Id.Contains("/gpu", StringComparison.OrdinalIgnoreCase) || r.Id.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase);
    private static bool IsCpu(SensorReading r) => r.Id.Contains("cpu", StringComparison.OrdinalIgnoreCase);

    public static SensorReading? CpuTemp(IReadOnlyList<SensorReading> r) =>
        r.FirstOrDefault(x => x.Kind == SensorKind.Temperature && IsCpu(x) && x.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) && x.Value != null)
        ?? r.FirstOrDefault(x => x.Kind == SensorKind.Temperature && IsCpu(x) && x.Name.Contains("Max", StringComparison.OrdinalIgnoreCase) && x.Value != null)
        ?? r.FirstOrDefault(x => x.Kind == SensorKind.Temperature && IsCpu(x) && x.Value != null);

    public static SensorReading? GpuTemp(IReadOnlyList<SensorReading> r) =>
        r.FirstOrDefault(x => x.Kind == SensorKind.Temperature && IsGpu(x) && x.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) && x.Value != null)
        ?? r.FirstOrDefault(x => x.Kind == SensorKind.Temperature && IsGpu(x) && x.Value != null);

    public static SensorReading? BoardTemp(IReadOnlyList<SensorReading> r) =>
        r.FirstOrDefault(x => x.Kind == SensorKind.Temperature && (x.Id.Contains("/lpc/") || x.Id.Contains("mock/temp/board")) &&
                              (x.Name.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("System", StringComparison.OrdinalIgnoreCase)) && x.Value != null)
        ?? r.FirstOrDefault(x => x.Kind == SensorKind.Temperature && x.Id.Contains("/lpc/") && x.Value != null);

    public static SensorReading? CpuLoad(IReadOnlyList<SensorReading> r) =>
        r.FirstOrDefault(x => x.Kind == SensorKind.Load && IsCpu(x) && x.Name.Contains("Total", StringComparison.OrdinalIgnoreCase));

    public static SensorReading? GpuLoad(IReadOnlyList<SensorReading> r) =>
        r.FirstOrDefault(x => x.Kind == SensorKind.Load && IsGpu(x) && x.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
        ?? r.FirstOrDefault(x => x.Kind == SensorKind.Load && x.Id.Contains("mock/load/gpu"));
}
