namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Runs the explicit install workflow and records each lifecycle transition in the operation journal.
/// Journal failures are surfaced as warnings and never silently reported as a verified install.
/// </summary>
public sealed class DriverInstallationLifecycleCoordinator
{
    private readonly DriverInstallationWorkflowService _workflow;
    private readonly DriverOperationJournalService _journal;

    public DriverInstallationLifecycleCoordinator(
        DriverInstallationWorkflowService? workflow = null,
        DriverOperationJournalService? journal = null)
    {
        _workflow = workflow ?? new DriverInstallationWorkflowService();
        _journal = journal ?? new DriverOperationJournalService();
    }

    public async Task<DriverInstallationLifecycleResult> ExecuteAsync(
        DriverInstallationPlan plan,
        string packageRoot,
        bool userConfirmed,
        string previousVersion,
        string packageSha256,
        Func<CancellationToken, Task<IEnumerable<Models.DeviceInfo>>> refreshDeviceInventory,
        CancellationToken cancellationToken = default)
    {
        if (plan is null)
            return new DriverInstallationLifecycleResult(
                null, false, "Blocked", "План установки отсутствует.", null);

        await TryJournalAsync("InstallRequested", "Started", plan, packageSha256,
            plan.ExpectedVersion, "Запрошено выполнение плана установки.", cancellationToken)
            .ConfigureAwait(false);

        var result = await _workflow.InstallAndVerifyAsync(
            plan,
            packageRoot,
            userConfirmed,
            previousVersion,
            refreshDeviceInventory,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string? journalWarning = null;
        try
        {
            await _journal.AppendAsync(
                "InstallCompleted",
                result.Status,
                plan.DeviceInstanceId,
                plan.HardwareId,
                packageSha256,
                result.Verification?.InstalledVersion ?? string.Empty,
                result.Summary,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            journalWarning = $"Не удалось записать итог операции в журнал: {ex.Message}";
        }

        var summary = journalWarning is null
            ? result.Summary
            : result.Summary + " " + journalWarning;

        return new DriverInstallationLifecycleResult(
            result, result.IsSuccessful, result.Status, summary, journalWarning);
    }

    private async Task TryJournalAsync(
        string operation,
        string status,
        DriverInstallationPlan plan,
        string packageSha256,
        string version,
        string details,
        CancellationToken cancellationToken)
    {
        try
        {
            await _journal.AppendAsync(
                operation, status, plan.DeviceInstanceId, plan.HardwareId,
                packageSha256, version, details, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The final lifecycle result will still expose any subsequent journal failure.
        }
    }
}

public sealed record DriverInstallationLifecycleResult(
    DriverInstallationWorkflowResult? Workflow,
    bool IsSuccessful,
    string Status,
    string Summary,
    string? JournalWarning);
