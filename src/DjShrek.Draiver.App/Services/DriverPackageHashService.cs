using System.Security.Cryptography;

namespace DjShrek.Draiver.App.Services;

public sealed record DriverFileHash(string FilePath, string Sha256, long SizeBytes);

/// <summary>Calculates package file hashes and verifies a supplied SHA-256 digest.</summary>
public sealed class DriverPackageHashService
{
    public async Task<DriverFileHash> CalculateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return new DriverFileHash(fullPath, Convert.ToHexString(hash), stream.Length);
    }

    public async Task<bool> VerifyAsync(string filePath, string expectedSha256, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) ||
            expectedSha256.Length != 64 ||
            !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Ожидаемый SHA-256 должен содержать 64 шестнадцатеричных символа.", nameof(expectedSha256));

        var actual = await CalculateAsync(filePath, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual.Sha256), Convert.FromHexString(expectedSha256));
    }

    public async Task<IReadOnlyList<DriverFileHash>> CalculateFolderAsync(
        string folder, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => new FileInfo(path).Length <= 2L * 1024 * 1024 * 1024)
            .Take(501).ToArray();
        if (files.Length > 500) throw new InvalidDataException("В папке более 500 файлов. Выберите более узкую папку.");
        var results = new List<DriverFileHash>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { results.Add(await CalculateAsync(file, cancellationToken)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return results;
    }
}
