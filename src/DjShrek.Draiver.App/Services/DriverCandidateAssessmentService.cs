using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Produces an explainable, conservative assessment for each catalog result.
/// Catalog metadata alone never enables installation.
/// </summary>
public sealed class DriverCandidateAssessmentService
{
    public void Assess(DriverUpdateCandidate candidate, DeviceInfo? device)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var reasons = new List<string>();

        if (device is null)
        {
            candidate.VersionAssessment = "Устройство не сопоставлено";
            candidate.OsCompatibilityAssessment = "Не определена";
            candidate.ArchitectureAssessment = "Не определена";
            reasons.Add("Нет точной связи кандидата с обнаруженным устройством.");
        }
        else
        {
            var comparison = DriverVersionComparisonService.Compare(
                candidate.AvailableVersion, device.DriverVersion);
            candidate.VersionAssessment = comparison.Message;
            if (comparison.Relation == DriverVersionRelation.Older)
                reasons.Add("Версия кандидата ниже установленной; downgrade не рекомендуется.");
            else if (comparison.Relation == DriverVersionRelation.Same)
                reasons.Add("Версия совпадает с установленной; обновление не требуется.");
            else if (comparison.Relation == DriverVersionRelation.Unknown)
                reasons.Add("Невозможно достоверно сравнить версии.");

            var currentOs = Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";
            candidate.OsCompatibilityAssessment = AssessOperatingSystem(candidate.OperatingSystemTarget, currentOs);
            if (candidate.OsCompatibilityAssessment.StartsWith("Не соответствует", StringComparison.Ordinal))
                reasons.Add("Метаданные каталога указывают на другую версию Windows.");
            else if (candidate.OsCompatibilityAssessment.StartsWith("Не указана", StringComparison.Ordinal))
                reasons.Add("В каталоге нет явных сведений о поддерживаемой Windows.");

            var currentArchitecture = GetCurrentArchitecture();
            candidate.ArchitectureAssessment = AssessArchitecture(candidate.ArchitectureTarget, currentArchitecture);
            if (candidate.ArchitectureAssessment.StartsWith("Не соответствует", StringComparison.Ordinal))
                reasons.Add("Архитектура пакета отличается от архитектуры системы.");
            else if (candidate.ArchitectureAssessment.StartsWith("Не указана", StringComparison.Ordinal))
                reasons.Add("В каталоге нет явных сведений об архитектуре пакета.");
        }

        if (candidate.CandidateScope == "ModelPack")
            reasons.Add("Модельный OEM-пакет не доказывает применимость INF к выбранному устройству.");
        if (candidate.CandidateScope == "SupportPortal")
            reasons.Add("Получена только страница поддержки, конкретный пакет отсутствует.");
        if (string.IsNullOrWhiteSpace(candidate.PackageUrl))
            reasons.Add("Нет прямой ссылки на файл пакета.");
        if (!string.IsNullOrWhiteSpace(candidate.PackageUrl) &&
            (!Uri.TryCreate(candidate.PackageUrl, UriKind.Absolute, out var packageUri) ||
             packageUri.Scheme != Uri.UriSchemeHttps))
            reasons.Add("Ссылка на пакет не является допустимым HTTPS-адресом.");

        candidate.RejectionReasons = string.Join(" ", reasons.Distinct(StringComparer.Ordinal));
        candidate.InstallEligibility = "Заблокирована до локальной проверки INF/CAT, Hardware ID и подписи";
        if (candidate.PackagePreflightStatus.Contains("готов", StringComparison.OrdinalIgnoreCase) &&
            candidate.PackageIntegrityStatus.Contains("совпадает", StringComparison.OrdinalIgnoreCase) &&
            candidate.CandidateScope == "DeviceExact" &&
            candidate.VersionAssessment.StartsWith("Доступная версия", StringComparison.Ordinal))
            candidate.InstallEligibility = "Требуется отдельное подтверждение и проверка установочного сценария";
    }

    public static string AssessOperatingSystem(string target, string current)
    {
        if (string.IsNullOrWhiteSpace(target)) return "Не указана в метаданных";
        if (target.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("Win10", StringComparison.OrdinalIgnoreCase))
            return current.Equals("Windows 10", StringComparison.OrdinalIgnoreCase)
                ? "Соответствует Windows 10" : "Не соответствует: пакет для Windows 10";
        if (target.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) ||
            target.Contains("Win11", StringComparison.OrdinalIgnoreCase))
            return current.Equals("Windows 11", StringComparison.OrdinalIgnoreCase)
                ? "Соответствует Windows 11" : "Не соответствует: пакет для Windows 11";
        return "Неоднозначные метаданные ОС; требуется ручная проверка";
    }

    public static string AssessArchitecture(string target, string current)
    {
        if (string.IsNullOrWhiteSpace(target)) return "Не указана в метаданных";
        if (target.Equals("All", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("Universal", StringComparison.OrdinalIgnoreCase))
            return "Универсальная архитектура по данным каталога";
        return target.Equals(current, StringComparison.OrdinalIgnoreCase)
            ? "Соответствует " + current
            : "Не соответствует: пакет " + target + ", система " + current;
    }

    public static string GetCurrentArchitecture() =>
        Environment.Is64BitOperatingSystem
            ? (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
               System.Runtime.InteropServices.Architecture.Arm64 ? "ARM64" : "x64")
            : "x86";
}
