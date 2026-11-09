using System.Net;
using System.Text.Json;

namespace DjShrek.Draiver.App.Services;

public sealed class MsiDriverCatalogService
{
    private const string ApiRoot = "https://www.msi.com/api/v1/product/support/panel";
    private const int MaxBytes = 12 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(25) };

    static MsiDriverCatalogService() =>
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("DjShrek-Draiver/2.0");

    public async Task<IReadOnlyList<MsiDriverPackage>> GetMotherboardDriversAsync(
        string model, bool windows11, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 160 || model.Any(char.IsControl))
            throw new ArgumentException("Некорректная модель MSI.", nameof(model));
        var query = new Dictionary<string, string>
        {
            ["product"] = model.Trim(),
            ["type"] = "driver",
            ["os"] = windows11 ? "Win11 64" : "Win10 64"
        };
        var uri = new Uri(ApiRoot + "?" + string.Join("&",
            query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidDataException("MSI catalog redirect was not followed.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxBytes)
            throw new InvalidDataException("MSI catalog response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes) throw new InvalidDataException("MSI catalog response is too large.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return Parse(buffer.ToArray(), model, uri);
    }

    internal static IReadOnlyList<MsiDriverPackage> Parse(byte[] json, string model, Uri catalogUri)
    {
        using var document = JsonDocument.Parse(json);
        var packages = new List<MsiDriverPackage>();
        Visit(document.RootElement, item =>
        {
            var id = Read(item, "download_id", "downloadId");
            var name = Read(item, "download_name", "downloadName", "name", "title");
            var url = Read(item, "download_url", "downloadUrl", "url", "link");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var packageUri)) return;
            if (packageUri.Scheme != Uri.UriSchemeHttps ||
                !(packageUri.Host.Equals("download.msi.com", StringComparison.OrdinalIgnoreCase) ||
                  packageUri.Host.Equals("download.msi.com.tw", StringComparison.OrdinalIgnoreCase)))
                return;
            var hash = Read(item, "download_sha256", "downloadSha256", "sha256", "hash");
            hash = hash.Replace("SHA-256:", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<br>", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (hash.Length != 0 && (hash.Length != 64 || !hash.All(Uri.IsHexDigit))) hash = "";
            packages.Add(new MsiDriverPackage(id, name,
                Read(item, "download_version", "downloadVersion", "version"),
                Read(item, "download_date", "downloadDate", "date", "releaseDate"),
                Read(item, "download_desc", "downloadDesc", "description"),
                Read(item, "download_size", "downloadSize", "size"), hash,
                packageUri, model, catalogUri));
        });
        return packages.GroupBy(p => p.DownloadId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).ToArray();
    }

    private static void Visit(JsonElement element, Action<JsonElement> action)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            action(element);
            foreach (var property in element.EnumerateObject()) Visit(property.Value, action);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Visit(item, action);
    }

    private static string Read(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return "";
        foreach (var property in element.EnumerateObject())
            if (names.Any(n => property.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            {
                if (property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString() ?? "";
                if (property.Value.ValueKind == JsonValueKind.Number) return property.Value.ToString();
            }
        return "";
    }
}

public sealed record MsiDriverPackage(
    string DownloadId, string Name, string Version, string ReleaseDate,
    string Description, string FileSize, string Sha256,
    Uri PackageUri, string Model, Uri CatalogUri);
