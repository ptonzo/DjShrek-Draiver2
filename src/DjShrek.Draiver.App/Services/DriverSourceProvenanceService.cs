using System.Globalization;
using System.Net;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Assesses where a driver package link points. This is provenance metadata only:
/// it does not validate Authenticode signatures, package contents, or safety.
/// </summary>
public sealed class DriverSourceProvenanceService
{
    private static readonly IReadOnlyDictionary<string, string[]> VendorDomains = BuildVendorDomains();

    private static IReadOnlyDictionary<string, string[]> BuildVendorDomains()
    {
        var domains = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in new DriverVendorCatalogRegistry().GetAll())
        {
            if (!string.IsNullOrWhiteSpace(entry.DisplayName) &&
                !string.IsNullOrWhiteSpace(entry.SearchDomain))
                domains[entry.DisplayName] = [entry.SearchDomain.Trim().Trim('.').ToLowerInvariant()];
        }

        domains["Hewlett-Packard"] = domains["HP"];
        domains["Hewlett Packard"] = domains["HP"];
        domains["Micro-Star International"] = domains["MSI"];
        domains["Micro-Star"] = domains["MSI"];
        domains["Advanced Micro Devices"] = domains["AMD"];
        domains["NVIDIA Corporation"] = domains["NVIDIA"];
        domains["Intel Corporation"] = domains["Intel"];
        return domains;
    }

    public DriverSourceAssessment Assess(string? manufacturer, string? sourceUrl, string? declaredSourceType = null)
    {
        if (!Uri.TryCreate(sourceUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return new DriverSourceAssessment(
                DriverSourceCategory.InvalidOrMissing,
                "Unverified",
                "Адрес источника отсутствует или имеет неподдерживаемый формат.",
                "");
        }

        var host = NormalizeHost(uri.Host);
        if (host.Length == 0 || IPAddress.TryParse(host, out _))
        {
            return new DriverSourceAssessment(
                DriverSourceCategory.Unverified,
                "Unverified",
                "Источник указан по IP-адресу; принадлежность производителю не подтверждена.",
                host);
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return new DriverSourceAssessment(
                DriverSourceCategory.Unverified,
                "Unverified",
                "Ссылка использует HTTP без шифрования. Официальность источника не подтверждена.",
                host);
        }

        if (string.Equals(declaredSourceType, "IndependentCatalog", StringComparison.OrdinalIgnoreCase))
        {
            return new DriverSourceAssessment(
                DriverSourceCategory.IndependentCatalog,
                "Independent",
                "Независимый каталог. Не является официальным источником производителя.",
                host);
        }

        var vendor = NormalizeManufacturer(manufacturer);
        if (vendor.Length > 0 &&
            VendorDomains.TryGetValue(vendor, out var domains) &&
            domains.Any(domain => IsDomainOrSubdomain(host, domain)))
        {
            return new DriverSourceAssessment(
                DriverSourceCategory.ManufacturerDomain,
                "ManufacturerDomain",
                $"Домен соответствует известному домену производителя {vendor}. Это не подтверждает подпись или подлинность самого файла.",
                host);
        }

        if (VendorDomains.Values.SelectMany(x => x).Any(domain => IsDomainOrSubdomain(host, domain)))
        {
            return new DriverSourceAssessment(
                DriverSourceCategory.OtherManufacturerDomain,
                "Unverified",
                "Домен принадлежит известному производителю, но не совпадает с производителем устройства.",
                host);
        }

        return new DriverSourceAssessment(
            DriverSourceCategory.ThirdPartyOrUnknown,
            "Unverified",
            "Сторонний или неизвестный домен. Не обозначать пакет как официальный.",
            host);
    }

    private static string NormalizeHost(string host)
    {
        var value = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (value.StartsWith("www.", StringComparison.Ordinal))
            value = value[4..];
        return value;
    }

    private static string NormalizeManufacturer(string? manufacturer)
    {
        var value = (manufacturer ?? "").Trim().ToLower(CultureInfo.InvariantCulture);
        return value switch
        {
            "hewlett-packard" or "hewlett packard" => "HP",
            "micro-star international" or "micro-star" => "MSI",
            "advanced micro devices" => "AMD",
            "nvidia corporation" => "NVIDIA",
            "intel corporation" => "Intel",
            _ => VendorDomains.Keys.FirstOrDefault(
                key => value.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? ""
        };
    }

    private static bool IsDomainOrSubdomain(string host, string trustedDomain) =>
        host.Equals(trustedDomain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + trustedDomain, StringComparison.OrdinalIgnoreCase);
}

public enum DriverSourceCategory
{
    InvalidOrMissing,
    Unverified,
    ManufacturerDomain,
    OtherManufacturerDomain,
    IndependentCatalog,
    ThirdPartyOrUnknown
}

public sealed record DriverSourceAssessment(
    DriverSourceCategory Category,
    string TrustLabel,
    string Explanation,
    string Host);
