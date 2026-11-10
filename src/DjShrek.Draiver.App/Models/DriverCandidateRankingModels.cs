namespace DjShrek.Draiver.App.Models;

public sealed class DriverCandidateForRanking
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Version { get; set; } = "";
    public string[] HardwareIds { get; set; } = Array.Empty<string>();
    public string[] CompatibleIds { get; set; } = Array.Empty<string>();
    public string Architecture { get; set; } = "";
    public int? MinimumWindowsBuild { get; set; }
    public int? MaximumWindowsBuild { get; set; }
    public bool? CatalogSignatureValid { get; set; }
    public bool IsOfficialSource { get; set; }
}

public sealed class DriverCandidateRanking
{
    public string CandidateId { get; set; } = "";
    public string Title { get; set; } = "";
    public int Score { get; set; }
    public bool IsEligible { get; set; }
    public string Confidence { get; set; } = "";
    public string MatchType { get; set; } = "";
    public List<string> Reasons { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
