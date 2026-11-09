using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DjShrek.Draiver.App.Services;

/// <summary>
/// Append-only, hash-chained local journal for driver lifecycle operations.
/// Entries intentionally contain identifiers and outcomes, not package contents or credentials.
/// </summary>
public sealed class DriverOperationJournalService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _journalPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DriverOperationJournalService(string? journalPath = null)
    {
        _journalPath = string.IsNullOrWhiteSpace(journalPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DjShrek-Draiver",
                "Logs",
                "driver-operations.jsonl")
            : Path.GetFullPath(journalPath);
    }

    public async Task<DriverOperationJournalEntry> AppendAsync(
        string operation,
        string status,
        string deviceInstanceId,
        string hardwareId,
        string? packageSha256 = null,
        string? driverVersion = null,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("Operation is required.", nameof(operation));
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("Status is required.", nameof(status));

        var safeDetails = Sanitize(details, 2000);
        var safePackageHash = NormalizeSha256(packageSha256);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_journalPath)
                ?? throw new InvalidOperationException("Journal directory is unavailable.");
            Directory.CreateDirectory(directory);

            var previousHash = await ReadLastHashAsync(cancellationToken).ConfigureAwait(false);
            var entry = new DriverOperationJournalEntry(
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                Sanitize(operation, 80),
                Sanitize(status, 80),
                Sanitize(deviceInstanceId, 512),
                Sanitize(hardwareId, 512),
                safePackageHash,
                Sanitize(driverVersion, 128),
                safeDetails,
                previousHash,
                string.Empty);

            var canonical = JsonSerializer.Serialize(entry with { EntrySha256 = string.Empty }, JsonOptions);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            entry = entry with { EntrySha256 = hash };

            await using var stream = new FileStream(
                _journalPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(JsonSerializer.Serialize(entry, JsonOptions)).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DriverOperationJournalVerification> VerifyAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_journalPath))
                return new DriverOperationJournalVerification(true, 0, null, "Журнал пока пуст.");

            string expectedPrevious = string.Empty;
            var lineNumber = 0;
            await using var stream = new FileStream(
                _journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                DriverOperationJournalEntry? entry;
                try { entry = JsonSerializer.Deserialize<DriverOperationJournalEntry>(line, JsonOptions); }
                catch (JsonException)
                {
                    return new DriverOperationJournalVerification(false, lineNumber, lineNumber,
                        "Повреждена запись журнала.");
                }

                if (entry is null || !string.Equals(entry.PreviousEntrySha256, expectedPrevious, StringComparison.Ordinal))
                    return new DriverOperationJournalVerification(false, lineNumber, lineNumber,
                        "Нарушена цепочка целостности журнала.");

                var canonical = JsonSerializer.Serialize(entry with { EntrySha256 = string.Empty }, JsonOptions);
                var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
                if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualHash),
                    Encoding.ASCII.GetBytes(entry.EntrySha256 ?? string.Empty)))
                    return new DriverOperationJournalVerification(false, lineNumber, lineNumber,
                        "Контрольная сумма записи не совпадает.");

                expectedPrevious = entry.EntrySha256;
            }

            return new DriverOperationJournalVerification(true, lineNumber, null,
                $"Проверено записей: {lineNumber}. Цепочка контрольных сумм цела.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> ReadLastHashAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_journalPath))
            return string.Empty;

        string? last = null;
        await using var stream = new FileStream(
            _journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<DriverOperationJournalEntry>(line, JsonOptions);
                if (entry is not null) last = entry.EntrySha256;
            }
            catch (JsonException) { throw new InvalidDataException("Журнал повреждён; запись остановлена, чтобы не продолжать нарушенную цепочку."); }
        }
        return last ?? string.Empty;
    }

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("Package SHA-256 must contain exactly 64 hexadecimal characters.", nameof(value));
        return normalized;
    }

    private static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }
}

public sealed record DriverOperationJournalEntry(
    string EntryId,
    DateTimeOffset TimestampUtc,
    string Operation,
    string Status,
    string DeviceInstanceId,
    string HardwareId,
    string? PackageSha256,
    string DriverVersion,
    string Details,
    string PreviousEntrySha256,
    string EntrySha256);

public sealed record DriverOperationJournalVerification(
    bool IsValid,
    int EntriesChecked,
    int? FirstInvalidLine,
    string Summary);
