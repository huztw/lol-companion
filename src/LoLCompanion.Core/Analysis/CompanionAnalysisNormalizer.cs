using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoLCompanion.Core.Contracts;
using LoLCompanion.Core.Lcu;

namespace LoLCompanion.Core.Analysis;

public sealed class CompanionAnalysisNormalizer
{
    private const int SupportedQueueAram = 450;
    private const int SupportedQueueAramMayhem = 2400;
    private const int ExpectedParticipantCount = 10;
    private const int ExpectedTeamSize = 5;
    private const int MaxFrames = 120;
    private const int MaxEvents = 500;
    private const int MaxParticipantId = 10;
    private const int MinTeamId = 1;
    private const int MaxTeamId = 999;
    private const int MaxKdaValue = 200;
    private const double MaxDamageValue = 1_000_000;
    private const double MaxCcValue = 100_000;
    private const double MaxGoldValue = 500_000;
    private const long MaxTimelineTimestamp = 21_600_000;
    private const int MaxTimelineLabelLength = 64;
    private const int MaxFinalItems = 7;
    private const int MaxAugments = 6;
    private const int MaxConfigurationId = 9_999_999;
    private const int MaxChampionId = 9_999_999;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public CompanionAnalysisPayloadV2 Normalize(
        LcuCurrentSummoner currentSummoner,
        LcuRecentMatchSummary selectedMatch,
        LcuMatchDetailDto matchDetail,
        LcuTimelineResult timelineResult)
    {
        ValidateSelectedMatch(selectedMatch, matchDetail);

        if (matchDetail.Participants.Count != ExpectedParticipantCount)
        {
            throw new CompanionAnalysisException("participant_count_invalid", "Companion analysis requires exactly 10 participants.");
        }

        var participants = matchDetail.Participants
            .Select((participant, index) => NormalizeParticipant(participant, index))
            .ToArray();

        var requestedParticipant = participants.SingleOrDefault((participant) =>
            string.Equals(participant.Puuid, currentSummoner.Puuid, StringComparison.Ordinal));

        if (requestedParticipant is null)
        {
            throw new CompanionAnalysisException("requested_participant_missing", "Companion analysis could not locate the requested participant.");
        }

        var requestedTeamCount = participants.Count((participant) => participant.TeamId == requestedParticipant.TeamId);
        if (requestedTeamCount != ExpectedTeamSize)
        {
            throw new CompanionAnalysisException("team_shape_invalid", "Companion analysis requires exactly five teammates for the requested participant.");
        }

        CompanionAnalysisTimelineV2? timeline = null;
        string? timelineUnavailableReason = null;
        if (timelineResult.IsAvailable)
        {
            if (timelineResult.Timeline is null)
            {
                throw new CompanionAnalysisException("timeline_invalid", "Timeline availability state is inconsistent.");
            }

            timeline = NormalizeTimeline(timelineResult.Timeline);
        }
        else
        {
            timelineUnavailableReason = string.IsNullOrWhiteSpace(timelineResult.UnavailableReason)
                ? "timeline_unavailable"
                : timelineResult.UnavailableReason;
        }

        if (timeline is not null && timelineUnavailableReason is not null)
        {
            throw new CompanionAnalysisException("timeline_conflict", "Timeline payload and unavailable reason cannot both be present.");
        }

        return new CompanionAnalysisPayloadV2(
            requestedParticipant.Puuid,
            participants,
            new CompanionAnalysisMatchV2(
                matchDetail.GameId.ToString(CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(matchDetail.GameDataVersion) ? "unknown" : matchDetail.GameDataVersion),
            timeline,
            timelineUnavailableReason
        );
    }

    public byte[] SerializeRequest(CompanionAnalysisSubmitRequest request)
    {
        var requestJsonOptions = new JsonSerializerOptions(JsonOptions)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, requestJsonOptions);
        if (bytes.Length > CompanionAnalysisContract.MaxRequestBytes)
        {
            throw new CompanionAnalysisException("payload_too_large", "Companion analysis payload exceeds the upload limit.");
        }

        return bytes;
    }

