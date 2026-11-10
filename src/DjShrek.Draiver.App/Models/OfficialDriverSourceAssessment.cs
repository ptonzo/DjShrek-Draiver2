namespace DjShrek.Draiver.App.Models;

public sealed class OfficialDriverSourceAssessment
{
    public bool IsOfficial { get; init; }
    public string Vendor { get; init; } = "";
    public string Host { get; init; } = "";
    public string TrustLevel { get; init; } = "Не подтверждён";
    public string Explanation { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
