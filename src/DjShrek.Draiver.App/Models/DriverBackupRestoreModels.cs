namespace DjShrek.Draiver.App.Models;

public sealed class DriverBackupRestoreResult
{
    public string BackupName { get; set; } = "";
    public string BackupDirectory { get; set; } = "";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    public string Outcome { get; set; } = "Не запускалось";
    public int? ExitCode { get; set; }
    public int InfPackageCount { get; set; }
    public List<string> VerifiedPackages { get; set; } = new();
    public List<string> Issues { get; set; } = new();
    public string Details { get; set; } = "";
}

public sealed class DriverBackupRestoreHistory
{
    public List<DriverBackupRestoreResult> Records { get; set; } = new();
}
