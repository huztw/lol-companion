using System.Diagnostics;

namespace LoLCompanion.Core.Lcu;

public enum LcuDiscoveryStatus
{
    Found,
    NotFound,
    Unreadable
}

public sealed record LcuLockfileDiscoveryResult(
    LcuDiscoveryStatus Status,
    LcuCredential? Credential,
    string? LockfilePath,
    string Message
);

public interface ILcuLockfileDiscovery
{
    Task<LcuLockfileDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default);
}

public interface ILcuFileSystem
{
    bool FileExists(string path);

    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);
}

public interface ILeagueProcessLocator
{
    IReadOnlyList<string> GetExecutablePaths();
}

public sealed class SystemLcuFileSystem : ILcuFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(path, cancellationToken);
}

public sealed class LeagueClientProcessLocator : ILeagueProcessLocator
{
    public IReadOnlyList<string> GetExecutablePaths()
    {
        var paths = new List<string>();
        foreach (var process in Process.GetProcessesByName("LeagueClientUx"))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(process.MainModule?.FileName))
                {
                    paths.Add(process.MainModule.FileName);
                }
            }
            catch
            {
                // Ignore inaccessible process metadata and continue with remaining candidates.
            }
            finally
            {
                process.Dispose();
            }
        }

        return paths;
    }
}

public sealed class LcuLockfileDiscoveryOptions
{
    public IReadOnlyList<string> ExplicitCandidates { get; init; } = [];

    public IReadOnlyList<string> InstallCandidates { get; init; } =
    [
        @"C:\Riot Games\League of Legends",
        @"C:\Program Files\Riot Games\League of Legends",
        @"C:\Program Files (x86)\Riot Games\League of Legends"
    ];
}

public sealed class LcuLockfileDiscovery : ILcuLockfileDiscovery
{
    private readonly ILcuFileSystem _fileSystem;
    private readonly ILeagueProcessLocator _processLocator;
    private readonly LcuLockfileDiscoveryOptions _options;

    public LcuLockfileDiscovery(
        ILcuFileSystem fileSystem,
        ILeagueProcessLocator processLocator,
        LcuLockfileDiscoveryOptions? options = null)
    {
        _fileSystem = fileSystem;
        _processLocator = processLocator;
        _options = options ?? new LcuLockfileDiscoveryOptions();
    }

    public async Task<LcuLockfileDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var unreadableCount = 0;

        foreach (var candidate in EnumerateCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_fileSystem.FileExists(candidate))
            {
                continue;
            }

            try
            {
                var content = await _fileSystem.ReadAllTextAsync(candidate, cancellationToken);
                var credential = LcuLockfileParser.Parse(content);
                return new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, credential, candidate, "LCU lockfile discovered.");
            }
            catch (LcuException)
            {
                unreadableCount++;
            }
            catch (IOException)
            {
                unreadableCount++;
            }
            catch (UnauthorizedAccessException)
            {
                unreadableCount++;
            }
        }

        return unreadableCount > 0
            ? new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Unreadable, null, null, "LCU lockfile is unavailable.")
            : new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.NotFound, null, null, "League Client lockfile was not found.");
    }

    private IEnumerable<string> EnumerateCandidates()
    {
        foreach (var candidate in _options.ExplicitCandidates)
        {
            yield return ToLockfilePath(candidate);
        }

        foreach (var executablePath in _processLocator.GetExecutablePaths())
        {
            yield return ToLockfilePath(executablePath);
        }

        foreach (var candidate in _options.InstallCandidates)
        {
            yield return ToLockfilePath(candidate);
        }
    }

    private static string ToLockfilePath(string candidate)
    {
        if (candidate.EndsWith("lockfile", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(Path.GetDirectoryName(candidate) ?? string.Empty, "lockfile");
        }

        return Path.Combine(candidate, "lockfile");
    }
}