    private static void ValidateSelectedMatch(LcuRecentMatchSummary selectedMatch, LcuMatchDetailDto matchDetail)
    {
        if (!selectedMatch.IsSupported || selectedMatch.QueueId is not SupportedQueueAram and not SupportedQueueAramMayhem)
        {
            throw new CompanionAnalysisException("unsupported_queue", "Only ARAM and ARAM Mayhem matches can be analyzed.");
        }

        if (selectedMatch.GameId != matchDetail.GameId || selectedMatch.QueueId != matchDetail.QueueId)
        {
            throw new CompanionAnalysisException("match_mismatch", "Selected match details do not match the requested analysis target.");
        }
    }

    private static CompanionAnalysisParticipantV2 NormalizeParticipant(LcuMatchParticipantDto participant, int index)
    {
        ValidateRequiredString(participant.Puuid, $"participants[{index}].puuid");
        ValidateRequiredString(participant.RiotIdGameName, $"participants[{index}].riotIdGameName");
        ValidateRequiredString(participant.RiotIdTagline, $"participants[{index}].riotIdTagline");
        ValidateRequiredString(participant.ChampionName, $"participants[{index}].championName");
        ValidateBoundedInt(participant.ParticipantId, 1, MaxParticipantId, $"participants[{index}].participantId");
        ValidateBoundedInt(participant.TeamId, MinTeamId, MaxTeamId, $"participants[{index}].teamId");
        ValidateBoundedInt(participant.ChampionId, 1, MaxChampionId, $"participants[{index}].championId");
        ValidateBoundedInt(participant.Kills, 0, MaxKdaValue, $"participants[{index}].kills");
        ValidateBoundedInt(participant.Deaths, 0, MaxKdaValue, $"participants[{index}].deaths");
        ValidateBoundedInt(participant.Assists, 0, MaxKdaValue, $"participants[{index}].assists");

        return new CompanionAnalysisParticipantV2(
            participant.Puuid,
            participant.RiotIdGameName!,
            participant.RiotIdTagline!,
            participant.ParticipantId,
            participant.TeamId,
            participant.Win,
            participant.ChampionId,
            participant.ChampionName!,
            participant.Kills,
            participant.Deaths,
            participant.Assists,
            NormalizeMetric(participant.TotalDamageDealtToChampions, MaxDamageValue, $"participants[{index}].totalDamageDealtToChampions"),
            NormalizeMetric(participant.TotalDamageTaken, MaxDamageValue, $"participants[{index}].totalDamageTaken"),
            NormalizeMetric(participant.TimeCCingOthers, MaxCcValue, $"participants[{index}].timeCCingOthers"),
            NormalizeMetric(participant.TotalHealsOnTeammates, MaxDamageValue, $"participants[{index}].totalHealsOnTeammates"),
            NormalizeMetric(participant.TotalDamageShieldedOnTeammates, MaxDamageValue, $"participants[{index}].totalDamageShieldedOnTeammates"),
            NormalizeConfigurationIds(participant.Items, MaxFinalItems, $"participants[{index}].items"),
            NormalizeConfigurationIds(participant.Augments, MaxAugments, $"participants[{index}].augments"),
            NormalizeMetric(participant.TotalHeal, MaxDamageValue, $"participants[{index}].totalHeal"),
            NormalizeMetric(participant.DamageSelfMitigated, MaxDamageValue, $"participants[{index}].damageSelfMitigated"),
            NormalizeMetric(participant.DamageDealtToTurrets, MaxDamageValue, $"participants[{index}].damageDealtToTurrets"),
            NormalizeMetric(participant.DamageDealtToObjectives, MaxDamageValue, $"participants[{index}].damageDealtToObjectives"),
            NormalizeMetric(participant.TotalTimeCrowdControlDealt, MaxCcValue, $"participants[{index}].totalTimeCrowdControlDealt")
        );
    }

    private static IReadOnlyList<int> NormalizeConfigurationIds(IReadOnlyList<int>? values, int maximumCount, string label)
    {
        if (values is null)
        {
            return Array.Empty<int>();
        }
        if (values.Count > maximumCount)
        {
            throw new CompanionAnalysisException("payload_invalid", $"{label} is missing or exceeds the supported limit.");
        }
        foreach (var value in values) ValidateBoundedInt(value, 0, MaxConfigurationId, label);
        return values.ToArray();
    }

