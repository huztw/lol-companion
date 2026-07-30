namespace LoLCompanion.Core.Contracts;

public static class CompanionAnalysisContract
{
    public const int SchemaVersion = 5;
    public const int MaxRequestBytes = 256 * 1024;
}

public sealed record CompanionAnalysisSubmitRequest(
    string RequestId,
    long GameId,
    int SchemaVersion,
    int QueueId,
    CompanionAnalysisPayloadV2 Payload
);

public sealed record CompanionAnalysisSubmitResponse(
    string JobId,
    bool Duplicate
);

public sealed record CompanionAnalysisPayloadV2(
    string RequestedParticipantPuuid,
    IReadOnlyList<CompanionAnalysisParticipantV2> Participants,
    CompanionAnalysisMatchV2 Match,
    CompanionAnalysisTimelineV2? Timeline,
    string? TimelineUnavailableReason
);

public sealed record CompanionAnalysisParticipantV2(
    string Puuid,
    string RiotIdGameName,
    string RiotIdTagline,
    int ParticipantId,
    int TeamId,
    bool Win,
    int ChampionId,
    string ChampionName,
    int Kills,
    int Deaths,
    int Assists,
    double? TotalDamageDealtToChampions,
    double? TotalDamageTaken,
    double? TimeCCingOthers,
    double? TotalHealsOnTeammates,
    double? TotalDamageShieldedOnTeammates,
    IReadOnlyList<int> Items,
    IReadOnlyList<int> Augments,
    double? TotalHeal = null,
    double? DamageSelfMitigated = null,
    double? DamageDealtToTurrets = null,
    double? DamageDealtToObjectives = null,
    double? TotalTimeCrowdControlDealt = null,
    int? ChampionLevel = null
);

public sealed record CompanionAnalysisMatchV2(string MatchId, string GameDataVersion);

public sealed record CompanionAnalysisTimelineV2(
    IReadOnlyList<CompanionAnalysisTimelineFrameV2> Frames,
    IReadOnlyList<CompanionAnalysisTimelineEventV2> Events
);

public sealed record CompanionAnalysisTimelineFrameV2(
    long Timestamp,
    IReadOnlyDictionary<string, CompanionAnalysisParticipantFrameV2> ParticipantFrames
);

public sealed record CompanionAnalysisParticipantFrameV2(double TotalGold);

public sealed record CompanionAnalysisTimelineEventV2(
    string Type,
    long Timestamp,
    int? KillerId,
    int? VictimId,
    int? ParticipantId,
    IReadOnlyList<int> AssistingParticipantIds,
    int? TeamId,
    string? BuildingType,
    string? TowerType,
    string? LaneType
);

public sealed record CompanionAnalysisStatusDtoV1(
    int SchemaVersion,
    string JobId,
    string State,
    string CreatedAt,
    string? CompletedAt,
    bool ReportAvailable,
    string DeliveryState,
    string UserAction
);

public sealed record CompanionVersionDtoV1(
    int SchemaVersion,
    CompanionDownloadContract Current,
    CompanionAnalysisCompatibilityContract? Analysis,
    CompanionRemoteControlCompatibilityContract? RemoteControl = null
);

public sealed record CompanionAnalysisCompatibilityContract(
    int CurrentSchemaVersion,
    int MinimumSchemaVersion
);

public sealed record CompanionDownloadContract(
    string LatestVersion,
    string DownloadUrl,
    string Sha256
);
