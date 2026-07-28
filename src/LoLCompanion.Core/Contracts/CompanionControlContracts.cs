namespace LoLCompanion.Core.Contracts;

public static class CompanionRemoteControlContract
{
    public const string ProtocolVersion = "remote-control-v1";
    public const int PollIntervalSeconds = 15;
    public const int MaxRecentMatches = 20;
}

public sealed record CompanionRemoteControlCompatibilityContract(
    string CurrentProtocolVersion,
    string MinimumProtocolVersion,
    int PollIntervalSeconds);

public sealed record CompanionControlJobDto(
    string ProtocolVersion,
    string ControlJobId,
    string Type,
    long? GameId,
    int? QueueId,
    string? GameMode,
    string? GameType,
    DateTimeOffset? CreatedAt,
    int? DurationSeconds,
    bool? Win,
    int? ChampionId,
    string? ChampionName,
    int? Kills,
    int? Deaths,
    int? Assists,
    bool? IsSupported,
    string? UnsupportedReason);

public sealed record CompanionControlResultDto(
    string State,
    string? FailureCategory = null,
    string? Message = null,
    IReadOnlyList<CompanionRecentMatchDto>? Matches = null,
    string? AnalysisJobId = null);

public sealed record CompanionRecentMatchDto(
    long GameId,
    int QueueId,
    string GameMode,
    string GameType,
    DateTimeOffset CreatedAt,
    int DurationSeconds,
    bool Win,
    int ChampionId,
    string ChampionName,
    int Kills,
    int Deaths,
    int Assists,
    bool Supported,
    string? UnsupportedReason);
