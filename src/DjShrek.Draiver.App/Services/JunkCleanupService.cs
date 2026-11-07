using System.IO;

namespace DjShrek.Draiver.App.Services;

public sealed class JunkFileItem
{
    public string Path { get; init; } = "";
    public string Category { get; init; } = "";
    public long Size { get; init; }
    public DateTime LastModified { get; init; }
    public bool Selected { get; set; } = true;
    public string SizeText => FormatBytes(Size);
    public string ModifiedText => LastModified.ToString("yyyy-MM-dd HH:mm");
    public static string FormatBytes(long value)
    {
        double amount = value;
        foreach (var unit in new[] { "Б", "КБ", "МБ", "ГБ" })
        {
            if (amount < 1024 || unit == "ГБ") return $"{amount:0.0} {unit}";
            amount /= 1024;
        }
        return $"{amount:0.0} ГБ";
    }
}

public sealed record CleanupSummary(int DeletedFiles, long FreedBytes, int SkippedFiles, IReadOnlyList<string> Errors);

public sealed class JunkCleanupService
{
    private const int MaxFiles = 20000;
    private static IReadOnlyList<(string Name, string Root)> GetRoots()
    {
        var candidates = new[]
        {
            ("Временные файлы пользователя", Path.GetTempPath()),
            ("Временные файлы Windows", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"))
        };
        var roots = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in candidates)
        {
            try
            {
                var full = Path.GetFullPath(item.Item2);
                if (Directory.Exists(full) && seen.Add(full)) roots.Add((item.Item1, full));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return roots;
    }

    public IReadOnlyList<JunkFileItem> Scan(int minimumAgeDays = 7)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Очистка доступна только в Windows 10/11.");
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, minimumAgeDays));
        var found = new List<JunkFileItem>(1024);
        foreach (var (category, root) in GetRoots())
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && found.Count < MaxFiles)
            {
                var folder = stack.Pop();
                IEnumerable<string> entries;
                try { entries = Directory.EnumerateFileSystemEntries(folder).ToArray(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var entry in entries)
                {
                    if (found.Count >= MaxFiles) break;
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            stack.Push(entry);
                            continue;
                        }
                        var info = new FileInfo(entry);
                        if (info.LastWriteTimeUtc < cutoff)
                            found.Add(new JunkFileItem
                            {
                                Path = info.FullName, Category = category, Size = info.Length,
                                LastModified = info.LastWriteTime
                            });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
            }
        }
        return found;
    }

    public CleanupSummary Clean(IEnumerable<JunkFileItem> selected, int minimumAgeDays = 7)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Очистка доступна только в Windows 10/11.");
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, minimumAgeDays));
        var roots = GetRoots().Select(x => x.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();
        var deleted = 0;
        long freed = 0;
        var skipped = 0;
        var errors = new List<string>();
        foreach (var item in selected.Where(x => x.Selected))
        {
            try
            {
                var full = Path.GetFullPath(item.Path);
                if (!roots.Any(root => full.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                { skipped++; continue; }
                var attributes = File.GetAttributes(full);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                { skipped++; continue; }
                var info = new FileInfo(full);
                if (info.LastWriteTimeUtc >= cutoff || info.Length != item.Size ||
                    info.LastWriteTime != item.LastModified)
                { skipped++; continue; }
                info.Delete();
                deleted++;
                freed += item.Size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                skipped++;
                if (errors.Count < 20) errors.Add(item.Path + ": " + ex.Message);
            }
        }
        return new CleanupSummary(deleted, freed, skipped, errors);
    }
}