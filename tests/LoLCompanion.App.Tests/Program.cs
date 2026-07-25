using LoLCompanion.App;

var defaultOptions = CompanionAppOptions.Load(_ => null);
Assert(
    defaultOptions.ApiBaseAddress.AbsoluteUri ==
    "https://aram-discord-api-891336206880.asia-east1.run.app/",
    "Expected default API base address.");

var blankOptions = CompanionAppOptions.Load(_ => "   ");
Assert(
    blankOptions.ApiBaseAddress.AbsoluteUri ==
    "https://aram-discord-api-891336206880.asia-east1.run.app/",
    "Expected blank env to use default.");

var trimmedOverride = CompanionAppOptions.Load(name =>
    name == CompanionAppOptions.ApiBaseUrlEnvironmentVariable
        ? "   https://example.test/api   "
        : null);
Assert(
    trimmedOverride.ApiBaseAddress.AbsoluteUri == "https://example.test/api/",
    "Expected trimmed override with trailing slash.");

var pathOverride = CompanionAppOptions.Load(name =>
    name == CompanionAppOptions.ApiBaseUrlEnvironmentVariable
        ? "https://example.test/api/v1"
        : null);
Assert(
    pathOverride.ApiBaseAddress.AbsoluteUri == "https://example.test/api/v1/",
    "Expected base path to be preserved and normalized.");

AssertInvalid("http://example.test/");
AssertInvalid("/relative");
AssertInvalid("https://user:pass@example.test/");
AssertInvalid("https://example.test/?q=1");
AssertInvalid("https://example.test/#frag");

const string secret = "apikey-123";
try
{
    CompanionAppOptions.Load(name =>
        name == CompanionAppOptions.ApiBaseUrlEnvironmentVariable
            ? $"https://user:{secret}@example.test/"
            : null);
    throw new InvalidOperationException("Expected invalid configuration.");
}
catch (InvalidOperationException exception)
{
    Assert(
        !exception.Message.Contains(secret, StringComparison.Ordinal),
        "Expected error message to avoid echoing secret input.");
}

Console.WriteLine("LoL Companion app options tests passed.");

static void AssertInvalid(string value)
{
    try
    {
        CompanionAppOptions.Load(name =>
            name == CompanionAppOptions.ApiBaseUrlEnvironmentVariable ? value : null);
        throw new InvalidOperationException("Expected invalid configuration.");
    }
    catch (InvalidOperationException exception)
    {
        Assert(
            !exception.Message.Contains(value, StringComparison.Ordinal),
            "Expected error message to avoid echoing input.");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
