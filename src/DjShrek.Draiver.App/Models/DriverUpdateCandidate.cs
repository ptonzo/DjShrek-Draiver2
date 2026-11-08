namespace DjShrek.Draiver.App.Models;

public sealed class DriverUpdateCandidate
{
    public string UpdateId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string Provider { get; init; } = "";
    public string DeviceClass { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public string DeviceName { get; init; } = "Устройство не сопоставлено";
    public string CurrentVersion { get; init; } = "Не сопоставлена";
    public string Compatibility { get; init; } = "Предложено Windows Update";
    public string AvailableVersion { get; init; } = "Не указана в каталоге";
    public string DriverVersionDate { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsDownloaded { get; init; }
    public string SourceUrl { get; init; } = "";
}
