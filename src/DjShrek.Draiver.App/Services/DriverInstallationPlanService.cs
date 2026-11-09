namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Produces an explicit, reviewable installation plan from preflight evidence.
/// This service does not install drivers and never converts missing evidence into approval.
/// </summary>
public sealed class DriverInstallationPlanService
{
    public DriverInstallationPlan Create(
        string deviceInstanceId,
        string hardwareId,
        string infPath,
        string expectedVersion,
        DriverPackagePreflightResult preflight,
        DriverPackageSignatureAssessment signature,
        bool verifiedBackupExists,
        bool userConfirmedInstallation)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(deviceInstanceId))
            blockers.Add("Не указан Instance ID целевого устройства.");
        if (string.IsNullOrWhiteSpace(hardwareId))
            blockers.Add("Не указан Hardware ID целевого устройства.");
        if (string.IsNullOrWhiteSpace(infPath) || !File.Exists(infPath))
            blockers.Add("Выбранный INF-файл не найден.");
        if (preflight is null || !preflight.HasExactHardwareIdMatch)
            blockers.Add("Предварительная проверка не подтвердила точное совпадение Hardware ID.");

        var selectedInf = preflight?.InfResults.FirstOrDefault(x =>
            x.IsReadable && x.HasExactHardwareIdMatch &&
            PathsEqual(x.InfPath, infPath));

        if (selectedInf is null)
            blockers.Add("Выбранный INF не входит в список файлов с точным совпадением Hardware ID.");
        else
        {
            if (!string.IsNullOrWhiteSpace(selectedInf.CatalogFile) && !selectedInf.CatalogExists)
                blockers.Add("Для INF указан CAT-файл, но он отсутствует.");
            if (selectedInf.CatalogExists &&
                selectedInf.CatalogCertificateStatus != "EmbeddedSignerCertificatePresent")
                warnings.Add("Сведения о сертификате CAT не подтверждают доверенную подпись каталога.");
        }

        if (signature is null || !signature.IsTrusted)
            blockers.Add("Политика доверия не подтвердила сертификат издателя CAT-файла.");
        if (!verifiedBackupExists)
            blockers.Add("Нет подтверждённой резервной копии текущего драйвера.");
        if (!userConfirmedInstallation)
            blockers.Add("Пользователь ещё не подтвердил установку.");

        var canInstall = blockers.Count == 0;
        return new DriverInstallationPlan(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            deviceInstanceId,
            hardwareId,
            Path.GetFullPath(infPath ?? ""),
            expectedVersion ?? "",
            canInstall,
            canInstall ? "ReadyForExplicitInstallation" : "Blocked",
            blockers,
            warnings,
            canInstall
                ? "Все обязательные предварительные условия выполнены. Установка должна выполняться отдельным действием с отображением результата Windows."
                : "Установка заблокирована до устранения всех обязательных замечаний.");
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }
}

public sealed record DriverInstallationPlan(
    string PlanId,
    DateTimeOffset CreatedUtc,
    string DeviceInstanceId,
    string HardwareId,
    string InfPath,
    string ExpectedVersion,
    bool CanProceed,
    string Status,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    string Summary);
