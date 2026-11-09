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
        var isModelPack = candidate.CandidateScope == "ModelPack" ||
                          candidate.DeviceClass.Contains("OEM", StringComparison.OrdinalIgnoreCase) ||
                          candidate.MatchType.Contains("OEM", StringComparison.OrdinalIgnoreCase);
        var isPortal = candidate.CandidateScope == "SupportPortal" ||
                       (string.IsNullOrWhiteSpace(candidate.PackageUrl) &&
                        candidate.SourceTrust.Equals("OfficialSupportPortal", StringComparison.OrdinalIgnoreCase));

        candidate.CandidateScope = isPortal ? "SupportPortal" :
            isModelPack ? "ModelPack" :
            candidate.MatchType.Equals("Hardware ID", StringComparison.OrdinalIgnoreCase)
                ? "DeviceExact" :
            candidate.MatchType.Equals("Compatible ID", StringComparison.OrdinalIgnoreCase)
                ? "DeviceCompatible" : "SearchDiscovery";

        candidate.EvidenceLevel = candidate.CandidateScope switch
        {
            "DeviceExact" => "Точный Hardware ID; пакет требует проверки INF/CAT",
            "DeviceCompatible" => "INF/каталог указывает Compatible ID; точное совпадение устройства не доказано",
            "ModelPack" => "Пакет модели OEM; соответствие отдельному устройству не подтверждено",
            "SupportPortal" => "Официальный портал; конкретный пакет не получен",
            "SearchDiscovery" => "Найден результат каталога/поиска; связь с устройством не доказана",
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
        var device = scope switch
        {
            "ModelPack" or "SupportPortal" =>
                Normalize(candidate.Manufacturer + "|" + candidate.Model),
            "SearchDiscovery" =>
                Normalize(!string.IsNullOrWhiteSpace(candidate.InstanceId)
                    ? candidate.InstanceId
                    : candidate.HardwareId + "|" + candidate.DeviceName + "|" +
                      candidate.Manufacturer + "|" + candidate.Model),
            _ => Normalize(!string.IsNullOrWhiteSpace(candidate.InstanceId)
                ? candidate.InstanceId : candidate.HardwareId + "|" + candidate.DeviceName)
        };
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
            "DeviceCompatible" => 250,
            "ModelPack" => 200,
            "SupportPortal" => 50,
            "SearchDiscovery" => 100,
            _ => 0
        };
        if (candidate.SourceCategory == nameof(DriverSourceCategory.ManufacturerDomain)) score += 80;
        else if (candidate.SourceCategory == nameof(DriverSourceCategory.IndependentCatalog)) score += 15;
        if (!string.IsNullOrWhiteSpace(candidate.PackageUrl)) score += 25;
        if (!string.IsNullOrWhiteSpace(candidate.AvailableVersion) &&
            !candidate.AvailableVersion.Contains("не указ", StringComparison.OrdinalIgnoreCase) &&
            !candidate.AvailableVersion.Contains("не получ", StringComparison.OrdinalIgnoreCase))
            score += 15;
        if (candidate.IsDownloaded) score += 5;
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
        if (text.Contains("ARM64", StringComparison.OrdinalIgnoreCase)) return "ARM64";
        if (text.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("64-bit", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("64 bit", StringComparison.OrdinalIgnoreCase)) return "x64";
        if (text.Contains("x86", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("32-bit", StringComparison.OrdinalIgnoreCase)) return "x86";
        return "";
    }

    private static string NormalizeUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return Normalize(value);
        return new UriBuilder(uri)
        {
            Fragment = ""
        }.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string Normalize(string value) =>
        string.Join(" ", (value ?? "").Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
