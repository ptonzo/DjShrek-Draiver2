using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Reads Lenovo's public SCCM driver-pack catalog. This catalog describes
/// model-level packs; it does not prove that every INF inside a pack applies
/// to every device. Individual INF/CAT inspection remains mandatory.
/// </summary>
public sealed class LenovoDriverPackCatalogService
{
    public const string CatalogUrl = "https://download.lenovo.com/cdrt/td/catalogv2.xml";
    private const long MaxCatalogBytes = 64L * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    static LenovoDriverPackCatalogService()
    {
        Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DjShrek-Draiver", "1.8"));
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/xml,text/xml;q=0.9");
    }

    public async Task<IReadOnlyList<OemDriverPackCandidate>> FindModelPacksAsync(
        string machineType,
        string operatingSystem,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(machineType))
            throw new ArgumentException("Не указан Machine Type Lenovo (MTM).", nameof(machineType));
        if (string.IsNullOrWhiteSpace(operatingSystem))
            throw new ArgumentException("Не указана версия Windows.", nameof(operatingSystem));

        using var response = await Client.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureOfficialFinalUri(response.RequestMessage?.RequestUri);

        if (response.Content.Headers.ContentLength is > MaxCatalogBytes)
            throw new InvalidDataException("Каталог Lenovo превышает допустимый размер 64 МБ.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bounded = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (bounded.Length + read > MaxCatalogBytes)
                throw new InvalidDataException("Каталог Lenovo превышает допустимый размер 64 МБ.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        bounded.Position = 0;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCatalogBytes,
            IgnoreComments = true
        };
        using var reader = XmlReader.Create(bounded, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var wantedType = Normalize(machineType);
        var wantedOs = NormalizeOs(operatingSystem);
        var results = new List<OemDriverPackCandidate>();

        foreach (var model in document.Descendants().Where(x => x.Name.LocalName == "Model"))
        {
            var modelName = (string?)model.Attribute("name") ?? "";
            var typeMatches = model.Descendants()
                .Where(x => x.Name.LocalName == "Type")
                .Select(x => Normalize(x.Value))
                .Any(x => x.Equals(wantedType, StringComparison.OrdinalIgnoreCase));
            if (!typeMatches) continue;

            foreach (var pack in model.Elements().Where(x => x.Name.LocalName == "SCCM"))
            {
                var packageUrl = pack.Value.Trim();
                if (!TryOfficialPackageUri(packageUrl, out var uri)) continue;

                // Lenovo catalogv2.xml uses "os" for the OS family (win10/win11)
                // and "version" for the Windows release/build selector (for example 1909).
                // The latter is NOT the driver package version.
                var catalogOs = (string?)pack.Attribute("os") ?? "";
                if (string.IsNullOrWhiteSpace(catalogOs) || !OsMatches(catalogOs, wantedOs))
                    continue;

                var osBuild = (string?)pack.Attribute("version") ?? "";
                var packageVersion = FirstNonEmpty(
                    (string?)pack.Attribute("packageVersion"),
                    string.IsNullOrWhiteSpace(osBuild)
                        ? "Каталог не указывает версию пакета"
                        : $"Версия ОС в каталоге: {osBuild}; версия пакета не указана");
                var releaseDate = FirstNonEmpty(
                    (string?)pack.Attribute("date"),
                    (string?)pack.Attribute("releaseDate"),
                    "");

                results.Add(new OemDriverPackCandidate(
                    "Lenovo",
                    modelName,
                    machineType.Trim(),
                    catalogOs.Equals("win10", StringComparison.OrdinalIgnoreCase) ? "Windows 10" :
                    catalogOs.Equals("win11", StringComparison.OrdinalIgnoreCase) ? "Windows 11" : catalogOs,
                    "catalogv2.xml",
                    packageVersion,
                    releaseDate,
                    uri,
                    CatalogUrl,
                    "Точное совпадение Machine Type в Lenovo catalogv2.xml; совместимость отдельных INF ещё не проверена"));
            }
        }

        return results
            .DistinctBy(x => x.PackageUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => ParseDate(x.ReleaseDate))
            .ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void EnsureOfficialFinalUri(Uri? uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("download.lenovo.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Каталог перенаправлен с официального узла Lenovo. Запрос остановлен.");
    }

    private static bool TryOfficialPackageUri(string value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !(parsed.Host.Equals("download.lenovo.com", StringComparison.OrdinalIgnoreCase) ||
              parsed.Host.EndsWith(".lenovo.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        var extension = Path.GetExtension(parsed.AbsolutePath);
        if (!new[] { ".exe", ".zip", ".cab" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;

        uri = parsed;
        return true;
    }

    private static string Normalize(string value) =>
        new(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string NormalizeOs(string value) =>
        value.Replace("Windows", "Win", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();

    private static bool OsMatches(string catalogValue, string requestedValue)
    {
        var catalog = NormalizeOs(catalogValue);
        var requested = NormalizeOs(requestedValue);
        if (catalog is "win10" or "windows10")
            return requested.Contains("windows10", StringComparison.OrdinalIgnoreCase) ||
                   requested.Contains("win10", StringComparison.OrdinalIgnoreCase);
        if (catalog is "win11" or "windows11")
            return requested.Contains("windows11", StringComparison.OrdinalIgnoreCase) ||
                   requested.Contains("win11", StringComparison.OrdinalIgnoreCase);
        return requested.Contains(catalog, StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

    private static DateTime ParseDate(string value) =>
        DateTime.TryParse(value, out var date) ? date : DateTime.MinValue;
}
