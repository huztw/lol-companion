namespace LoLCompanion.Core.Lcu;

public static class LcuLockfileParser
{
    public static LcuCredential Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw Invalid("Lockfile is empty.");
        }

        var parts = content.Trim().Split(':');
        if (parts.Length != 5)
        {
            throw Invalid("Lockfile format is invalid.");
        }

        if (string.IsNullOrWhiteSpace(parts[0]))
        {
            throw Invalid("Lockfile client name is missing.");
        }

        if (!int.TryParse(parts[1], out var processId) || processId <= 0)
        {
            throw Invalid("Lockfile process id is invalid.");
        }

        if (!int.TryParse(parts[2], out var port) || port is < 1 or > 65535)
        {
            throw Invalid("Lockfile port is invalid.");
        }

        if (string.IsNullOrWhiteSpace(parts[3]))
        {
            throw Invalid("Lockfile password is missing.");
        }

        if (!string.Equals(parts[4], "https", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("Lockfile protocol is invalid.");
        }

        return new LcuCredential(processId, "127.0.0.1", port, parts[4], parts[3]);
    }

    private static LcuException Invalid(string message) => new("lockfile_invalid", message, isRecoverable: true);
}
