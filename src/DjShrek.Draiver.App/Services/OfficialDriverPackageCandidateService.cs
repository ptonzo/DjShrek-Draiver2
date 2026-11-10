using System.Net;
using System.Text.RegularExpressions;

namespace DjShrek.Draiver.App.Services;

public sealed record OfficialDriverPackageCandidate(
    string Title, string Url, string Provider, string SourcePage,
    string FileType, string MatchHardwareId, string ConfidenceNote);

/// <summary>
/// Extracts direct package links from an already discovered official vendor support page.
/// A link is only a download candidate; INF matching and catalog signature validation remain
/// separate mandatory steps before installation.
/// </summary>
public sealed class OfficialDriverPackageCandidateService
{
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(25) };


    private static readonly Regex AnchorRegex = new(
        @"<a\b[^>]*href\s*=\s*[""'](?<url>[^""']+)[""'][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);

    public async Task<IReadOnlyList<OfficialDriverPackageCandidate>> DiscoverAsync(
        InternetDriverSearchResult source, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var page) ||
            page.Scheme != Uri.UriSchemeHttps || !IsOfficial(page.Host))
            throw new InvalidDataException("Страница источника не относится к разрешённому HTTPS-домену производителя.");

        using var request = new HttpRequestMessage(HttpMethod.Get, page);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidDataException("Страница перенаправляет на другой адрес. Откройте её вручную на сайте производителя.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 8 * 1024 * 1024)
            throw new InvalidDataException("Страница каталога превышает допустимый размер 8 МБ.");

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (html.Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Страница каталога превышает допустимый размер 8 МБ.");

        var output = new List<OfficialDriverPackageCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AnchorRegex.Matches(html))
        {
            var raw = WebUtility.HtmlDecode(match.Groups["url"].Value.Trim());
            if (!Uri.TryCreate(page, raw, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !new OfficialDriverSourcePolicy().Assess(page.AbsoluteUri, uri.AbsoluteUri, source.Manufacturer).IsOfficial || !IsPackageUrl(uri) || !seen.Add(uri.AbsoluteUri))
                continue;

            var title = WebUtility.HtmlDecode(TagRegex.Replace(match.Groups["text"].Value, " "));
            title = Regex.Replace(title, @"\s+", " ").Trim();
            if (title.Length > 180) title = title[..180];
            if (string.IsNullOrWhiteSpace(title))
                title = Path.GetFileName(uri.AbsolutePath);

            output.Add(new OfficialDriverPackageCandidate(
                title, uri.AbsoluteUri, source.Manufacturer, page.AbsoluteUri,
                Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToUpperInvariant(),
                source.HardwareId,
                "Ссылка найдена на официальной странице. Совместимость и подпись пакета ещё не подтверждены."));
        }

        return output.Take(50).ToArray();
    }

    private static bool IsPackageUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        var extension = Path.GetExtension(path);
        if (new[] { ".exe", ".msi", ".zip", ".cab", ".7z", ".rar" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase)) return true;

        var text = (path + uri.Query).ToLowerInvariant();
        return text.Contains("download", StringComparison.Ordinal) &&
               (text.Contains("driver", StringComparison.Ordinal) ||
                text.Contains("package", StringComparison.Ordinal) ||
                text.Contains("file", StringComparison.Ordinal));
    }

}\n