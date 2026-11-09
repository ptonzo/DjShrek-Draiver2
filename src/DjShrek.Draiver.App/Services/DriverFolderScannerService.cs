using System.Globalization;
using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

public sealed record DriverFolderCandidate(
    InfPackageInspection Inspection,
    bool IsNewerThanInstalled,
    string VersionComparison,
    string DisplayText);

/// <summary>
/// Scans an extracted OEM package folder and ranks INF files for one Plug and Play device.
/// Only exact Hardware ID matches with a valid catalog signature and applicable architecture
/// are marked installable. Compatible-ID matches remain visible as manual-review candidates.
/// </summary>
public sealed class DriverFolderScannerService
{
    private const int MaxInfFiles = 5000;
    private readonly DriverInfPackageInspector _inspector = new();

    public IReadOnlyList<DriverFolderCandidate> Scan(string folder, DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Папка пакета драйверов не найдена.");

        var files = EnumerateInfFiles(root).Take(MaxInfFiles + 1).ToArray();
        if (files.Length == 0) throw new InvalidDataException("В выбранной папке не найдены INF-файлы.");
        if (files.Length > MaxInfFiles)
            throw new InvalidDataException($"В папке более {MaxInfFiles} INF-файлов. Укажите папку конкретного пакета.");

        var results = new List<DriverFolderCandidate>();
        var errors = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var inspection = _inspector.Inspect(file, device);
                if (inspection.MatchKind == "No match") continue;

                var comparison = DriverVersionComparisonService.Compare(inspection.DriverVersion, device.DriverVersion);
                var isNewer = comparison.IsNewer;
                var state = inspection.IsInstallableForDevice
                    ? comparison.Relation switch
                    {
                        DriverVersionRelation.Newer => "Точное совпадение ID · CAT действителен · версия новее",
                        DriverVersionRelation.Same => "Точное совпадение ID · CAT действителен · версия совпадает",
                        DriverVersionRelation.Older => "Точное совпадение ID · CAT действителен · версия старее",
                        _ => "Точное совпадение ID · CAT действителен · версия не распознана"
                    }
                    : $"Требует ручной проверки · {inspection.MatchKind} · CAT: {inspection.CatalogSignature}";
                var display = $"{Path.GetFileName(file)} | {inspection.Provider} | {inspection.DriverVersion} | {inspection.DriverDate} | {state} | {comparison.Message}";
                results.Add(new DriverFolderCandidate(inspection, isNewer, state + " — " + comparison.Message, display));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                if (errors.Count < 10) errors.Add(Path.GetFileName(file) + ": " + ex.Message);
            }
        }

        if (results.Count == 0 && errors.Count > 0)
            throw new InvalidDataException("INF-файлы найдены, но ни один не удалось разобрать." + Environment.NewLine +
                string.Join(Environment.NewLine, errors));

        return results
            .OrderByDescending(x => x.Inspection.IsInstallableForDevice)
            .ThenByDescending(x => x.IsNewerThanInstalled)
            .ThenByDescending(x => ParseDate(x.Inspection.DriverDate))
            .ThenBy(x => x.Inspection.Provider, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateInfFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else if (Path.GetExtension(entry).Equals(".inf", StringComparison.OrdinalIgnoreCase))
                    yield return entry;
            }
        }
    }

    private static DateTime ParseDate(string value)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : DateTime.MinValue;
    }
}
