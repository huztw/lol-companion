namespace LoLCompanion.Core.Lcu;

public sealed record LcuCurrentSummoner(long SummonerId, long? AccountId, string DisplayName, string Puuid);

public sealed record LcuRecentMatchSummary(
    long GameId,
    int QueueId,
    string GameMode,
    string GameType,
    DateTimeOffset CreatedAt,
    TimeSpan Duration,
    bool Win,
    int ChampionId,
    string? ChampionName,
    int Kills,
    int Deaths,
    int Assists,
    bool IsSupported,
    string? UnsupportedReason
);

public sealed record LcuMatchDetailDto(
    long GameId,
    int QueueId,
    string GameMode,
    string GameType,
    DateTimeOffset GameCreation,
    TimeSpan GameDuration,
    IReadOnlyList<LcuMatchParticipantDto> Participants,
    string? GameDataVersion = null
);

public sealed record LcuMatchParticipantDto(
    string Puuid,
    string? RiotIdGameName,
    string? RiotIdTagline,
    int ParticipantId,
    int TeamId,
    bool Win,
    int ChampionId,
    string? ChampionName,
    int Kills,
    int Deaths,
    int Assists,
    double? TotalDamageDealtToChampions,
    double? TotalDamageTaken,
    double? TimeCCingOthers,
    double? TotalHealsOnTeammates,
    double? TotalDamageShieldedOnTeammates,
    IReadOnlyList<int>? Items = null,
    IReadOnlyList<int>? Augments = null
);

public sealed record LcuTimelineDto(
    IReadOnlyList<LcuTimelineFrameDto> Frames,
    IReadOnlyList<LcuTimelineEventDto> Events
);

public sealed record LcuTimelineFrameDto(long Timestamp, IReadOnlyDictionary<int, double> ParticipantGoldById);

public sealed record LcuTimelineEventDto(
    string Type,
    long Timestamp,
    int? KillerId,
    int? VictimId,
    int? ParticipantId,
    IReadOnlyList<int> AssistingParticipantIds,
    int? TeamId = null,
    string? BuildingType = null,
    string? TowerType = null,
    string? LaneType = null
);

public sealed record LcuTimelineResult(bool IsAvailable, LcuTimelineDto? Timeline, string? UnavailableReason)
{
    public static LcuTimelineResult Available(LcuTimelineDto timeline) => new(true, timeline, null);

    public static LcuTimelineResult Unavailable(string reason) => new(false, null, reason);
}
