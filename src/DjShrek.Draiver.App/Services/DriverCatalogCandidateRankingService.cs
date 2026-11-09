using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Normalizes provenance and ranks catalog candidates without converting a search hit
/// into a compatibility or publisher-verification verdict.
/// </summary>
public sealed class DriverCatalogCandidateRankingService
{
    public IReadOnlyList<DriverUpdateCandidate> RankAndDeduplicate(
        IEnumerable<DriverUpdateCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var prepared = candidates.Where(x => x is not null).ToArray();
        foreach (var candidate in prepared)
            EnrichEvidence(candidate);

        return prepared
            .GroupBy(BuildIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(Score)
                .ThenByDescending(x => x.PublishedDate, StringComparer.Ordinal)
                .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(Score)
            .ThenBy(x => x.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void EnrichEvidence(DriverUpdateCandidate candidate)
    {
        var isModelPack = candidate.DeviceClass.Contains("OEM", StringComparison.OrdinalIgnoreCase) ||
                          candidate.MatchType.Contains("OEM", StringComparison.OrdinalIgnoreCase);
        var isPortal = string.IsNullOrWhiteSpace(candidate.PackageUrl) &&
                       candidate.SourceTrust.Equals("OfficialSupportPortal", StringComparison.OrdinalIgnoreCase);

        candidate.CandidateScope = isPortal ? "SupportPortal" :
            isModelPack ? "ModelPack" :
            candidate.MatchType.Equals("Hardware ID", StringComparison.OrdinalIgnoreCase)
                ? "DeviceExact" : "DeviceCompatible";

        candidate.EvidenceLevel = candidate.CandidateScope switch
        {
            "DeviceExact" => "Точный Hardware ID; пакет требует проверки INF/CAT",
            "DeviceCompatible" => "Совпадение Compatible ID или поисковая зацепка; INF не подтверждён",
            "ModelPack" => "Пакет модели OEM; соответствие отдельному устройству не подтверждено",
            "SupportPortal" => "Официальный портал; конкретный пакет не получен",
            _ => "Недостаточно сведений"
        };

        candidate.PublisherVerificationStatus =
            candidate.SourceTrust.Contains("Unverified", StringComparison.OrdinalIgnoreCase) ||
            candidate.SourceTrust.Equals("OfficialSupportPortal", StringComparison.OrdinalIgnoreCase)
                ? "Не проверена для пакета"
                : candidate.PublisherVerificationStatus;

        candidate.PackageAcquisitionStatus = !string.IsNullOrWhiteSpace(candidate.PackageUrl)
            ? (candidate.IsDownloaded ? "Загружен; требуется проверка состава и подписи" : "Ссылка получена; пакет не загружен")
            : "Прямая ссылка на пакет отсутствует";

        if (string.IsNullOrWhiteSpace(candidate.OperatingSystemTarget))
            candidate.OperatingSystemTarget = InferOperatingSystem(candidate.Description);
        if (string.IsNullOrWhiteSpace(candidate.ArchitectureTarget))
            candidate.ArchitectureTarget = InferArchitecture(candidate.Description);
    }

    private static string BuildIdentityKey(DriverUpdateCandidate candidate)
    {
        var scope = candidate.CandidateScope;
        var device = scope == "ModelPack" || scope == "SupportPortal"
            ? Normalize(candidate.Manufacturer + "|" + candidate.Model)
            : Normalize(!string.IsNullOrWhiteSpace(candidate.InstanceId)
                ? candidate.InstanceId : candidate.HardwareId + "|" + candidate.DeviceName);
        var artifact = !string.IsNullOrWhiteSpace(candidate.PackageUrl)
            ? NormalizeUri(candidate.PackageUrl)
            : NormalizeUri(candidate.SourceUrl);
        return scope + "|" + device + "|" + artifact;
    }

    private static int Score(DriverUpdateCandidate candidate)
    {
        var score = candidate.CandidateScope switch
        {
            "DeviceExact" => 500,
            "DeviceCompatible" => 300,
            "ModelPack" => 200,
            "SupportPortal" => 50,
            _ => 0
        };
        if (candidate.SourceType == "OfficialManufacturer") score += 100;
        if (!string.IsNullOrWhiteSpace(candidate.PackageUrl)) score += 40;
        if (!string.IsNullOrWhiteSpace(candidate.AvailableVersion) &&
            !candidate.AvailableVersion.Contains("не указ", StringComparison.OrdinalIgnoreCase) &&
            !candidate.AvailableVersion.Contains("не получ", StringComparison.OrdinalIgnoreCase))
            score += 20;
        if (candidate.IsDownloaded) score += 10;
        return score;
    }

    private static string InferOperatingSystem(string text)
    {
        if (text.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Win11", StringComparison.OrdinalIgnoreCase)) return "Windows 11";
        if (text.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Win10", StringComparison.OrdinalIgnoreCase)) return "Windows 10";
        return "";
    }

    private static string InferArchitecture(string text)
    {
        if (text.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("64-bit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("64 bit", StringComparison.OrdinalIgnoreCase)) return "x64";
        if (text.Contains("ARM64", StringComparison.OrdinalIgnoreCase)) return "ARM64";
        if (text.Contains("x86", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("32-bit", StringComparison.OrdinalIgnoreCase)) return "x86";
        return "";
    }

    private static string NormalizeUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return Normalize(value);
        return new UriBuilder(uri)
        {
            Fragment = "",
            Query = ""
        }.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string Normalize(string value) =>
        string.Join(" ", (value ?? "").Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
