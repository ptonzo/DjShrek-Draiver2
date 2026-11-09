namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Coordinates one approved installation and a caller-supplied fresh device inventory refresh.
/// The refresh callback must obtain a new Windows device snapshot, not reuse the pre-install snapshot.
/// </summary>
public sealed class DriverInstallationWorkflowService
{
    private readonly DriverInstallExecutionService _executor;
    private readonly DriverInstallationOutcomeVerificationService _verifier;

    public DriverInstallationWorkflowService(
        DriverInstallExecutionService? executor = null,
        DriverInstallationOutcomeVerificationService? verifier = null)
    {
        _executor = executor ?? new DriverInstallExecutionService();
        _verifier = verifier ?? new DriverInstallationOutcomeVerificationService();
    }

    public async Task<DriverInstallationWorkflowResult> InstallAndVerifyAsync(
        DriverInstallationPlan plan,
        string packageRoot,
        bool userConfirmed,
        string previousVersion,
        Func<CancellationToken, Task<IEnumerable<Models.DeviceInfo>>> refreshDeviceInventory,
        TimeSpan? executionTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (refreshDeviceInventory is null)
            return DriverInstallationWorkflowResult.NotStarted(
                "Не задан источник обновлённого снимка устройств.");

        var execution = await _executor.ExecuteAsync(
            plan, packageRoot, userConfirmed, executionTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!execution.Started ||
            execution.Cancelled ||
            execution.TimedOut ||
            execution.ExitCode != 0)
        {
            return new DriverInstallationWorkflowResult(
                execution, null, false, execution.Status, execution.Summary);
        }

        IEnumerable<Models.DeviceInfo> inventory;
        try
        {
            inventory = await refreshDeviceInventory(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DriverInstallationWorkflowResult(
                execution, null, false, "VerificationCancelled",
                "Команда установки завершилась, но обновление списка устройств было отменено. Результат не подтверждён.");
        }
        catch (Exception ex)
        {
            return new DriverInstallationWorkflowResult(
                execution, null, false, "InventoryRefreshFailed",
                $"Команда установки завершилась, но не удалось обновить список устройств: {ex.Message}");
        }

        var verification = _verifier.Verify(
            plan.DeviceInstanceId,
            plan.ExpectedVersion,
            previousVersion,
            execution,
            inventory);

        return new DriverInstallationWorkflowResult(
            execution,
            verification,
            verification.IsVerified,
            verification.Status,
            verification.Summary);
    }
}

public sealed record DriverInstallationWorkflowResult(
    DriverInstallExecutionResult Execution,
    DriverInstallationOutcomeVerification? Verification,
    bool IsSuccessful,
    string Status,
    string Summary)
{
    public static DriverInstallationWorkflowResult NotStarted(string reason)
    {
        var execution = DriverInstallExecutionResult.NotStarted(reason);
        return new DriverInstallationWorkflowResult(
            execution, null, false, "NotStarted", reason);
    }
}
