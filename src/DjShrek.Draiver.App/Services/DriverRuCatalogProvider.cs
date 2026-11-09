using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Direct Driver.ru search adapter. The search page is a discovery source only:
/// package contents, exact INF match, OS support and CAT signature still require inspection.
/// </summary>
public sealed class DriverRuCatalogProvider
{
    private const int MaxPageBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static DriverRuCatalogProvider()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("DjShrek-Draiver/2.0");
        Client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9");
    }

    public static Uri BuildSearchUri(string hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId) || hardwareId.Length > 256 ||
            hardwareId.Any(char.IsControl))
            throw new ArgumentException("Некорректный Hardware ID.", nameof(hardwareId));
        return new Uri("https://driver.ru/Search/" + Uri.EscapeDataString(hardwareId.Trim()));
    }

    public async Task<IReadOnlyList<DriverUpdateCandidate>> SearchAsync(
        DeviceInfo device, string hardwareId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var uri = BuildSearchUri(hardwareId);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                return Array.Empty<DriverUpdateCandidate>();
            if (!response.IsSuccessStatusCode) return Array.Empty<DriverUpdateCandidate>();
            if (response.RequestMessage?.RequestUri?.Host is not { } host ||
                !host.Equals("driver.ru", StringComparison.OrdinalIgnoreCase))
                return Array.Empty<DriverUpdateCandidate>();
            if (response.Content.Headers.ContentLength is > MaxPageBytes)
                return Array.Empty<DriverUpdateCandidate>();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory = new MemoryStream();
            var buffer = new byte[32 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (memory.Length + read > MaxPageBytes) return Array.Empty<DriverUpdateCandidate>();
                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return ParseSearchPage(device, hardwareId, uri, System.Text.Encoding.UTF8.GetString(memory.ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Array.Empty<DriverUpdateCandidate>(); }
        catch (HttpRequestException) { return Array.Empty<DriverUpdateCandidate>(); }
        catch (IOException) { return Array.Empty<DriverUpdateCandidate>(); }
    }

    public static IReadOnlyList<DriverUpdateCandidate> ParseSearchPage(
        DeviceInfo device, string hardwareId, Uri searchUri, string html)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(searchUri);
        if (!searchUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !searchUri.Host.Equals("driver.ru", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Страница не принадлежит Driver.ru.");

        var normalizedHtml = Regex.Replace(html ?? string.Empty,
            @"<(script|style)\b[^>]*>.*?</\1\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var plain = WebUtility.HtmlDecode(Regex.Replace(normalizedHtml,
            @"</?(?:br|p|div|tr|td|li|h[1-6])\b[^>]*>", "\n", RegexOptions.IgnoreCase));
        plain = Regex.Replace(plain, @"<[^>]+>", " ");
        plain = Regex.Replace(plain, @"[ \t\u00A0]+", " ");
        var blocks = Regex.Split(plain, @"(?=Производитель\s*:)").Where(x =>
            x.Contains("Производитель:", StringComparison.OrdinalIgnoreCase) &&
            (x.Contains("Оборудование:", StringComparison.OrdinalIgnoreCase) ||
             x.Contains("Название:", StringComparison.OrdinalIgnoreCase))).ToArray();

        var results = new List<DriverUpdateCandidate>();
        for (var index = 0; index < blocks.Length && index < 20; index++)
        {
            var block = blocks[index];
            var manufacturer = Field(block, "Производитель");
            var equipment = Field(block, "Оборудование");
            var title = Field(block, "Название");
            var version = Field(block, "Версия");
            var releaseDate = Field(block, "Дата выхода");
            var os = Field(block, "Система");
            var description = Field(block, "Описание");
            if (string.IsNullOrWhiteSpace(equipment) && string.IsNullOrWhiteSpace(title)) continue;

            var deviceTitle = string.IsNullOrWhiteSpace(equipment) ? title : equipment;
            var date = NormalizeDate(releaseDate);
            var identity = DeviceHardwareIdParser.Parse(hardwareId);
            results.Add(new DriverUpdateCandidate
            {
                UpdateId = searchUri.AbsoluteUri + "#driver-" + index.ToString(CultureInfo.InvariantCulture),
                Title = string.IsNullOrWhiteSpace(title) ? "Драйвер " + deviceTitle : title,
                Manufacturer = string.IsNullOrWhiteSpace(manufacturer) ? device.Manufacturer : manufacturer,
                Model = device.Name,
                Provider = "driver.ru",
                DeviceClass = device.Class,
                HardwareId = hardwareId,
                BusType = identity?.Bus ?? "",
                VendorId = identity?.VendorId ?? "",
                DeviceId = identity?.DeviceId ?? "",
                SubsystemId = identity?.SubsystemId ?? "",
                RevisionId = identity?.RevisionId ?? "",
                HardwareIdentitySummary = identity?.Summary ?? hardwareId,
                MatchType = "Hardware ID",
                DeviceName = device.Name,
                CurrentVersion = device.DriverVersion,
                Compatibility = DriverUpdateCandidate.HardwareIdSearchNotice,
                AvailableVersion = string.IsNullOrWhiteSpace(version) ? "Не указана на странице" : version,
                DriverVersionDate = releaseDate,
                PublishedDate = date,
                Description = string.Join(" | ", new[] { description, os }.Where(x => !string.IsNullOrWhiteSpace(x))),
                SearchQuery = hardwareId,
                Evidence = "Driver.ru вернул запись по адресу поиска с Hardware ID. Совместимость подтверждается только после анализа INF/CAT скачанного пакета.",
                SourceType = "IndependentCatalog",
                SourceTrust = "IndependentCatalog_UnverifiedPackage",
                SourceUrl = searchUri.AbsoluteUri
            });
        }
        return results;
    }

    private static string Field(string block, string label)
    {
        var match = Regex.Match(block, @"(?:^|\n)\s*" + Regex.Escape(label) +
            @"\s*:\s*(?<value>[^\r\n]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : "";
    }

    private static string NormalizeDate(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return "";
    }
}