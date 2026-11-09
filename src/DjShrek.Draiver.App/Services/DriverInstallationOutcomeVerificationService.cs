namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Compares the post-install device inventory with the requested target and version.
/// This is deliberately separate from pnputil execution: command completion alone is not verification.
/// </summary>
public sealed class DriverInstallationOutcomeVerificationService
{
    public DriverInstallationOutcomeVerification Verify(
        string targetInstanceId,
        string expectedVersion,
        string previousVersion,
        DriverInstallExecutionResult execution,
        IEnumerable<Models.DeviceInfo> currentInventory)
    {
        var findings = new List<string>();

        if (string.IsNullOrWhiteSpace(targetInstanceId))
            return Failed("Не указан Instance ID целевого устройства.");

        if (execution is null || !execution.Started)
            return Failed("Команда установки не была запущена.");

        if (execution.Cancelled || execution.TimedOut)
            return Failed("Команда установки была прервана; результат установки не подтверждён.");

        if (!string.Equals(execution.Status, "CommandCompleted", StringComparison.Ordinal) ||
            execution.ExitCode != 0)
            return Failed("pnputil сообщил об ошибке. Установка не считается подтверждённой.");

        if (currentInventory is null)
            return Failed("Не получен актуальный снимок устройств после установки.");

        var matches = currentInventory
            .Where(x => string.Equals(x.InstanceId, targetInstanceId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return Failed("Целевое устройство не найдено в актуальном снимке оборудования.");

        if (matches.Count > 1)
            return Failed("Instance ID целевого устройства неоднозначен в снимке оборудования.");

        var device = matches[0];
        var installedVersion = device.DriverVersion ?? string.Empty;

        if (string.IsNullOrWhiteSpace(installedVersion))
            return Failed("Windows не предоставила версию установленного драйвера.");

        if (string.IsNullOrWhiteSpace(expectedVersion))
            return Failed("В плане установки не задана ожидаемая версия драйвера.");

        if (!TryNormalizeVersion(expectedVersion, out var expected) ||
            !TryNormalizeVersion(installedVersion, out var actual))
            return Failed("Не удалось однозначно разобрать ожидаемую или установленную версию.");

        if (actual != expected)
        {
            findings.Add($"Ожидалась версия {expectedVersion}, обнаружена {installedVersion}.");
            if (TryNormalizeVersion(previousVersion, out var previous) && actual == previous)
                findings.Add("Версия драйвера не изменилась относительно снимка до установки.");

            return new DriverInstallationOutcomeVerification(
                false, "VersionMismatch", targetInstanceId, expectedVersion,
                installedVersion, findings,
                "Команда установки завершилась, но требуемая версия на целевом устройстве не подтверждена.");
        }

        if (!string.IsNullOrWhiteSpace(device.Problem) &&
            !string.Equals(device.Problem, "0", StringComparison.OrdinalIgnoreCase))
            findings.Add($"Устройство сообщает код проблемы: {device.Problem}.");

        if (!string.IsNullOrWhiteSpace(device.Status) &&
            !device.Status.Contains("OK", StringComparison.OrdinalIgnoreCase) &&
            !device.Status.Contains("Работает", StringComparison.OrdinalIgnoreCase))
            findings.Add($"Состояние устройства после установки: {device.Status}.");

        var healthy = findings.Count == 0;
        return new DriverInstallationOutcomeVerification(
            healthy,
            healthy ? "Verified" : "VersionInstalledWithDeviceWarnings",
            targetInstanceId,
            expectedVersion,
            installedVersion,
            findings,
            healthy
                ? "Ожидаемая версия драйвера обнаружена у целевого устройства."
                : "Ожидаемая версия обнаружена, но устройство сообщает дополнительные предупреждения.");
    }

    private static DriverInstallationOutcomeVerification Failed(string reason) =>
        new(false, "NotVerified", string.Empty, string.Empty, string.Empty,
            new[] { reason }, reason);

    private static bool TryNormalizeVersion(string value, out Version version)
    {
        version = new Version(0, 0);
        if (!Version.TryParse(value?.Trim(), out var parsed) || parsed is null)
            return false;

        var build = parsed.Build < 0 ? 0 : parsed.Build;
        var revision = parsed.Revision < 0 ? 0 : parsed.Revision;
        version = new Version(parsed.Major, parsed.Minor, build, revision);
        return true;
    }
}

public sealed record DriverInstallationOutcomeVerification(
    bool IsVerified,
    string Status,
    string DeviceInstanceId,
    string ExpectedVersion,
    string InstalledVersion,
    IReadOnlyList<string> Findings,
    string Summary);
