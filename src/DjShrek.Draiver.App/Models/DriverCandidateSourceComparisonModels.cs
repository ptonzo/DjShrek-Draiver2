namespace DjShrek.Draiver.App.Models;

public sealed class DriverCandidateSourceEntry
{
    public DriverCandidateForRanking Candidate { get; init; } = new();
    public string SourcePage { get; init; } = "";
    public string PackageUrl { get; init; } = "";
    public string Sha256 { get; init; } = "";
}

public sealed class DriverCandidateSourceComparison
{
    public string CandidateId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Version { get; init; } = "";
    public string PackageUrl { get; init; } = "";
    public string SourcePage { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public int Score { get; init; }
    public bool IsEligible { get; init; }
    public string Confidence { get; init; } = "";
    public string MatchType { get; init; } = "";
    public string SourceTrust { get; init; } = "";
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public int DuplicateSourceCount { get; init; }
}
