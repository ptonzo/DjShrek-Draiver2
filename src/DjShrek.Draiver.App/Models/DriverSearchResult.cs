namespace DjShrek.Draiver.App.Models;

/// <summary>
/// Manufacturer page discovered for a device. This is not an installable driver package.
/// </summary>
public sealed class DriverSearchResult
{
    public string DeviceName { get; init; } = "";
    public string DeviceClass { get; init; } = "";
    public string CurrentProvider { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public string PageTitle { get; init; } = "";
    public string Snippet { get; init; } = "";
    public string Host { get; init; } = "";
    public string Url { get; init; } = "";
    public string Verification { get; init; } = "";
}
