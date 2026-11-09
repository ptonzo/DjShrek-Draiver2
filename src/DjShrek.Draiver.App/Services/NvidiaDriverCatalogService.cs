using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// NVIDIA's public driver lookup adapter. Product IDs are resolved from NVIDIA's
/// own XML lookup using an exact normalized product-name match; no guessed IDs.
/// Search results are surfaced as official detail pages, never as installable packages.
/// </summary>
public sealed class NvidiaDriverCatalogService
{
    private const string ApiRoot = "https://www.nvidia.com/Download/API/";
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(25) };

    static NvidiaDriverCatalogService() =>
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("DjShrek-Draiver/2.0");

    public async Task<IReadOnlyList<NvidiaDriverCatalogItem>> FindDriversAsync(
        string detectedProductName, bool windows11, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(detectedProductName) || detectedProductName.Length > 256 ||
            detectedProductName.Any(char.IsControl))
            return Array.Empty<NvidiaDriverCatalogItem>();

        var products = await ReadLookupAsync(3, cancellationToken);
        var osEntries = await ReadLookupAsync(4, cancellationToken);
        var product = products
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .Select(x => new { Entry = x, Score = Normalize(x.Name) == Normalize(detectedProductName) ? 2 :
                Normalize(x.Name).Contains(Normalize(detectedProductName), StringComparison.Ordinal) ? 1 : 0 })
            .Where(x => x.Score == 2)
            .Select(x => x.Entry)
            .FirstOrDefault();
        if (product is null || !int.TryParse(product.Value, out var productId) ||
            !int.TryParse(product.ParentId, out var seriesId))
            return Array.Empty<NvidiaDriverCatalogItem>();

        var osName = windows11 ? "Windows 11" : "Windows 10";
        var os = osEntries.FirstOrDefault(x =>
            Normalize(x.Name).Equals(Normalize(osName), StringComparison.Ordinal) ||
            Normalize(x.Name).Equals(Normalize(osName + " 64-bit"), StringComparison.Ordinal));
        if (os is null || !int.TryParse(os.Value, out var osId))
            return Array.Empty<NvidiaDriverCatalogItem>();

        var query = new Dictionary<string, string>
        {
            ["psid"] = seriesId.ToString(), ["pfid"] = productId.ToString(),
            ["osid"] = osId.ToString(), ["lid"] = "1", ["lang"] = "en-us",
            ["dtcid"] = "1", ["dtid"] = "1", ["ctk"] = "0"
        };
        var uri = new Uri("https://www.nvidia.com/Download/processFind.aspx?" +
            string.Join("&", query.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value))));
        var html = await GetTextAsync(uri, cancellationToken);
        var matches = Regex.Matches(html,
            @"href\s*=\s*[""'](?<url>(?:https://www\.nvidia\.com)?/[^""']*/drivers/details/[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var results = new List<NvidiaDriverCatalogItem>();
        foreach (Match match in matches.Cast<Match>().Take(10))
        {
            var raw = WebUtility.HtmlDecode(match.Groups["url"].Value);
            if (!Uri.TryCreate(new Uri("https://www.nvidia.com"), raw, out var detail) ||
                detail.Scheme != Uri.UriSchemeHttps ||
                !detail.Host.Equals("www.nvidia.com", StringComparison.OrdinalIgnoreCase) ||
                !detail.AbsolutePath.Contains("/drivers/details/", StringComparison.OrdinalIgnoreCase))
                continue;
            var rowStart = Math.Max(0, match.Index - 1200);
            var row = html.Substring(rowStart, Math.Min(2400, html.Length - rowStart));
            var plain = WebUtility.HtmlDecode(Regex.Replace(row, "<[^>]+>", " "));
            plain = Regex.Replace(plain, @"\s+", " ").Trim();
            results.Add(new NvidiaDriverCatalogItem(
                detectedProductName, "GeForce Game Ready", ExtractVersion(plain),
                plain.Length > 500 ? plain[..500] : plain, detail));
        }
        return results.GroupBy(x => x.DetailUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First()).ToArray();
    }

    private static async Task<IReadOnlyList<LookupEntry>> ReadLookupAsync(
        int typeId, CancellationToken cancellationToken)
    {
        var uri = new Uri(ApiRoot + "lookupValueSearch.aspx?TypeID=" + typeId + "&ParentID=0");
        var xml = await GetTextAsync(uri, cancellationToken);
        var document = XDocument.Parse(xml, LoadOptions.None);
        return document.Descendants()
            .Where(x => x.Name.LocalName.Equals("LookupValue", StringComparison.OrdinalIgnoreCase))
            .Select(x => new LookupEntry(
                x.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Name", StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? "",
                x.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Value", StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? "",
                x.Attribute("ParentID")?.Value ?? ""))
            .Where(x => x.Name.Length > 0 && x.Value.Length > 0).Take(5000).ToArray();
    }

    private static async Task<string> GetTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidDataException("NVIDIA catalog redirect was not followed.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("NVIDIA catalog response exceeds size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > MaxResponseBytes)
                throw new InvalidDataException("NVIDIA catalog response exceeds size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static string Normalize(string value) =>
        string.Join(" ", Regex.Replace(value, @"[^a-zA-Z0-9]+", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string ExtractVersion(string value)
    {
        var match = Regex.Match(value, @"\b\d{3}\.\d{2,3}\b");
        return match.Success ? match.Value : "Версия уточняется на странице NVIDIA";
    }

    private sealed record LookupEntry(string Name, string Value, string ParentId);
}

public sealed record NvidiaDriverCatalogItem(
    string ProductName, string Channel, string Version, string Summary, Uri DetailUri);
