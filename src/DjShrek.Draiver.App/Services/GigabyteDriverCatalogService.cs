using System.Net;
using System.Text.Json;

namespace DjShrek.Draiver.App.Services;

public sealed class GigabyteDriverCatalogService
{
    private const string ApiRoot = "https://www.gigabyte.com/iisApplicationNuxt/api/proxy/api/v1.0/";
    private const int MaxBytes = 16 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(25) };

    static GigabyteDriverCatalogService() =>
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("DjShrek-Draiver/2.0");

    public async Task<IReadOnlyList<GigabyteDriverPackage>> FindMotherboardDriversAsync(
        string boardModel, int windowsBuild, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(boardModel) || boardModel.Length > 160 ||
            boardModel.Any(char.IsControl))
            throw new ArgumentException("Некорректная модель платы.", nameof(boardModel));

        var productsUri = new Uri(ApiRoot + "Support/global/DownloadCenter/2/GetProducts");
        using var productsResponse = await GetAsync(productsUri, cancellationToken);
        using var productsJson = JsonDocument.Parse(await ReadBoundedAsync(productsResponse.Content, cancellationToken));
        var product = FindProduct(productsJson.RootElement, boardModel);
        if (product is null) return Array.Empty<GigabyteDriverPackage>();

        var detailUri = new Uri(ApiRoot + "Consumer/global/GetProductTabDataAsync/Support/" +
            Uri.EscapeDataString(product.Value.Id));
        using var detailResponse = await GetAsync(detailUri, cancellationToken);
        using var detailJson = JsonDocument.Parse(await ReadBoundedAsync(detailResponse.Content, cancellationToken));
        return ParsePackages(detailJson.RootElement, product.Value.Name, detailUri, windowsBuild);
    }

    private static (string Id, string Name)? FindProduct(JsonElement root, string model)
    {
        var target = Normalize(model);
        var candidates = new List<(string Id, string Name, int Score)>();
        Visit(root, element =>
        {
            var id = Read(element, "productId", "id", "productID", "pid");
            var name = Read(element, "productName", "name", "model", "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return;
            var normalized = Normalize(name);
            // SMBIOS board names must match the catalog product exactly. Substring
            // matching can select a different board in the same product family.
            var score = normalized == target ? 100 : 0;
            if (score > 0) candidates.Add((id, name, score));
        });
        var exact = candidates.Select(x => (x.Id, x.Name))
            .DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        return exact.Length == 1 ? exact[0] : null;
    }

    private static IReadOnlyList<GigabyteDriverPackage> ParsePackages(
        JsonElement root, string model, Uri catalogUri, int windowsBuild)
    {
        var packages = new List<GigabyteDriverPackage>();
        Visit(root, item =>
        {
            var fileName = Read(item, "fileName", "filename", "name");
            var download = Read(item, "downloadUrl", "downloadURL", "url", "fileUrl", "link");
            if (string.IsNullOrWhiteSpace(fileName) || !Uri.TryCreate(download, UriKind.Absolute, out var uri))
                return;
            if (uri.Scheme != Uri.UriSchemeHttps ||
                !(uri.Host.Equals("download.gigabyte.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("download1.gigabyte.com", StringComparison.OrdinalIgnoreCase)))
                return;

            var description = Read(item, "description", "desc", "memo");
            var version = Read(item, "version", "fileVersion");
            var date = Read(item, "date", "releaseDate", "updateDate");
            var size = Read(item, "fileSize", "size");
            var category = Read(item, "infoName", "category", "type");
            var osText = Read(item, "osName", "os", "operatingSystem");
            if (!string.IsNullOrWhiteSpace(osText))
            {
                var isWindows11Only =
                    (osText.Contains("Win11", StringComparison.OrdinalIgnoreCase) ||
                     osText.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)) &&
                    !osText.Contains("Win10", StringComparison.OrdinalIgnoreCase) &&
                    !osText.Contains("Windows 10", StringComparison.OrdinalIgnoreCase);
                var isWindows10Only =
                    (osText.Contains("Win10", StringComparison.OrdinalIgnoreCase) ||
                     osText.Contains("Windows 10", StringComparison.OrdinalIgnoreCase)) &&
                    !osText.Contains("Win11", StringComparison.OrdinalIgnoreCase) &&
                    !osText.Contains("Windows 11", StringComparison.OrdinalIgnoreCase);

                if (windowsBuild >= 22000 && isWindows10Only) return;
                if (windowsBuild < 22000 && isWindows11Only) return;
            }

            packages.Add(new GigabyteDriverPackage(fileName, version, date, description,
                category, osText, size, uri, model, catalogUri));
        });
        return packages.GroupBy(x => x.PackageUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First()).ToArray();
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

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            response.Dispose();
            throw new InvalidDataException("Gigabyte API redirect was not followed.");
        }
        try { response.EnsureSuccessStatusCode(); return response; }
        catch { response.Dispose(); throw; }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength is > MaxBytes)
            throw new InvalidDataException("Gigabyte catalog response is too large.");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                throw new InvalidDataException("Gigabyte catalog response is too large.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), token);
        }
        return buffer.ToArray();
    }
}

public sealed record GigabyteDriverPackage(
    string FileName, string Version, string ReleaseDate, string Description,
    string Category, string OperatingSystem, string FileSize,
    Uri PackageUri, string Model, Uri CatalogUri);
