namespace DjShrek.Draiver.App.Models;

public sealed class StructuredDriverCatalogEntry
{
    public string Vendor { get; init; } = "";
    public string Model { get; init; } = "";
    public string OperatingSystem { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string Version { get; init; } = "";
    public string ReleaseDate { get; init; } = "";
    public string PackageUrl { get; init; } = "";
    public string DetailsUrl { get; init; } = "";
    public IReadOnlyList<string> HardwareIds { get; init; } = Array.Empty<string>();
    public string SourceFormat { get; init; } = "";
}
