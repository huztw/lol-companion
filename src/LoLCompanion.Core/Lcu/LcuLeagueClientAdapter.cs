using System.Net;
using System.Text.Json;

namespace LoLCompanion.Core.Lcu;

public sealed class LcuLeagueClientAdapter
{
    private readonly ILcuLockfileDiscovery _discovery;
    private readonly LcuHttpClientFactory _clientFactory;
    private LcuCredential? _cachedCredential;

    public LcuLeagueClientAdapter(ILcuLockfileDiscovery discovery, LcuHttpClientFactory clientFactory)
    {
        _discovery = discovery;
        _clientFactory = clientFactory;
    }

    public Task<LcuCurrentSummoner> GetCurrentSummonerAsync(CancellationToken cancellationToken = default) =>
        ExecuteWithRefreshAsync(
            static (client, ct) => SendAndParseAsync(client, "lol-summoner/v1/current-summoner", ParseCurrentSummoner, ct),
            cancellationToken);

    public async Task<IReadOnlyList<LcuRecentMatchSummary>> GetRecentMatchesAsync(CancellationToken cancellationToken = default)
    {
        var summoner = await GetCurrentSummonerAsync(cancellationToken);
        return await ExecuteWithRefreshAsync(
            (client, ct) => SendAndParseAsync(
                client,
                $"lol-match-history/v1/products/lol/{Uri.EscapeDataString(summoner.Puuid)}/matches?begIndex=0&endIndex=19",
                json => ParseRecentMatches(json, summoner),
                ct),
            cancellationToken);
    }

    public Task<LcuMatchDetailDto> GetMatchDetailAsync(long gameId, CancellationToken cancellationToken = default) =>
        ExecuteWithRefreshAsync(
            (client, ct) => SendAndParseAsync(client, $"lol-match-history/v1/games/{gameId}", ParseMatchDetail, ct),
            cancellationToken);

    public Task<LcuTimelineResult> GetTimelineAsync(long gameId, CancellationToken cancellationToken = default) =>
        ExecuteWithRefreshAsync(
            async (client, ct) =>
            {
                using var response = await client.GetAsync($"lol-match-history/v1/game-timelines/{gameId}", ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return LcuTimelineResult.Unavailable("timeline_unavailable");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw await CreateHttpErrorAsync(response, ct);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                try
                {
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    return LcuTimelineResult.Available(ParseTimeline(document.RootElement));
                }
                catch (LcuException exception) when (exception.Category is "timeline_schema_invalid" or "lcu_schema_invalid")
                {
                    return LcuTimelineResult.Unavailable("timeline_schema_invalid");
                }
                catch (JsonException)
                {
                    return LcuTimelineResult.Unavailable("timeline_schema_invalid");
                }
            },
            cancellationToken);

    private async Task<T> ExecuteWithRefreshAsync<T>(Func<HttpClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var refreshAttempted = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var credential = await EnsureCredentialAsync(cancellationToken);
            using var client = _clientFactory.Create(credential);

            try
            {
                return await operation(client, cancellationToken);
            }
            catch (LcuException exception) when (!refreshAttempted && IsRefreshable(exception))
            {
                _cachedCredential = null;
                refreshAttempted = true;
            }
            catch (HttpRequestException) when (!refreshAttempted)
            {
                _cachedCredential = null;
                refreshAttempted = true;
            }
            catch (TaskCanceledException) when (!refreshAttempted && !cancellationToken.IsCancellationRequested)
            {
                _cachedCredential = null;
                refreshAttempted = true;
            }
            catch (HttpRequestException exception)
            {
                throw ClassifyTransportFailure(exception, cancellationToken);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LcuException("lcu_timeout", "LCU request timed out.", true, innerException: exception);
            }
        }
    }

    private async Task<LcuCredential> EnsureCredentialAsync(CancellationToken cancellationToken)
    {
        if (_cachedCredential is not null)
        {
            return _cachedCredential;
        }

        var discovery = await _discovery.DiscoverAsync(cancellationToken);
        if (discovery.Status != LcuDiscoveryStatus.Found || discovery.Credential is null)
        {
            throw new LcuException("lockfile_unavailable", discovery.Message, isRecoverable: true);
        }

        _cachedCredential = discovery.Credential;
        return _cachedCredential;
    }

    private static bool IsRefreshable(LcuException exception) =>
        exception.Category is "lcu_auth_failed" or "lcu_connection_failed";

