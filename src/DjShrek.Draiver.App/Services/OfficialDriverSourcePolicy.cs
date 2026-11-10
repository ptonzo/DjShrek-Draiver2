using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Central allowlist for OEM source identity. Uses exact DNS suffix boundaries,
/// never substring matching, and assesses both the landing page and package URL.
/// </summary>
public sealed class OfficialDriverSourcePolicy
{
    private static readonly IReadOnlyDictionary<string, string[]> VendorDomains =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Acer"] = new[] { "acer.com" },
            ["AMD"] = new[] { "amd.com" },
            ["ASRock"] = new[] { "asrock.com" },
            ["ASUS"] = new[] { "asus.com" },
            ["Broadcom"] = new[] { "broadcom.com" },
            ["Dell"] = new[] { "dell.com" },
            ["Gigabyte"] = new[] { "gigabyte.com" },
            ["HP"] = new[] { "hp.com", "hpe.com" },
            ["Intel"] = new[] { "intel.com" },
            ["Lenovo"] = new[] { "lenovo.com" },
            ["MediaTek"] = new[] { "mediatek.com" },
            ["MSI"] = new[] { "msi.com" },
            ["NVIDIA"] = new[] { "nvidia.com" },
            ["Qualcomm"] = new[] { "qualcomm.com" },
            ["Realtek"] = new[] { "realtek.com" }
        };

    public OfficialDriverSourceAssessment Assess(string? sourcePage, string? packageUrl,
        string? claimedVendor = null)
    {
        var warnings = new List<string>();
        if (!TryGetHttpsHost(sourcePage, out var pageHost))
            return Rejected("", "", "Страница источника отсутствует или не использует HTTPS.");

        var pageVendor = FindVendor(pageHost);
        if (pageVendor is null)
            return Rejected(pageHost, "", "Домен страницы отсутствует в списке официальных производителей.");

        if (!TryGetHttpsHost(packageUrl, out var packageHost))
            return Rejected(pageHost, pageVendor, "Ссылка на пакет отсутствует или не использует HTTPS.");

        var packageVendor = FindVendor(packageHost);
        if (packageVendor is null)
            return Rejected(packageHost, pageVendor, "Домен файла не входит в список официальных доменов.");

        if (!string.Equals(pageVendor, packageVendor, StringComparison.OrdinalIgnoreCase))
            return Rejected(packageHost, pageVendor,
                $"Страница принадлежит {pageVendor}, а файл размещён на домене другого производителя ({packageVendor}).");

        if (!string.IsNullOrWhiteSpace(claimedVendor) &&
            !VendorNameMatches(claimedVendor, pageVendor))
            warnings.Add($"Заявленный производитель «{claimedVendor}» не совпадает с владельцем домена «{pageVendor}».");

        return new OfficialDriverSourceAssessment
        {
            IsOfficial = true,
            Vendor = pageVendor,
            Host = packageHost,
            TrustLevel = warnings.Count == 0 ? "Официальный домен подтверждён" : "Домен подтверждён, метаданные требуют внимания",
            Explanation = $"Страница и пакет размещены на HTTPS-доменах производителя {pageVendor}. Это подтверждает источник, но не совместимость и не подпись драйвера.",
            Warnings = warnings
        };
    }

    public IReadOnlyList<string> GetSupportedVendors() =>
        VendorDomains.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    private static OfficialDriverSourceAssessment Rejected(string host, string vendor, string reason) =>
        new()
        {
            IsOfficial = false,
            Host = host,
            Vendor = vendor,
            TrustLevel = "Источник не подтверждён",
            Explanation = reason,
            Warnings = new[] { reason }
        };

    private static bool TryGetHttpsHost(string? value, out string host)
    {
        host = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host))
            return false;
        host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        return true;
    }

    private static string? FindVendor(string host)
    {
        foreach (var pair in VendorDomains)
            if (pair.Value.Any(domain => host == domain || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
                return pair.Key;
        return null;
    }

    private static bool VendorNameMatches(string claimed, string actual)
    {
        var normalized = claimed.Trim().ToLowerInvariant();
        if (normalized.Contains("hewlett") || normalized == "hewlett-packard") normalized = "hp";
        if (normalized.Contains("micro-star")) normalized = "msi";
        return normalized == actual.ToLowerInvariant();
    }
}
