namespace LoLCompanion.Core.Contracts;

public static class CompanionAnalysisContract
{
    public const int SchemaVersion = 1;
    public const int MaxRequestBytes = 256 * 1024;
}

public sealed record CompanionAnalysisSubmitRequest(
    string RequestId,
    long GameId,
    int SchemaVersion,
    int QueueId,
    CompanionAnalysisPayloadV1 Payload
);

public sealed record CompanionAnalysisSubmitResponse(
    string JobId,
    bool Duplicate
);

public sealed record CompanionAnalysisPayloadV1(
    string RequestedParticipantPuuid,
    IReadOnlyList<CompanionAnalysisParticipantV1> Participants,
    CompanionAnalysisMatchV1 Match,
    CompanionAnalysisTimelineV1? Timeline,
    string? TimelineUnavailableReason
);

public sealed record CompanionAnalysisParticipantV1(
    string Puuid,
    string RiotIdGameName,
    string RiotIdTagline,
    int ParticipantId,
    int TeamId,
    bool Win,
    string ChampionName,
    int Kills,
    int Deaths,
    int Assists,
    double? TotalDamageDealtToChampions,
    double? TotalDamageTaken,
    double? TimeCCingOthers,
    double? TotalHealsOnTeammates,
    double? TotalDamageShieldedOnTeammates
);

public sealed record CompanionAnalysisMatchV1(string MatchId);

public sealed record CompanionAnalysisTimelineV1(
    IReadOnlyList<CompanionAnalysisTimelineFrameV1> Frames,
    IReadOnlyList<CompanionAnalysisTimelineEventV1> Events
);

public sealed record CompanionAnalysisTimelineFrameV1(
    long Timestamp,
    IReadOnlyDictionary<string, CompanionAnalysisParticipantFrameV1> ParticipantFrames
);

public sealed record CompanionAnalysisParticipantFrameV1(double TotalGold);

public sealed record CompanionAnalysisTimelineEventV1(
    string Type,
    long Timestamp,
    int? KillerId,
    int? VictimId,
    int? ParticipantId,
    IReadOnlyList<int> AssistingParticipantIds,
    string? BuildingType
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
    CompanionDownloadContract Current
);

public sealed record CompanionDownloadContract(
    string LatestVersion,
    string DownloadUrl,
    string Sha256
);
