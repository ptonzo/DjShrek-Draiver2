using System.Text.Json.Serialization;

namespace DjShrek.Draiver.App.Models;

public enum DriverCandidateState
{
    Discovered, Downloading, Downloaded, Staged, Applicable, Incompatible,
    SignatureRejected, ReadyForInstall, Installed, Failed, Archived
}

public sealed class DriverUpdateRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = "";
    public string DeviceInstanceId { get; set; } = "";
    public string[] HardwareIds { get; set; } = Array.Empty<string>();
    public string Provider { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string CandidateVersion { get; set; } = "";
    public string CandidateDate { get; set; } = "";
    public string PackageTitle { get; set; } = "";
    public string PackageUrl { get; set; } = "";
    public string SourcePage { get; set; } = "";
    public string PackagePath { get; set; } = "";
    public string StagingDirectory { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string WindowsTarget { get; set; } = "";
    public string SignatureStatus { get; set; } = "Не проверена";
    public string Compatibility { get; set; } = "Не проверена";
    public string State { get; set; } = DriverCandidateState.Discovered.ToString();
    public string[] Reasons { get; set; } = Array.Empty<string>();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public bool IsActionable => State is nameof(DriverCandidateState.ReadyForInstall) or nameof(DriverCandidateState.Applicable);
}

public sealed class DriverUpdateCatalogDocument
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<DriverUpdateRecord> Records { get; set; } = new();
}

public sealed class DriverBackupRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public int PackageCount { get; set; }
    public long TotalBytes { get; set; }
    public string ManifestPath { get; set; } = "";
    public string State { get; set; } = "Создана";
    public List<DriverBackupPackage> Packages { get; set; } = new();
}

public sealed class DriverBackupPackage
{
    public string InfName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string DriverDate { get; set; } = "";
}


public sealed class DriverInstallationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string DeviceName { get; set; } = "";
    public string DeviceInstanceId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string PreviousVersion { get; set; } = "";
    public string RequestedVersion { get; set; } = "";
    public string InfPath { get; set; } = "";
    public string CandidateId { get; set; } = "";
    public string Outcome { get; set; } = "Не определён";
    public string RebootStatus { get; set; } = "Не определён";
    public string Details { get; set; } = "";
}
