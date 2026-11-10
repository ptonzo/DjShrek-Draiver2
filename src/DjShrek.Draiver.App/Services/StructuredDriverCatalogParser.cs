using System.Text.Json;
using System.Xml.Linq;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Parses vendor-provided JSON/XML catalog payloads into normalized package records.
/// Fetching is intentionally separate: callers must obtain payloads from verified HTTPS OEM endpoints.
/// </summary>
public sealed class StructuredDriverCatalogParser
{
    private const int MaxPayloadCharacters = 8 * 1024 * 1024;

    public IReadOnlyList<StructuredDriverCatalogEntry> Parse(string payload, string sourceUrl, string vendor)
    {
        if (string.IsNullOrWhiteSpace(payload)) return Array.Empty<StructuredDriverCatalogEntry>();
        if (payload.Length > MaxPayloadCharacters)
            throw new InvalidDataException("Структурированный каталог превышает лимит 8 МБ.");
        var policy = new OfficialDriverSourcePolicy();
        if (!policy.Assess(sourceUrl, sourceUrl, vendor).IsOfficial)
            throw new InvalidDataException("Источник структурированного каталога не подтверждён как официальный HTTPS-домен.");

        var trimmed = payload.TrimStart();
        return trimmed.StartsWith("<", StringComparison.Ordinal)
            ? ParseXml(payload, sourceUrl, vendor)
            : ParseJson(payload, sourceUrl, vendor);
    }

    private static IReadOnlyList<StructuredDriverCatalogEntry> ParseJson(string payload, string sourceUrl, string vendor)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 48, CommentHandling = JsonCommentHandling.Skip });
        var items = new List<JsonElement>();
        CollectJsonItems(document.RootElement, items, 0);
        return items.Take(5000).Select(item => new StructuredDriverCatalogEntry
        {
            Vendor = vendor,
            Model = Read(item, "model", "product", "device", "name", "title"),
            OperatingSystem = Read(item, "os", "operatingsystem", "platform", "system"),
            Architecture = Read(item, "architecture", "arch", "platformarchitecture"),
            Version = Read(item, "version", "driverversion", "releaseversion"),
            ReleaseDate = Read(item, "date", "releasedate", "publishdate"),
            PackageUrl = Resolve(sourceUrl, Read(item, "downloadurl", "packageurl", "download", "url", "fileurl")),
            DetailsUrl = Resolve(sourceUrl, Read(item, "detailsurl", "supporturl", "producturl")),
            HardwareIds = SplitIds(Read(item, "hardwareids", "hardwareid", "pnpid", "deviceid")),
            SourceFormat = "JSON"
        }).Where(x => !string.IsNullOrWhiteSpace(x.PackageUrl)).ToArray();
    }

    private static void CollectJsonItems(JsonElement element, List<JsonElement> output, int depth)
    {
        if (depth > 24 || output.Count >= 10000) return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) CollectJsonItems(child, output, depth + 1);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        if (HasAny(element, "downloadurl", "packageurl", "download", "fileurl"))
            output.Add(element);
        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                CollectJsonItems(property.Value, output, depth + 1);
    }

    private static IReadOnlyList<StructuredDriverCatalogEntry> ParseXml(string payload, string sourceUrl, string vendor)
    {
        var document = XDocument.Parse(payload, LoadOptions.None);
        var nodes = document.Descendants().Where(x =>
            x.Elements().Any(e => IsName(e.Name.LocalName, "downloadurl", "packageurl", "download", "fileurl"))).Take(5000);
        return nodes.Select(node => new StructuredDriverCatalogEntry
        {
            Vendor = vendor,
            Model = ReadXml(node, "model", "product", "device", "name", "title"),
            OperatingSystem = ReadXml(node, "os", "operatingsystem", "platform", "system"),
            Architecture = ReadXml(node, "architecture", "arch", "platformarchitecture"),
            Version = ReadXml(node, "version", "driverversion", "releaseversion"),
            ReleaseDate = ReadXml(node, "date", "releasedate", "publishdate"),
            PackageUrl = Resolve(sourceUrl, ReadXml(node, "downloadurl", "packageurl", "download", "url", "fileurl")),
            DetailsUrl = Resolve(sourceUrl, ReadXml(node, "detailsurl", "supporturl", "producturl")),
            HardwareIds = SplitIds(ReadXml(node, "hardwareids", "hardwareid", "pnpid", "deviceid")),
            SourceFormat = "XML"
        }).Where(x => !string.IsNullOrWhiteSpace(x.PackageUrl)).ToArray();
    }

    private static string Read(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
            if (names.Contains(Normalize(property.Name), StringComparer.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString()?.Trim() ?? "";
                if (property.Value.ValueKind == JsonValueKind.Number) return property.Value.ToString();
                if (property.Value.ValueKind == JsonValueKind.Array)
                    return string.Join(";", property.Value.EnumerateArray().Select(x => x.ToString()));
            }
        return "";
    }

    private static bool HasAny(JsonElement element, params string[] names) =>
        element.EnumerateObject().Any(x => names.Contains(Normalize(x.Name), StringComparer.OrdinalIgnoreCase));

    private static string ReadXml(XElement node, params string[] names) =>
        node.DescendantsAndSelf().FirstOrDefault(x => names.Contains(Normalize(x.Name.LocalName), StringComparer.OrdinalIgnoreCase))?.Value.Trim() ?? "";

    private static string Normalize(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string Resolve(string baseUrl, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var root) ||
            !Uri.TryCreate(root, value.Trim(), out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "";
        return uri.AbsoluteUri;
    }

    private static IReadOnlyList<string> SplitIds(string value) =>
        value.Split(new[] { ';', ',', '\n', '\r', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(256).ToArray();
}