    private static async Task<T> SendAndParseAsync<T>(
        HttpClient client,
        string relativePath,
        Func<JsonElement, T> parser,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(relativePath, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateHttpErrorAsync(response, cancellationToken);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return parser(document.RootElement);
        }
        catch (LcuException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new LcuException("lcu_schema_invalid", "LCU response schema is invalid.", true, innerException: exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new LcuException("lcu_schema_invalid", "LCU response schema is invalid.", true, innerException: exception);
        }
    }

    private static Task<LcuException> CreateHttpErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        return Task.FromResult(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => new LcuException("lcu_auth_failed", "LCU authorization failed.", true, (int)response.StatusCode),
            _ => new LcuException("lcu_connection_failed", "LCU request failed.", true, (int)response.StatusCode)
        });
    }

    private static LcuCurrentSummoner ParseCurrentSummoner(JsonElement root)
    {
        return new LcuCurrentSummoner(
            GetRequiredSafeInt64(root, "summonerId"),
            TryGetSafeInt64(root, "accountId"),
            GetRequiredString(root, "displayName"),
            GetRequiredString(root, "puuid"));
    }

    private static IReadOnlyList<LcuRecentMatchSummary> ParseRecentMatches(JsonElement root, LcuCurrentSummoner summoner)
    {
        var games = GetGamesArray(root);
        var results = new List<LcuRecentMatchSummary>();

        foreach (var game in games.EnumerateArray().Take(20))
        {
            var participants = game.GetProperty("participants");
            JsonElement player = default;
            var found = false;

            foreach (var participant in participants.EnumerateArray())
            {
                if (MatchesCurrentSummoner(participant, summoner))
                {
                    player = participant;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                throw new LcuException("lcu_schema_invalid", "Current summoner was not found in recent match participants.", true);
            }

            var queueId = game.GetProperty("queueId").GetInt32();
            results.Add(new LcuRecentMatchSummary(
                GameId: game.GetProperty("gameId").GetInt64(),
                QueueId: queueId,
                GameMode: game.GetProperty("gameMode").GetString() ?? "UNKNOWN",
                GameType: game.GetProperty("gameType").GetString() ?? "UNKNOWN",
                CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(game.GetProperty("gameCreation").GetInt64()),
                Duration: TimeSpan.FromSeconds(game.GetProperty("gameDuration").GetInt64()),
                Win: player.GetProperty("win").GetBoolean(),
                ChampionId: player.GetProperty("championId").GetInt32(),
                ChampionName: player.TryGetProperty("championName", out var championName) ? championName.GetString() : null,
                Kills: player.GetProperty("kills").GetInt32(),
                Deaths: player.GetProperty("deaths").GetInt32(),
                Assists: player.GetProperty("assists").GetInt32(),
                IsSupported: queueId is 450 or 2400,
                UnsupportedReason: queueId is 450 or 2400 ? null : "analysis_not_supported_for_queue"));
        }

        return results;
    }

    private static LcuMatchDetailDto ParseMatchDetail(JsonElement root)
    {
        var participants = new List<LcuMatchParticipantDto>();
        foreach (var participant in root.GetProperty("participants").EnumerateArray())
        {
            participants.Add(new LcuMatchParticipantDto(
                Puuid: GetRequiredString(participant, "puuid"),
                RiotIdGameName: participant.TryGetProperty("riotIdGameName", out var gameName) ? gameName.GetString() : null,
                RiotIdTagline: participant.TryGetProperty("riotIdTagline", out var tagLine) ? tagLine.GetString() : null,
                ParticipantId: participant.GetProperty("participantId").GetInt32(),
                TeamId: participant.GetProperty("teamId").GetInt32(),
                Win: participant.GetProperty("win").GetBoolean(),
                ChampionId: participant.GetProperty("championId").GetInt32(),
                ChampionName: participant.TryGetProperty("championName", out var championName) ? championName.GetString() : null,
                Kills: participant.GetProperty("kills").GetInt32(),
                Deaths: participant.GetProperty("deaths").GetInt32(),
                Assists: participant.GetProperty("assists").GetInt32(),
                TotalDamageDealtToChampions: TryGetDouble(participant, "totalDamageDealtToChampions"),
                TotalDamageTaken: TryGetDouble(participant, "totalDamageTaken"),
                TimeCCingOthers: TryGetDouble(participant, "timeCCingOthers"),
                TotalHealsOnTeammates: TryGetDouble(participant, "totalHealsOnTeammates"),
                TotalDamageShieldedOnTeammates: TryGetDouble(participant, "totalDamageShieldedOnTeammates")));
        }

        return new LcuMatchDetailDto(
            GameId: root.GetProperty("gameId").GetInt64(),
            QueueId: root.GetProperty("queueId").GetInt32(),
            GameMode: root.GetProperty("gameMode").GetString() ?? "UNKNOWN",
            GameType: root.GetProperty("gameType").GetString() ?? "UNKNOWN",
            GameCreation: DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("gameCreation").GetInt64()),
            GameDuration: TimeSpan.FromSeconds(root.GetProperty("gameDuration").GetInt64()),
            Participants: participants);
    }

    private static LcuTimelineDto ParseTimeline(JsonElement root)
    {
        if (!root.TryGetProperty("frames", out var framesElement) || framesElement.ValueKind != JsonValueKind.Array)
        {
            throw new LcuException("timeline_schema_invalid", "LCU timeline frames are missing.", true);
        }

        var frames = new List<LcuTimelineFrameDto>();
        var events = new List<LcuTimelineEventDto>();
        foreach (var frame in framesElement.EnumerateArray())
        {
            var participantFrames = GetRequiredProperty(frame, "participantFrames", JsonValueKind.Object, "timeline_schema_invalid");
            var participantGold = new Dictionary<int, double>();
            foreach (var property in participantFrames.EnumerateObject())
            {
                if (int.TryParse(property.Name, out var participantId) &&
                    property.Value.TryGetProperty("totalGold", out var totalGold) &&
                    totalGold.TryGetDouble(out var parsedGold))
                {
                    participantGold[participantId] = parsedGold;
                }
            }

            frames.Add(new LcuTimelineFrameDto(GetRequiredInt64(frame, "timestamp"), participantGold));

            if (frame.TryGetProperty("events", out var frameEvents) && frameEvents.ValueKind == JsonValueKind.Array)
            {
                foreach (var eventElement in frameEvents.EnumerateArray())
                {
                    events.Add(ParseTimelineEvent(eventElement));
                }
            }
        }

        return new LcuTimelineDto(frames, events);
    }

    private static LcuTimelineEventDto ParseTimelineEvent(JsonElement eventElement)
    {
        var assists = eventElement.TryGetProperty("assistingParticipantIds", out var assisting) && assisting.ValueKind == JsonValueKind.Array
            ? assisting.EnumerateArray().Where(value => value.TryGetInt32(out _)).Select(value => value.GetInt32()).ToArray()
            : [];

        return new LcuTimelineEventDto(
            Type: eventElement.TryGetProperty("type", out var type) ? type.GetString() ?? "UNKNOWN" : "UNKNOWN",
            Timestamp: GetRequiredInt64(eventElement, "timestamp"),
            KillerId: TryGetInt32(eventElement, "killerId"),
            VictimId: TryGetInt32(eventElement, "victimId"),
            ParticipantId: TryGetInt32(eventElement, "participantId"),
            AssistingParticipantIds: assists,
            BuildingType: eventElement.TryGetProperty("buildingType", out var buildingType) ? buildingType.GetString() : null);
    }

    private static JsonElement GetGamesArray(JsonElement root)
    {
        var games = GetRequiredProperty(root, "games", null, "lcu_schema_invalid");
        if (games.ValueKind == JsonValueKind.Array)
        {
            return games;
        }

        if (games.ValueKind == JsonValueKind.Object &&
            games.TryGetProperty("games", out var nestedGames) &&
            nestedGames.ValueKind == JsonValueKind.Array)
        {
            return nestedGames;
        }

        throw new LcuException("lcu_schema_invalid", "LCU recent matches response is invalid.", true);
    }

    private static bool MatchesCurrentSummoner(JsonElement participant, LcuCurrentSummoner summoner)
    {
        if (participant.TryGetProperty("puuid", out var puuidElement))
        {
            var puuid = puuidElement.GetString();
            if (!string.IsNullOrWhiteSpace(puuid))
            {
                return string.Equals(puuid, summoner.Puuid, StringComparison.Ordinal);
            }
        }

        var participantSummonerId = TryGetSafeInt64(participant, "summonerId");
        if (participantSummonerId.HasValue && participantSummonerId.Value == summoner.SummonerId)
        {
            return true;
        }

        var participantAccountId = TryGetSafeInt64(participant, "accountId");
        return participantAccountId.HasValue &&
               summoner.AccountId.HasValue &&
               participantAccountId.Value == summoner.AccountId.Value;
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string propertyName, JsonValueKind? expectedKind, string category)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new LcuException(category, $"LCU response is missing {propertyName}.", true);
        }

        if (expectedKind.HasValue && property.ValueKind != expectedKind.Value)
        {
            throw new LcuException(category, $"LCU response has invalid {propertyName}.", true);
        }

        return property;
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new LcuException("lcu_schema_invalid", $"LCU response is missing {propertyName}.", true);
        }

        return property.GetString()!;
    }

    private static long GetRequiredSafeInt64(JsonElement root, string propertyName)
    {
        var value = TryGetSafeInt64(root, propertyName);
        if (!value.HasValue)
        {
            throw new LcuException("lcu_schema_invalid", $"LCU response is missing {propertyName}.", true);
        }

        return value.Value;
    }

    private static long? TryGetSafeInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var numeric) && IsSafeInteger(numeric) => numeric,
            JsonValueKind.String when long.TryParse(property.GetString(), out var parsed) && IsSafeInteger(parsed) => parsed,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => throw new LcuException("lcu_schema_invalid", $"LCU response has invalid {propertyName}.", true)
        };
    }

    private static long GetRequiredInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || !property.TryGetInt64(out var value))
        {
            throw new LcuException("timeline_schema_invalid", $"LCU response is missing {propertyName}.", true);
        }

        return value;
    }

    private static double? TryGetDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value) ? value : null;

    private static int? TryGetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value) ? value : null;

    private static LcuException ClassifyTransportFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw exception;
        }

        return exception switch
        {
            TaskCanceledException timeout => new LcuException("lcu_timeout", "LCU request timed out.", true, innerException: timeout),
            HttpRequestException requestFailure => new LcuException("lcu_connection_failed", "LCU request failed.", true, innerException: requestFailure),
            _ => new LcuException("lcu_connection_failed", "LCU request failed.", true, innerException: exception)
        };
    }

    private static bool IsSafeInteger(long value) => Math.Abs(value) <= 9_007_199_254_740_991L;
}
