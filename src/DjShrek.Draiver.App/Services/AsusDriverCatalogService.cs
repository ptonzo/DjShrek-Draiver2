using System.Net;
using System.Text.Json;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// ASUS Support WebAPI adapter. It consumes the product identifiers selected by
/// ASUS's own model/product lookup (PDId and PDHashedId) and retrieves the official
/// per-model driver list. It never guesses product IDs from model strings.
/// </summary>
public sealed class AsusDriverCatalogService
{
    private const string ApiRoot = "https://www.asus.com/support/webapi/ProductV2/";
    private const string ProductApi = "https://www.asus.com/support/api/product.asmx/";
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(25) };

    static AsusDriverCatalogService()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("DjShrek-Draiver/2.0");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        Client.DefaultRequestHeaders.Referrer = new Uri("https://www.asus.com/support/download-center/");
    }

    public async Task<IReadOnlyList<AsusDriverPackage>> GetDriversAsync(
        AsusProductIdentity product, int osId, string website = "global",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        ValidateToken(product.Model, nameof(product.Model), 128);
        ValidateToken(product.ProductId, nameof(product.ProductId), 64);
        ValidateToken(product.HashedProductId ?? "", nameof(product.HashedProductId), 256);
        ValidateToken(product.Cpu ?? "", nameof(product.Cpu), 128);
        ValidateToken(website, nameof(website), 16);
        if (osId is not (8 or 45 or 52))
            throw new ArgumentOutOfRangeException(nameof(osId), "Поддерживаются только известные ASUS OS ID: 8, 45, 52.");

        var query = new Dictionary<string, string>
        {
            ["website"] = website,
            ["model"] = product.Model,
            ["pdid"] = product.ProductId,
            ["pdhashedid"] = product.HashedProductId ?? product.ProductId,
            ["mode"] = "",
            ["cpu"] = product.Cpu ?? "",
            ["osid"] = osId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["active"] = "",
            ["systemCode"] = product.SystemCode ?? ""
        };
        var uri = new Uri(ApiRoot + "GetPDDrivers?" + string.Join("&",
            query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));

        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidDataException("ASUS API returned a redirect; it was not followed.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("ASUS driver catalog response is too large.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidDataException("ASUS driver catalog response is too large.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return ParseDrivers(buffer.ToArray(), product, osId, uri);
    }


    public async Task<IReadOnlyList<AsusProductLevelEntry>> GetProductLevelAsync(
        int type, int typeId, int productFlag, string website = "global",
        CancellationToken cancellationToken = default)
    {
        if (type is not (1 or 2) || typeId <= 0 || productFlag is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(type));
        ValidateToken(website, nameof(website), 16);
        var query = new Dictionary<string, string>
        {
            ["website"] = website, ["type"] = type.ToString(),
            ["typeid"] = typeId.ToString(), ["productflag"] = productFlag.ToString()
        };
        var uri = new Uri(ProductApi + "GetPDLevel?" + string.Join("&",
            query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidDataException("ASUS product lookup redirect was not followed.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("ASUS response exceeds the allowed size.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > MaxResponseBytes) throw new InvalidDataException("ASUS response exceeds the allowed size.");
        using var document = JsonDocument.Parse(bytes);
        var result = document.RootElement;
        while (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("Result", out var nested))
            result = nested;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("Obj", out nested))
            result = nested;
        if (result.ValueKind != JsonValueKind.Array) return Array.Empty<AsusProductLevelEntry>();
        return result.EnumerateArray().Take(1000).Select(item =>
            new AsusProductLevelEntry(ReadString(item, "PDId", "Id", "ID"),
                ReadString(item, "PDHashedId", "PDHashedID"),
                ReadString(item, "PDName", "Name", "Title"),
                ReadString(item, "PDUrl", "Url", "URL")))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name)).ToArray();
    }

    // GetDriversAsync intentionally requires a real ASUS PDId/PDHashedId.
    // SMBIOS model strings alone are not sufficient to construct this API request.

    internal static IReadOnlyList<AsusDriverPackage> ParseDrivers(
        byte[] jsonBytes, AsusProductIdentity product, int osId, Uri catalogUri)
    {
        using var document = JsonDocument.Parse(jsonBytes);
        var root = document.RootElement;
        var list = FindArray(root, "Items", "Obj", "Result", "Data", "DriverList", "PDDrivers");
        if (list is null) return Array.Empty<AsusDriverPackage>();

        var results = new List<AsusDriverPackage>();
        foreach (var item in list.Value.EnumerateArray().Take(500))
        {
            var file = item;
            if (TryProperty(item, "Files", out var files) &&
                files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0)
                file = files[0];

            var title = FirstNonEmpty(ReadString(item, "Title", "Name", "FileName", "DownloadName"),
                ReadString(file, "Title", "Name", "FileName", "DownloadName"));
            var version = FirstNonEmpty(ReadString(file, "Version", "Ver", "DriverVersion"),
                ReadString(item, "Version", "Ver", "DriverVersion"));
            var date = FirstNonEmpty(ReadString(file, "ReleaseDate", "Date", "UpdateDate"),
                ReadString(item, "ReleaseDate", "Date", "UpdateDate"));
            var description = FirstNonEmpty(ReadString(item, "Description", "Memo", "Info"),
                ReadString(file, "Description", "Memo", "Info"));
            var category = FirstNonEmpty(ReadString(item, "Category", "Type", "OSName"),
                ReadString(file, "Category", "Type", "OSName"));
            var sha256 = FirstNonEmpty(ReadString(file, "sha256", "SHA256", "Sha256"),
                ReadString(item, "sha256", "SHA256", "Sha256"));
            var download = FirstNonEmpty(FindDownloadUrl(file), FindDownloadUrl(item));
            if (string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(download, UriKind.Absolute, out var packageUri))
                continue;
            if (packageUri.Scheme != Uri.UriSchemeHttps ||
                !(packageUri.Host.Equals("dlcdnets.asus.com", StringComparison.OrdinalIgnoreCase) ||
                  packageUri.Host.Equals("dlcdnets.asus.com.cn", StringComparison.OrdinalIgnoreCase) ||
                  packageUri.Host.Equals("rog.asus.com", StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add(new AsusDriverPackage(
                title, version, date, description, category, sha256,
                packageUri, product.Model, osId, catalogUri));
        }
        return results
            .GroupBy(x => x.PackageUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First()).ToArray();
    }

    private static JsonElement? FindArray(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        foreach (var name in names)
            if (TryProperty(root, name, out var value) && value.ValueKind == JsonValueKind.Array)
                return value;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var name in names)
                if (TryProperty(property.Value, name, out var value) && value.ValueKind == JsonValueKind.Array)
                    return value;
        }
        return null;
    }

    private static string FirstNonEmpty(string first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : first;

    private static string FindDownloadUrl(JsonElement item)
    {
        foreach (var name in new[] { "DownloadUrl", "DownloadURL", "Url", "URL", "FileUrl", "Link" })
        {
            var value = ReadString(item, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        if (TryProperty(item, "Files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                var value = ReadString(file, "DownloadUrl", "Url", "URL", "FileUrl");
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return "";
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryProperty(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
            if (value.ValueKind == JsonValueKind.Number) return value.ToString();
        }
        return "";
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        }
        value = default;
        return false;
    }

    private static void ValidateToken(string value, string name, int maxLength)
    {
        if (value.Length > maxLength || value.Any(char.IsControl))
            throw new ArgumentException("Invalid ASUS catalog parameter.", name);
    }
}

public sealed record AsusProductIdentity(
    string Model, string ProductId, string? HashedProductId = null, string? Cpu = null, string? SystemCode = null);

public sealed record AsusProductLevelEntry(string Id, string HashedId, string Name, string Url);

public sealed record AsusDriverPackage(
    string Title, string Version, string ReleaseDate, string Description, string Category,
    string Sha256, Uri PackageUri, string Model, int OperatingSystemId, Uri CatalogUri);
