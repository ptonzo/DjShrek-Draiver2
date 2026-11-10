namespace DjShrek.Draiver.App.Models;

public sealed class DriverStorePackage
{
    public string PublishedInfName { get; set; } = "";
    public string OriginalInfName { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string ClassGuid { get; set; } = "";
    public string DriverVersion { get; set; } = "";
    public string SignerName { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public bool IsInbox { get; set; }
    public bool IsThirdParty { get; set; }
    public bool IsUsedByPresentDevice { get; set; }
    public string RetentionAssessment { get; set; } = "Не оценено";
    public string RetentionGuidance { get; set; } = "";
    public List<string> DeviceNames { get; set; } = new();
    public List<string> DeviceInstanceIds { get; set; } = new();
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DriverStoreInventorySnapshot
{
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public string WindowsVersion { get; set; } = "";
    public int TotalPackages { get; set; }
    public int ThirdPartyPackages { get; set; }
    public int UsedPackages { get; set; }
    public int UnusedThirdPartyPackages { get; set; }
    public List<DriverStorePackage> Packages { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
