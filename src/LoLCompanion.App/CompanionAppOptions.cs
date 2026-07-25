namespace LoLCompanion.App;

public sealed record CompanionAppOptions(Uri ApiBaseAddress)
{
    public const string ApiBaseUrlEnvironmentVariable = "LOL_COMPANION_API_BASE_URL";
    public static readonly Uri DefaultApiBaseAddress =
        new("https://aram-discord-api-891336206880.asia-east1.run.app/");

    public static CompanionAppOptions Load() => Load(Environment.GetEnvironmentVariable);

    public static CompanionAppOptions Load(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var rawValue = getEnvironmentVariable(ApiBaseUrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new CompanionAppOptions(DefaultApiBaseAddress);
        }

        if (!Uri.TryCreate(rawValue.Trim(), UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            throw InvalidConfiguration();
        }

        var builder = new UriBuilder(parsed)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = parsed.IsDefaultPort ? -1 : parsed.Port,
            Path = NormalizeBasePath(parsed.AbsolutePath)
        };

        return new CompanionAppOptions(builder.Uri);
    }

    private static string NormalizeBasePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return "/";
        }

        return path.EndsWith("/", StringComparison.Ordinal) ? path : path + "/";
    }

    private static InvalidOperationException InvalidConfiguration() =>
        new($"Invalid {ApiBaseUrlEnvironmentVariable} configuration.");
}
