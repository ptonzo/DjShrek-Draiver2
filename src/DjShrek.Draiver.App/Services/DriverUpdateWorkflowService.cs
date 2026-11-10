using DjShrek.Draiver.App.Models;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Coordinates discovery, durable candidate tracking and package staging.
/// It never installs a driver; installation remains a separate explicit operation.
/// </summary>
public sealed class DriverUpdateWorkflowService
{
    private readonly OfficialDriverPackageCandidateService _discovery;
    private readonly DriverPackageIntakeService _intake;
    private readonly DriverUpdateCatalogService _catalog;

    public DriverUpdateWorkflowService(
        OfficialDriverPackageCandidateService? discovery = null,
        DriverPackageIntakeService? intake = null,
        DriverUpdateCatalogService? catalog = null)
    {
        _discovery = discovery ?? new OfficialDriverPackageCandidateService();
        _intake = intake ?? new DriverPackageIntakeService();
        _catalog = catalog ?? new DriverUpdateCatalogService();
    }

    public Task<IReadOnlyList<DriverUpdateRecord>> GetQueueAsync(CancellationToken token = default) =>
        _catalog.GetRecordsAsync(token);

    public Task<IReadOnlyList<DriverBackupRecord>> GetBackupsAsync(CancellationToken token = default) =>
        _catalog.GetBackupsAsync(token);

    public async Task<IReadOnlyList<DriverUpdateRecord>> DiscoverForDeviceAsync(
        DeviceInfo device, IEnumerable<InternetDriverSearchResult> officialPages,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var results = new List<DriverUpdateRecord>();
        var pages = officialPages.Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).Take(20);
        foreach (var page in pages)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<OfficialDriverPackageCandidate> candidates;
            try { candidates = await _discovery.DiscoverAsync(page, token); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                await _catalog.UpsertCandidateAsync(device, page.Manufacturer,
                    "Страница производителя недоступна: " + page.Url, page.Url, page.Url, token: token);
                continue;
            }

            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                results.Add(await _catalog.UpsertCandidateAsync(device, candidate.Provider,
                    candidate.Title, candidate.Url, candidate.SourcePage, token: token));
            }
        }
        return results;
    }

    public async Task<DriverPackageStageResult> AcquireCandidateAsync(
        string recordId, DeviceInfo device, string destinationRoot,
        CancellationToken token = default)
    {
        var record = (await _catalog.GetRecordsAsync(token)).FirstOrDefault(x => x.Id == recordId)
            ?? throw new KeyNotFoundException("Кандидат не найден в каталоге обновлений.");
        if (!string.Equals(record.DeviceInstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выбранный кандидат относится к другому устройству.");
        if (!Uri.TryCreate(record.PackageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Адрес пакета не прошёл проверку HTTPS.");

        await _catalog.SetStateAsync(recordId, DriverCandidateState.Downloading, token: token);
        try
        {
            var candidate = new OfficialDriverPackageCandidate(record.PackageTitle, record.PackageUrl,
                record.Provider, record.SourcePage, Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToUpperInvariant(),
                record.HardwareIds.FirstOrDefault() ?? "", "Кандидат из постоянного каталога обновлений.");
            var stage = await _intake.AcquireAndAnalyzeAsync(candidate, device, destinationRoot, token);
            await _catalog.UpdateStageAsync(recordId, stage, token);
            return stage;
        }
        catch (OperationCanceledException)
        {
            await _catalog.SetStateAsync(recordId, DriverCandidateState.Failed,
                new[] { "Операция отменена пользователем." }, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await _catalog.SetStateAsync(recordId, DriverCandidateState.Failed,
                new[] { ex.Message }, CancellationToken.None);
            throw;
        }
    }

    public async Task<DriverBackupRecord> CreateBackupRecordAsync(string backupDirectory,
        string displayName, CancellationToken token = default) =>
        await _catalog.RegisterBackupAsync(backupDirectory, displayName, token);

    public Task<IReadOnlyList<string>> VerifyBackupAsync(string backupDirectory,
        CancellationToken token = default) => _catalog.VerifyBackupAsync(backupDirectory, token);

    public Task<DriverBackupRestoreResult> RestoreBackupAsync(DriverBackupRecord backup,
        bool userConfirmed, CancellationToken token = default) =>
        new DriverBackupRestoreService(_catalog).RestoreAsync(backup, userConfirmed, token);

    public Task<IReadOnlyList<DriverBackupRestoreResult>> GetBackupRestoreHistoryAsync(
        CancellationToken token = default) =>
        new DriverBackupRestoreService(_catalog).GetHistoryAsync(token);

    public Task<DriverActivationReconciliation> ReconcileActivationAsync(
        string instanceId, string expectedProvider, string expectedVersion, string expectedInfName,
        IReadOnlyCollection<DeviceInfo> currentDevices, CancellationToken token = default) =>
        new DriverActivationReconciliationService().ReconcileAsync(
            instanceId, expectedProvider, expectedVersion, expectedInfName, currentDevices, token);

    public Task<IReadOnlyList<DriverActivationReconciliation>> GetActivationHistoryAsync(
        CancellationToken token = default) =>
        new DriverActivationReconciliationService().GetHistoryAsync(token);

    public IReadOnlyList<DriverCandidateRanking> RankCandidates(
        DeviceInfo device, IEnumerable<DriverCandidateForRanking> candidates,
        string architecture, int windowsBuild) =>
        new DriverCandidateRankingService().Rank(device, candidates, architecture, windowsBuild);

    public Task SetCandidateStateAsync(string recordId, DriverCandidateState state,
        IEnumerable<string>? reasons = null, CancellationToken token = default) =>
        _catalog.SetStateAsync(recordId, state, reasons, token);
}
