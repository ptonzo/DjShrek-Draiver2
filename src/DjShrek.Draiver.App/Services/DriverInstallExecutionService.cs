using System.Diagnostics;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Executes one explicitly approved INF installation through Windows Driver Store tooling.
/// A successful pnputil exit code is not treated as proof that the target device was updated.
/// </summary>
public sealed class DriverInstallExecutionService
{
    public async Task<DriverInstallExecutionResult> ExecuteAsync(
        DriverInstallationPlan plan,
        string packageRoot,
        bool userConfirmed,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return DriverInstallExecutionResult.NotStarted("Установка драйверов поддерживается только в Windows.");

        if (plan is null || !plan.CanProceed ||
            !string.Equals(plan.Status, "ReadyForExplicitInstallation", StringComparison.Ordinal))
            return DriverInstallExecutionResult.NotStarted("План установки не имеет статуса ReadyForExplicitInstallation.");

        if (!userConfirmed)
            return DriverInstallExecutionResult.NotStarted("Не получено явное подтверждение запуска установки.");

        if (string.IsNullOrWhiteSpace(packageRoot) || !Path.IsPathFullyQualified(packageRoot))
            return DriverInstallExecutionResult.NotStarted("Не задан абсолютный путь к корню пакета.");

        if (string.IsNullOrWhiteSpace(plan.InfPath) || !Path.IsPathFullyQualified(plan.InfPath))
            return DriverInstallExecutionResult.NotStarted("В плане отсутствует абсолютный путь к INF-файлу.");

        string root;
        string inf;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
            inf = Path.GetFullPath(plan.InfPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return DriverInstallExecutionResult.NotStarted($"Некорректный путь пакета: {ex.Message}");
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!inf.StartsWith(rootPrefix, comparison) ||
            !string.Equals(Path.GetExtension(inf), ".inf", comparison))
            return DriverInstallExecutionResult.NotStarted("INF должен находиться внутри указанного корня пакета.");

        if (!File.Exists(inf))
            return DriverInstallExecutionResult.NotStarted("INF-файл не существует.");

        if (ContainsReparsePoint(root, inf))
            return DriverInstallExecutionResult.NotStarted("В пути к INF обнаружен reparse point; запуск заблокирован.");

        var pnputil = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "pnputil.exe");
        if (!File.Exists(pnputil))
            return DriverInstallExecutionResult.NotStarted("Не найден системный pnputil.exe.");

        var startInfo = new ProcessStartInfo
        {
            FileName = pnputil,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        startInfo.ArgumentList.Add("/add-driver");
        startInfo.ArgumentList.Add(inf);
        startInfo.ArgumentList.Add("/install");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return DriverInstallExecutionResult.NotStarted("Windows не запустила pnputil.exe.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DriverInstallExecutionResult.NotStarted($"Не удалось запустить pnputil.exe: {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var limit = timeout ?? TimeSpan.FromMinutes(3);
        if (limit <= TimeSpan.Zero)
            limit = TimeSpan.FromMinutes(3);

        using var timeoutCts = new CancellationTokenSource(limit);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            var stdout = await ReadSafelyAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await ReadSafelyAsync(stderrTask).ConfigureAwait(false);
            var wasCancelled = cancellationToken.IsCancellationRequested;
            return new DriverInstallExecutionResult(
                true, null, false, !wasCancelled, wasCancelled,
                stdout, stderr,
                wasCancelled ? "Cancelled" : "TimedOut",
                wasCancelled
                    ? "Установка прервана по запросу вызывающей стороны."
                    : $"Превышено время ожидания ({limit}).");
        }

        var output = await ReadSafelyAsync(stdoutTask).ConfigureAwait(false);
        var error = await ReadSafelyAsync(stderrTask).ConfigureAwait(false);
        var exitCode = process.ExitCode;
        return new DriverInstallExecutionResult(
            true, exitCode, false, false, false, output, error,
            exitCode == 0 ? "CommandCompleted" : "CommandFailed",
            exitCode == 0
                ? "pnputil завершил команду без ошибки. Требуется отдельная проверка версии драйвера целевого устройства."
                : $"pnputil завершился с кодом {exitCode}. Проверьте вывод Windows и журнал установки.");
    }

    private static bool ContainsReparsePoint(string root, string inf)
    {
        try
        {
            var rootInfo = new DirectoryInfo(root);
            if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;

            var relative = Path.GetRelativePath(root, inf);
            var current = root;
            var parts = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                FileAttributes attributes = i == parts.Length - 1
                    ? File.GetAttributes(current)
                    : Directory.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch { }
    }

    private static async Task<string> ReadSafelyAsync(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return string.Empty; }
    }
}

public sealed record DriverInstallExecutionResult(
    bool Started,
    int? ExitCode,
    bool TimedOut,
    bool WasCancelled,
    bool Cancelled,
    string StandardOutput,
    string StandardError,
    string Status,
    string Summary)
{
    public static DriverInstallExecutionResult NotStarted(string reason) =>
        new(false, null, false, false, false, string.Empty, string.Empty, "NotStarted", reason);
}