    private static CompanionAnalysisTimelineV2 NormalizeTimeline(LcuTimelineDto timeline)
    {
        if (timeline.Frames.Count > MaxFrames)
        {
            throw new CompanionAnalysisException("timeline_frames_invalid", "Timeline frame count exceeds the v2 limit.");
        }

        if (timeline.Events.Count > MaxEvents)
        {
            throw new CompanionAnalysisException("timeline_events_invalid", "Timeline event count exceeds the v2 limit.");
        }

        var frames = timeline.Frames.Select((frame, index) =>
        {
            ValidateBoundedLong(frame.Timestamp, 0, MaxTimelineTimestamp, $"timeline.frames[{index}].timestamp");
            var participantFrames = new Dictionary<string, CompanionAnalysisParticipantFrameV2>(StringComparer.Ordinal);
            foreach (var entry in frame.ParticipantGoldById.OrderBy((item) => item.Key))
            {
                ValidateBoundedInt(entry.Key, 1, MaxParticipantId, $"timeline.frames[{index}].participantFrames key");
                participantFrames[entry.Key.ToString(CultureInfo.InvariantCulture)] =
                    new CompanionAnalysisParticipantFrameV2(NormalizeMetric(entry.Value, MaxGoldValue, $"timeline.frames[{index}].participantFrames[{entry.Key}].totalGold")!.Value);
            }

            return new CompanionAnalysisTimelineFrameV2(frame.Timestamp, participantFrames);
        }).ToArray();

        var events = timeline.Events.Select((eventDto, index) =>
        {
            ValidateRequiredString(eventDto.Type, $"timeline.events[{index}].type");
            ValidateBoundedLong(eventDto.Timestamp, 0, MaxTimelineTimestamp, $"timeline.events[{index}].timestamp");
            var killerId = NormalizeOptionalParticipantId(eventDto.KillerId);
            var victimId = NormalizeOptionalParticipantId(eventDto.VictimId);
            var participantId = NormalizeOptionalParticipantId(eventDto.ParticipantId);
            var assistingParticipantIds = eventDto.AssistingParticipantIds
                .Select(value => NormalizeOptionalParticipantId(value))
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .Distinct()
                .ToArray();

            foreach (var assist in assistingParticipantIds)
            {
                ValidateBoundedInt(assist, 1, MaxParticipantId, $"timeline.events[{index}].assistingParticipantIds");
            }

            return new CompanionAnalysisTimelineEventV2(
                eventDto.Type,
                eventDto.Timestamp,
                killerId,
                victimId,
                participantId,
                assistingParticipantIds,
                NormalizeOptionalTeamId(eventDto.TeamId),
                NormalizeOptionalLabel(eventDto.BuildingType),
                NormalizeOptionalLabel(eventDto.TowerType),
                NormalizeOptionalLabel(eventDto.LaneType)
            );
        }).ToArray();

        return new CompanionAnalysisTimelineV2(frames, events);
    }

    private static void ValidateRequiredString(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CompanionAnalysisException("payload_invalid", $"{label} is required.");
        }
    }

    private static void ValidateBoundedInt(int value, int minimum, int maximum, string label)
    {
        if (value < minimum || value > maximum)
        {
            throw new CompanionAnalysisException("payload_invalid", $"{label} is outside the supported range.");
        }
    }

    private static int? NormalizeOptionalParticipantId(int? value) =>
        value.HasValue && value.Value is >= 1 and <= 10 ? value : null;

    private static int? NormalizeOptionalTeamId(int? value) =>
        value.HasValue && value.Value is >= MinTeamId and <= MaxTeamId ? value : null;

    private static string? NormalizeOptionalLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Length > MaxTimelineLabelLength)
        {
            throw new CompanionAnalysisException("payload_invalid", "Timeline event label exceeds the supported length.");
        }

        return value;
    }

    private static void ValidateOptionalBoundedInt(int? value, int minimum, int maximum, string label)
    {
        if (!value.HasValue)
        {
            return;
        }

        ValidateBoundedInt(value.Value, minimum, maximum, label);
    }

    private static void ValidateBoundedLong(long value, long minimum, long maximum, string label)
    {
        if (value < minimum || value > maximum)
        {
            throw new CompanionAnalysisException("payload_invalid", $"{label} is outside the supported range.");
        }
    }

    private static double? NormalizeMetric(double? value, double maximum, string label)
    {
        if (!value.HasValue)
        {
            return null;
        }

        if (!double.IsFinite(value.Value) || value.Value < 0 || value.Value > maximum)
        {
            throw new CompanionAnalysisException("payload_invalid", $"{label} is outside the supported range.");
        }

        return value.Value;
    }
}
