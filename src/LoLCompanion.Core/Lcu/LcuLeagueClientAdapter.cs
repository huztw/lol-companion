using System.Net;
using System.Text.Json;

namespace LoLCompanion.Core.Lcu;

public sealed class LcuLeagueClientAdapter
{
    private readonly ILcuLockfileDiscovery _discovery;
    private readonly LcuHttpClientFactory _clientFactory;
    private readonly object _championNamesGate = new();
    private LcuCredential? _cachedCredential;
    private IReadOnlyDictionary<int, string>? _championNames;
    private Task<IReadOnlyDictionary<int, string>?>? _championNamesLoadTask;

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
            async (client, ct) =>
            {
                var championNames = await TryGetChampionNamesAsync(client, ct);
                return await SendAndParseAsync(
                    client,
                    $"lol-match-history/v1/products/lol/{Uri.EscapeDataString(summoner.Puuid)}/matches?begIndex=0&endIndex=19",
                    json => ParseRecentMatches(json, summoner, championNames),
                    ct);
            },
            cancellationToken);
    }

    public Task<LcuMatchDetailDto> GetMatchDetailAsync(long gameId, CancellationToken cancellationToken = default) =>
        ExecuteWithRefreshAsync(
            async (client, ct) =>
            {
                var championNames = await TryGetChampionNamesAsync(client, ct);
                return await SendAndParseAsync(client, $"lol-match-history/v1/games/{gameId}", json => ParseMatchDetail(json, championNames), ct);
            },
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
        var displayName = GetOptionalNonEmptyString(root, "displayName")
            ?? GetOptionalNonEmptyString(root, "gameName");
        if (displayName is null)
        {
            throw new LcuException("lcu_schema_invalid", "LCU response is missing displayName.", true);
        }

        return new LcuCurrentSummoner(
            GetRequiredSafeInt64(root, "summonerId"),
            TryGetSafeInt64(root, "accountId"),
            displayName,
            GetRequiredString(root, "puuid"));
    }

    private static IReadOnlyList<LcuRecentMatchSummary> ParseRecentMatches(
        JsonElement root,
        LcuCurrentSummoner summoner,
        IReadOnlyDictionary<int, string>? championNames)
    {
        var games = GetGamesArray(root);
        var results = new List<LcuRecentMatchSummary>();

        foreach (var game in games.EnumerateArray().Take(20))
        {
            if (TryParseNestedRecentMatch(game, summoner, championNames, out var nestedSummary))
            {
                results.Add(nestedSummary);
                continue;
            }

            if (TryParseFlatRecentMatch(game, summoner, championNames, out var flatSummary))
            {
                results.Add(flatSummary);
                continue;
            }

            throw new LcuException("lcu_schema_invalid", "Current summoner was not found in recent match participants.", true);
        }

        return results;
    }

    private static LcuMatchDetailDto ParseMatchDetail(JsonElement root, IReadOnlyDictionary<int, string>? championNames)
    {
        var participants = new List<LcuMatchParticipantDto>();
        var identities = GetParticipantIdentities(root);

        if (identities is not null)
        {
            var participantById = GetParticipantsById(root);
            foreach (var identity in identities.Value.EnumerateArray())
            {
                var participantId = GetParticipantId(identity);
                if (!participantId.HasValue || !participantById.TryGetValue(participantId.Value, out var participant))
                {
                    throw new LcuException("lcu_schema_invalid", "LCU response is missing participant mapping.", true);
                }

                var player = GetRequiredProperty(identity, "player", JsonValueKind.Object, "lcu_schema_invalid");
                var stats = GetRequiredProperty(participant, "stats", JsonValueKind.Object, "lcu_schema_invalid");
                var championId = GetRequiredInt32(participant, "championId");
                participants.Add(new LcuMatchParticipantDto(
                    Puuid: GetRequiredString(player, "puuid"),
                    RiotIdGameName: GetOptionalNonEmptyString(player, "gameName") ?? GetOptionalNonEmptyString(player, "riotIdGameName"),
                    RiotIdTagline: GetOptionalNonEmptyString(player, "tagLine") ?? GetOptionalNonEmptyString(player, "riotIdTagline"),
                    ParticipantId: participantId.Value,
                    TeamId: GetRequiredInt32(participant, "teamId"),
                    Win: GetRequiredBool(stats, "win"),
                    ChampionId: championId,
                    ChampionName: ResolveChampionName(participant, championId, championNames, allowFallbackToLabel: true),
                    Kills: GetRequiredInt32(stats, "kills"),
                    Deaths: GetRequiredInt32(stats, "deaths"),
                    Assists: GetRequiredInt32(stats, "assists"),
                    TotalDamageDealtToChampions: TryGetDouble(stats, "totalDamageDealtToChampions"),
                    TotalDamageTaken: TryGetDouble(stats, "totalDamageTaken"),
                    TimeCCingOthers: TryGetDouble(stats, "timeCCingOthers"),
                    TotalHealsOnTeammates: TryGetDouble(stats, "totalHealsOnTeammates"),
                    TotalDamageShieldedOnTeammates: TryGetDouble(stats, "totalDamageShieldedOnTeammates"),
                    Items: ReadConfigurationIds(stats, "item", 7),
                    Augments: ReadConfigurationIds(stats, "playerAugment", 6, 1)));
            }
        }
        else
        {
            var participantElements = root.GetProperty("participants").EnumerateArray().ToArray();
            foreach (var participant in participantElements)
            {
                var championId = participant.GetProperty("championId").GetInt32();
                participants.Add(new LcuMatchParticipantDto(
                    Puuid: GetRequiredString(participant, "puuid"),
                    RiotIdGameName: participant.TryGetProperty("riotIdGameName", out var gameName) ? gameName.GetString() : null,
                    RiotIdTagline: participant.TryGetProperty("riotIdTagline", out var tagLine) ? tagLine.GetString() : null,
                    ParticipantId: participant.GetProperty("participantId").GetInt32(),
                    TeamId: participant.GetProperty("teamId").GetInt32(),
                    Win: participant.GetProperty("win").GetBoolean(),
                    ChampionId: championId,
                    ChampionName: ResolveChampionName(participant, championId, championNames, allowFallbackToLabel: true),
                    Kills: participant.GetProperty("kills").GetInt32(),
                    Deaths: participant.GetProperty("deaths").GetInt32(),
                    Assists: participant.GetProperty("assists").GetInt32(),
                    TotalDamageDealtToChampions: TryGetDouble(participant, "totalDamageDealtToChampions"),
                    TotalDamageTaken: TryGetDouble(participant, "totalDamageTaken"),
                    TimeCCingOthers: TryGetDouble(participant, "timeCCingOthers"),
                    TotalHealsOnTeammates: TryGetDouble(participant, "totalHealsOnTeammates"),
                    TotalDamageShieldedOnTeammates: TryGetDouble(participant, "totalDamageShieldedOnTeammates"),
                    Items: ReadConfigurationIds(participant, "item", 7),
                    Augments: ReadConfigurationIds(participant, "playerAugment", 6, 1)));
            }
        }

        return new LcuMatchDetailDto(
            GameId: root.GetProperty("gameId").GetInt64(),
            QueueId: root.GetProperty("queueId").GetInt32(),
            GameMode: root.GetProperty("gameMode").GetString() ?? "UNKNOWN",
            GameType: root.GetProperty("gameType").GetString() ?? "UNKNOWN",
            GameCreation: DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("gameCreation").GetInt64()),
            GameDuration: TimeSpan.FromSeconds(root.GetProperty("gameDuration").GetInt64()),
            Participants: participants,
            GameDataVersion: GetOptionalNonEmptyString(root, "gameVersion"));
    }

    private static JsonElement? GetParticipantIdentities(JsonElement root) =>
        root.TryGetProperty("participantIdentities", out var identities) && identities.ValueKind == JsonValueKind.Array
            ? identities
            : null;

    private static Dictionary<int, JsonElement> GetParticipantsById(JsonElement root)
    {
        if (!root.TryGetProperty("participants", out var participants) || participants.ValueKind != JsonValueKind.Array)
        {
            throw new LcuException("lcu_schema_invalid", "LCU response is missing participants.", true);
        }

        var participantById = new Dictionary<int, JsonElement>();
        foreach (var participant in participants.EnumerateArray())
        {
            var participantId = TryGetSafeInt32(participant, "participantId");
            if (!participantId.HasValue)
            {
                throw new LcuException("lcu_schema_invalid", "LCU response is missing participantId.", true);
            }

            if (!participantById.TryAdd(participantId.Value, participant))
            {
                throw new LcuException("lcu_schema_invalid", "LCU response has duplicate participantId.", true);
            }
        }

        return participantById;
    }

    private static int? GetParticipantId(JsonElement identity)
    {
        var participantId = TryGetSafeInt32(identity, "participantId");
        if (participantId.HasValue)
        {
            return participantId;
        }

        return null;
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
            ? assisting.EnumerateArray()
                .Select(value => TryGetBoundedOptionalParticipantId(value))
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .Distinct()
                .ToArray()
            : [];

        return new LcuTimelineEventDto(
            Type: eventElement.TryGetProperty("type", out var type) ? type.GetString() ?? "UNKNOWN" : "UNKNOWN",
            Timestamp: GetRequiredInt64(eventElement, "timestamp"),
            KillerId: TryGetBoundedOptionalParticipantId(eventElement, "killerId"),
            VictimId: TryGetBoundedOptionalParticipantId(eventElement, "victimId"),
            ParticipantId: TryGetBoundedOptionalParticipantId(eventElement, "participantId"),
            AssistingParticipantIds: assists,
            TeamId: TryGetSafeInt32(eventElement, "teamId"),
            BuildingType: eventElement.TryGetProperty("buildingType", out var buildingType) ? buildingType.GetString() : null,
            TowerType: eventElement.TryGetProperty("towerType", out var towerType) ? towerType.GetString() : null,
            LaneType: eventElement.TryGetProperty("laneType", out var laneType) ? laneType.GetString() : null);
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

    private Task<IReadOnlyDictionary<int, string>?> TryGetChampionNamesAsync(HttpClient client, CancellationToken cancellationToken)
    {
        lock (_championNamesGate)
        {
            if (_championNames is not null)
            {
                return Task.FromResult<IReadOnlyDictionary<int, string>?>(_championNames);
            }

            if (_championNamesLoadTask is not null)
            {
                return _championNamesLoadTask;
            }

            _championNamesLoadTask = LoadChampionNamesAsync(client, cancellationToken);
            return _championNamesLoadTask;
        }
    }

    private async Task<IReadOnlyDictionary<int, string>?> LoadChampionNamesAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync("lol-game-data/assets/v1/champion-summary.json", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CacheChampionNames(Array.Empty<KeyValuePair<int, string>>());
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return CacheChampionNames(Array.Empty<KeyValuePair<int, string>>());
            }

            var names = new Dictionary<int, string>();
            foreach (var champion in document.RootElement.EnumerateArray())
            {
                var championId = TryGetSafeInt32(champion, "id");
                var championName = GetOptionalNonEmptyString(champion, "name");
                if (championId.HasValue && championName is not null)
                {
                    names[championId.Value] = championName;
                }
            }

            return CacheChampionNames(names);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return CacheChampionNames(Array.Empty<KeyValuePair<int, string>>());
        }
    }

    private IReadOnlyDictionary<int, string>? CacheChampionNames(IEnumerable<KeyValuePair<int, string>> championNames)
    {
        lock (_championNamesGate)
        {
            _championNames = championNames.ToDictionary(pair => pair.Key, pair => pair.Value);
            _championNamesLoadTask = null;
            return _championNames;
        }
    }

    private static bool TryParseNestedRecentMatch(JsonElement game, LcuCurrentSummoner summoner, IReadOnlyDictionary<int, string>? championNames, out LcuRecentMatchSummary summary)
    {
        summary = default!;

        if (!game.TryGetProperty("participantIdentities", out var identities) || identities.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var participantId = FindCurrentParticipantId(identities, summoner);
        if (!participantId.HasValue)
        {
            return false;
        }

        if (!game.TryGetProperty("participants", out var participants) || participants.ValueKind != JsonValueKind.Array)
        {
            throw new LcuException("lcu_schema_invalid", "LCU recent match participants are missing.", true);
        }

        foreach (var participant in participants.EnumerateArray())
        {
            if (TryGetSafeInt32(participant, "participantId") != participantId.Value)
            {
                continue;
            }

            var stats = GetRequiredProperty(participant, "stats", JsonValueKind.Object, "lcu_schema_invalid");
            summary = CreateRecentMatchSummary(game, participant, stats, championNames, allowFallbackToLabel: false);
            return true;
        }

        throw new LcuException("lcu_schema_invalid", "Current summoner was not found in recent match participants.", true);
    }

    private static bool TryParseFlatRecentMatch(JsonElement game, LcuCurrentSummoner summoner, IReadOnlyDictionary<int, string>? championNames, out LcuRecentMatchSummary summary)
    {
        summary = default!;

        if (!game.TryGetProperty("participants", out var participants) || participants.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var participant in participants.EnumerateArray())
        {
            if (!MatchesCurrentSummoner(participant, summoner))
            {
                continue;
            }

            summary = CreateRecentMatchSummary(game, participant, participant, championNames, allowFallbackToLabel: false);
            return true;
        }

        return false;
    }

    private static LcuRecentMatchSummary CreateRecentMatchSummary(
        JsonElement game,
        JsonElement participant,
        JsonElement statsSource,
        IReadOnlyDictionary<int, string>? championNames,
        bool allowFallbackToLabel)
    {
        var queueId = game.GetProperty("queueId").GetInt32();
        var championId = participant.GetProperty("championId").GetInt32();
        return new LcuRecentMatchSummary(
            GameId: game.GetProperty("gameId").GetInt64(),
            QueueId: queueId,
            GameMode: game.GetProperty("gameMode").GetString() ?? "UNKNOWN",
            GameType: game.GetProperty("gameType").GetString() ?? "UNKNOWN",
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(game.GetProperty("gameCreation").GetInt64()),
            Duration: TimeSpan.FromSeconds(game.GetProperty("gameDuration").GetInt64()),
            Win: GetRequiredBool(statsSource, "win"),
            ChampionId: championId,
            ChampionName: ResolveChampionName(participant, championId, championNames, allowFallbackToLabel),
            Kills: GetRequiredInt32(statsSource, "kills"),
            Deaths: GetRequiredInt32(statsSource, "deaths"),
            Assists: GetRequiredInt32(statsSource, "assists"),
            IsSupported: queueId is 450 or 2400,
            UnsupportedReason: queueId is 450 or 2400 ? null : "analysis_not_supported_for_queue");
    }

    private static string? ResolveChampionName(
        JsonElement participant,
        int championId,
        IReadOnlyDictionary<int, string>? championNames,
        bool allowFallbackToLabel)
    {
        var explicitName = GetOptionalNonEmptyString(participant, "championName");
        if (explicitName is not null)
        {
            return explicitName;
        }

        if (championNames is not null && championNames.TryGetValue(championId, out var resolvedName) && !string.IsNullOrWhiteSpace(resolvedName))
        {
            return resolvedName;
        }

        return allowFallbackToLabel ? $"Champion #{championId}" : null;
    }

    private static int? FindCurrentParticipantId(JsonElement identities, LcuCurrentSummoner summoner)
    {
        foreach (var identity in identities.EnumerateArray())
        {
            if (!identity.TryGetProperty("player", out var player) || player.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (MatchesCurrentSummoner(player, summoner))
            {
                return TryGetSafeInt32(identity, "participantId");
            }
        }

        return null;
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

    private static string? GetOptionalNonEmptyString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
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

    private static int? TryGetSafeInt32(JsonElement element, string propertyName)
    {
        var value = TryGetSafeInt64(element, propertyName);
        if (!value.HasValue || value.Value < int.MinValue || value.Value > int.MaxValue)
        {
            return null;
        }

        return (int)value.Value;
    }

    private static int? TryGetBoundedOptionalParticipantId(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return TryGetBoundedOptionalParticipantId(property);
    }

    private static int? TryGetBoundedOptionalParticipantId(JsonElement value)
    {
        if (!value.TryGetInt32(out var participantId))
        {
            return null;
        }

        return participantId is < 1 or > 10 ? null : participantId;
    }

    private static bool GetRequiredBoolFromStats(JsonElement participant)
    {
        var stats = GetRequiredProperty(participant, "stats", JsonValueKind.Object, "lcu_schema_invalid");
        if (!stats.TryGetProperty("win", out var win) || win.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new LcuException("lcu_schema_invalid", "LCU response is missing win.", true);
        }

        return win.GetBoolean();
    }

    private static int GetRequiredInt32FromStats(JsonElement participant, string propertyName)
    {
        var stats = GetRequiredProperty(participant, "stats", JsonValueKind.Object, "lcu_schema_invalid");
        var value = TryGetSafeInt32(stats, propertyName);
        if (!value.HasValue)
        {
            throw new LcuException("lcu_schema_invalid", $"LCU response is missing {propertyName}.", true);
        }

        return value.Value;
    }

    private static double? GetOptionalDoubleFromStats(JsonElement participant, string propertyName)
    {
        if (!participant.TryGetProperty("stats", out var stats) || stats.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetDouble(stats, propertyName);
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

    private static IReadOnlyList<int>? ReadConfigurationIds(JsonElement source, string prefix, int count, int startIndex = 0)
    {
        var values = new List<int>();
        foreach (var index in Enumerable.Range(startIndex, count))
        {
            var value = TryGetSafeInt32(source, $"{prefix}{index}");
            if (!value.HasValue) return null;
            values.Add(value.Value);
        }
        return values;
    }

    private static bool GetRequiredBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new LcuException("lcu_schema_invalid", $"LCU response is missing {propertyName}.", true);
        }

        return property.GetBoolean();
    }

    private static int GetRequiredInt32(JsonElement element, string propertyName)
    {
        var value = TryGetSafeInt32(element, propertyName);
        if (!value.HasValue)
        {
            throw new LcuException("lcu_schema_invalid", $"LCU response is missing {propertyName}.", true);
        }

        return value.Value;
    }

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
