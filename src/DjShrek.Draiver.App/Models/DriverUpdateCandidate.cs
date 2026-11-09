using DjShrek.Draiver.App.Services;

namespace DjShrek.Draiver.App.Models;

public sealed class DriverUpdateCandidate
{
    public const string HardwareIdSearchNotice = "Поиск выполнен по Hardware ID; пакет не проверен";
    public const string UnmatchedDeviceNotice = "Не сопоставлено: проверьте вручную";
    public string UpdateId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string Provider { get; init; } = "";
    public string DeviceClass { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string BusType { get; init; } = "";
    public string VendorId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string SubsystemId { get; init; } = "";
    public string RevisionId { get; init; } = "";
    public string HardwareIdentitySummary { get; init; } = "";
    public string MatchType { get; init; } = "Hardware ID";
    public string DeviceName { get; init; } = "Устройство не сопоставлено";
    public string CurrentVersion { get; init; } = "Не сопоставлена";
    public string Compatibility { get; init; } = "Совместимость не проверена";
    public string AvailableVersion { get; init; } = "Не указана в каталоге";
    public string VersionComparison { get; init; } = "Версия не проверена";
    public string DriverVersionDate { get; init; } = "";
    public string Description { get; init; } = "";
    public string SearchQuery { get; init; } = "";
    public string PublishedDate { get; init; } = "";
    public string Evidence { get; init; } = "";
    /// <summary>Origin category: OfficialManufacturer, IndependentCatalog, or SearchDiscovery.</summary>
    public string SourceType { get; init; } = "SearchDiscovery";
    /// <summary>Provenance assessment only; not a package signature verdict.</summary>
    public string SourceTrust { get; init; } = "Unverified";
    public bool IsDownloaded { get; set; }
    public string SourceUrl { get; init; } = "";
    public string PackageUrl { get; set; } = "";
    public string PackageFileName { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string PackageStatus { get; set; } = "Пакет не загружен";
    public string PackageIntegrityStatus { get; set; } = "Не проверено";
    public string PackagePreflightStatus { get; set; } = "Не проверено";
    public string VerifiedInfPath { get; set; } = "";
    public string VerifiedInfVersion { get; set; } = "";
    public string VerifiedPackageRoot { get; set; } = "";
    public DriverPackageIntegrityManifest? VerifiedPackageManifest { get; set; }
}