namespace DjShrek.Draiver.App.Models;

public sealed record SystemMetrics(
    double CpuPercent,
    double MemoryPercent,
    double MemoryUsedGb,
    double MemoryTotalGb,
    double DiskPercent,
    double DiskUsedGb,
    double DiskTotalGb,
    string CpuName,
    string GpuName,
    string Os,
    string Computer,
    string Uptime);
