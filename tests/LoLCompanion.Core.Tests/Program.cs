using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LoLCompanion.Core.Analysis;
using LoLCompanion.Core.Api;
using LoLCompanion.Core.Contracts;
using LoLCompanion.Core.Lcu;

await TestLockfileParserAsync();
await TestDiscoveryAsync();
await TestLoopbackAndAuthAsync();
await TestRecentMatchMappingAsync();
await TestDetailAndTimelineAsync();
await TestStaleRefreshAsync();
await TestCancellationAsync();
await TestTransportFailureClassificationAsync();
await TestCompanionAnalysisNormalizerAsync();
await TestCompanionSessionManagerAsync();
await TestCompanionApiClientAsync();
await TestCompanionPairingControllerAsync();
await TestCompanionAnalysisWorkflowAsync();

Console.WriteLine("LoL Companion core adapter tests passed.");

static Task TestLockfileParserAsync()
{
    var credential = LcuLockfileParser.Parse("LeagueClientUx:1234:2999:super-secret:https");
    Assert(credential.ProcessId == 1234, "Expected parsed process id.");
    Assert(credential.Port == 2999, "Expected parsed port.");
    Assert(credential.Protocol == "https", "Expected https protocol.");
    Assert(!credential.ToString().Contains("super-secret", StringComparison.Ordinal), "Credential ToString must redact password.");

    foreach (var invalid in new[]
             {
                 "",
                 "LeagueClientUx:0:2999:secret:https",
                 "LeagueClientUx:1234:70000:secret:https",
                 "LeagueClientUx:1234:2999::https",
                 "LeagueClientUx:1234:2999:secret:http"
             })
    {
        try
        {
            _ = LcuLockfileParser.Parse(invalid);
            throw new InvalidOperationException("Expected invalid lockfile to fail.");
        }
        catch (LcuException exception)
        {
            Assert(exception.Category == "lockfile_invalid", "Expected redacted lockfile error category.");
            Assert(!exception.ToString().Contains("secret", StringComparison.Ordinal), "Lockfile exception must not leak password.");
        }
    }

    return Task.CompletedTask;
}

static async Task TestDiscoveryAsync()
{
    var fileSystem = new FakeFileSystem(new Dictionary<string, string>
    {
        [@"C:\Portable\League\lockfile"] = "LeagueClientUx:2222:3001:secret-a:https",
        [@"C:\Games\LeagueClientUx.exe\..\lockfile"] = "ignored"
    });
    var locator = new FakeProcessLocator([@"C:\Games\League\LeagueClientUx.exe"]);
    var discovery = new LcuLockfileDiscovery(fileSystem, locator, new LcuLockfileDiscoveryOptions
    {
        ExplicitCandidates = [@"C:\Portable\League"]
    });

    var found = await discovery.DiscoverAsync();
    Assert(found.Status == LcuDiscoveryStatus.Found, "Expected explicit candidate to be discovered first.");
    Assert(found.Credential?.Port == 3001, "Expected discovered lockfile port.");

    var unreadable = new LcuLockfileDiscovery(
        new ThrowingFileSystem(@"C:\Broken\League\lockfile", new IOException("sharing violation")),
        new FakeProcessLocator([]),
        new LcuLockfileDiscoveryOptions { ExplicitCandidates = [@"C:\Broken\League\lockfile"] });
    var unreadableResult = await unreadable.DiscoverAsync();
    Assert(unreadableResult.Status == LcuDiscoveryStatus.Unreadable, "Expected unreadable lockfile state.");

    var missing = new LcuLockfileDiscovery(new FakeFileSystem(new Dictionary<string, string>()), new FakeProcessLocator([]));
    var missingResult = await missing.DiscoverAsync();
    Assert(missingResult.Status == LcuDiscoveryStatus.NotFound, "Expected not found when no candidates exist.");
}

static Task TestLoopbackAndAuthAsync()
{
    var credential = new LcuCredential(1234, "127.0.0.1", 2999, "https", "loopback-secret");
    var header = credential.CreateAuthorizationHeader();
    Assert(header.Scheme == "Basic", "Expected basic authorization scheme.");
    Assert(header.Parameter == Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:loopback-secret")), "Expected riot basic auth header.");

    var factory = new LcuHttpClientFactory(TimeSpan.FromSeconds(5), _ => new RecordingHandler((_, _) =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"summonerId":1,"accountId":99,"displayName":"Tester","puuid":"puuid-1"}""", Encoding.UTF8, "application/json")
        }));

    using var client = factory.Create(credential);
    Assert(client.BaseAddress?.ToString() == "https://127.0.0.1:2999/", "Expected loopback base address.");
    Assert(client.DefaultRequestHeaders.Authorization?.Scheme == "Basic", "Expected default auth header.");
    Assert(LcuHttpClientFactory.IsTrustedLoopbackUri(new Uri("https://127.0.0.1:2999/test"), credential), "Expected trusted loopback https URI.");
    Assert(!LcuHttpClientFactory.IsTrustedLoopbackUri(new Uri("http://127.0.0.1:2999/test"), credential), "Expected scheme guard.");
    Assert(!LcuHttpClientFactory.IsTrustedLoopbackUri(new Uri("https://example.com:2999/test"), credential), "Expected host guard.");
    Assert(!LcuHttpClientFactory.IsTrustedLoopbackUri(new Uri("https://127.0.0.1:3000/test"), credential), "Expected port guard.");

    try
    {
        _ = new LcuHttpClientFactory(TimeSpan.FromSeconds(5)).Create(new LcuCredential(1234, "10.0.0.5", 2999, "https", "secret"));
        throw new InvalidOperationException("Expected non-loopback host to fail.");
    }
    catch (LcuException exception)
    {
        Assert(exception.Category == "loopback_only", "Expected loopback guard category.");
    }

    return Task.CompletedTask;
}

static async Task TestRecentMatchMappingAsync()
{
    var adapter = CreateAdapter(
        discoveries:
        [
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1234, "127.0.0.1", 2999, "https", "secret"), @"C:\League\lockfile", "found")
        ],
        handlers:
        [
            new RecordingHandler((request, _) =>
            {
                if (request.RequestUri?.AbsolutePath.Contains("current-summoner", StringComparison.Ordinal) == true)
                {
                    return Json("""{"summonerId":1001,"accountId":2002,"displayName":"Tester","puuid":"puuid-1"}""");
                }

                var recent = """
                {
                  "games": {
                    "gameCount": 2,
                    "games": [
                      {
                        "gameId": 11,
                        "queueId": 450,
                        "gameMode": "ARAM",
                        "gameType": "MATCHED",
                        "gameCreation": 1721892000000,
                        "gameDuration": 1200,
                        "participants": [
                          { "puuid": "someone-else", "summonerId": 9999, "accountId": 8888, "win": false, "championId": 99, "championName": "Lux", "kills": 0, "deaths": 10, "assists": 1 },
                          { "summonerId": 1001, "accountId": 2002, "win": true, "championId": 1, "championName": "Annie", "kills": 8, "deaths": 3, "assists": 10 }
                        ]
                      },
                      {
                        "gameId": 12,
                        "queueId": 400,
                        "gameMode": "CLASSIC",
                        "gameType": "MATCHED",
                        "gameCreation": 1721893000000,
                        "gameDuration": 1500,
                        "participants": [
                          { "puuid": "puuid-1", "win": false, "championId": 22, "championName": "Ashe", "kills": 2, "deaths": 6, "assists": 7 }
                        ]
                      }
                    ]
                  }
                }
                """;

                return Json(recent);
            })
        ]);

    var matches = await adapter.GetRecentMatchesAsync();
    Assert(matches.Count == 2, "Expected two mapped recent matches.");
    Assert(matches[0].IsSupported, "Expected queue 450 to be supported.");
    Assert(matches[0].Win, "Expected current summoner identity fallback to select the correct player.");
    Assert(matches[0].Kills == 8, "Expected the selected player to be the current summoner, not another participant.");
    Assert(!matches[1].IsSupported, "Expected queue 400 to be unsupported.");
    Assert(matches[1].UnsupportedReason == "analysis_not_supported_for_queue", "Expected unsupported reason.");
}

static async Task TestDetailAndTimelineAsync()
{
    var requests = new List<Uri?>();
    var adapter = CreateAdapter(
        discoveries:
        [
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1234, "127.0.0.1", 2999, "https", "secret"), @"C:\League\lockfile", "found")
        ],
        handlers:
        [
            new RecordingHandler((request, _) =>
            {
                requests.Add(request.RequestUri);
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Contains("/games/431945471", StringComparison.Ordinal))
                {
                    return Json("""
                    {
                      "gameId": 431945471,
                      "queueId": 2400,
                      "gameMode": "ARAM",
                      "gameType": "MATCHED",
                      "gameCreation": 1721892000000,
                      "gameDuration": 1600,
                      "participants": [
                        {
                          "puuid": "puuid-1",
                          "participantId": 1,
                          "teamId": 100,
                          "win": true,
                          "championId": 1,
                          "championName": "Annie",
                          "kills": 8,
                          "deaths": 3,
                          "assists": 10,
                          "totalDamageDealtToChampions": 25000,
                          "totalDamageTaken": 14000,
                          "timeCCingOthers": 30,
                          "totalHealsOnTeammates": 1000,
                          "totalDamageShieldedOnTeammates": 500
                        }
                      ]
                    }
                    """);
                }

                return Json("""
                {
                  "frames": [
                    {
                      "timestamp": 60000,
                      "participantFrames": {
                        "1": { "totalGold": 2500 }
                      },
                      "events": [
                        {
                          "type": "CHAMPION_KILL",
                          "timestamp": 61000,
                          "killerId": 1,
                          "victimId": 6,
                          "assistingParticipantIds": [2, 3]
                        }
                      ]
                    }
                  ]
                }
                """);
            })
        ]);

    var detail = await adapter.GetMatchDetailAsync(431945471);
    Assert(detail.QueueId == 2400, "Expected detailed queue id.");
    Assert(requests.Count == 1 && requests[0]?.AbsolutePath.EndsWith("/games/431945471", StringComparison.Ordinal) == true, "Detail should be fetched on demand only.");

    var timeline = await adapter.GetTimelineAsync(431945471);
    Assert(timeline.IsAvailable, "Expected frame-based timeline to parse successfully.");
    Assert(timeline.Timeline?.Frames.Count == 1, "Expected timeline frame parsing.");
    Assert(timeline.Timeline?.Events.Count == 1, "Expected timeline events to come from frames[].events.");

    var unavailableAdapter = CreateAdapter(
        discoveries:
        [
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1234, "127.0.0.1", 2999, "https", "secret"), @"C:\League\lockfile", "found")
        ],
        handlers:
        [
            new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound))
        ]);

    var unavailableTimeline = await unavailableAdapter.GetTimelineAsync(431945471);
    Assert(!unavailableTimeline.IsAvailable, "Expected 404 timeline to become unavailable result.");
    Assert(unavailableTimeline.UnavailableReason == "timeline_unavailable", "Expected explicit unavailable reason.");

    var invalidTimelineAdapter = CreateAdapter(
        discoveries:
        [
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1234, "127.0.0.1", 2999, "https", "secret"), @"C:\League\lockfile", "found")
        ],
        handlers:
        [
            new RecordingHandler((_, _) => Json("""{"events": []}"""))
        ]);

    var invalidTimeline = await invalidTimelineAdapter.GetTimelineAsync(431945471);
    Assert(!invalidTimeline.IsAvailable, "Expected invalid timeline schema to become unavailable result.");
    Assert(invalidTimeline.UnavailableReason == "timeline_schema_invalid", "Expected schema unavailable reason.");
}

static async Task TestStaleRefreshAsync()
{
    var factoryCalls = 0;
    var adapter = CreateAdapter(
        discoveries:
        [
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1234, "127.0.0.1", 2999, "https", "secret-a"), @"C:\League\lockfile", "found"),
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1235, "127.0.0.1", 3000, "https", "secret-b"), @"C:\League\lockfile", "refreshed")
        ],
        handlerFactory: credential =>
        {
            factoryCalls++;
            if (credential.Port == 2999)
            {
                return new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return new RecordingHandler((_, _) => Json("""{"summonerId":1,"accountId":99,"displayName":"Tester","puuid":"puuid-1"}"""));
        });

    var summoner = await adapter.GetCurrentSummonerAsync();
    Assert(summoner.Puuid == "puuid-1", "Expected rediscovered credential to succeed.");
    Assert(factoryCalls == 2, "Expected exactly one stale credential refresh.");
}

static async Task TestCancellationAsync()
{
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    var adapter = CreateAdapter(
        discoveries:
        [
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1234, "127.0.0.1", 2999, "https", "secret"), @"C:\League\lockfile", "found")
        ],
        handlers:
        [
            new RecordingHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return Json("""{"summonerId":1,"accountId":99,"displayName":"Tester","puuid":"puuid-1"}""");
            })
        ]);

    await AssertThrowsAsync<OperationCanceledException>(() => adapter.GetCurrentSummonerAsync(cts.Token), "Expected cancellation to flow through.");
}

static async Task TestTransportFailureClassificationAsync()
{
    var attempts = 0;
    var timeoutAdapter = CreateAdapter(
        discoveries:
        [
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1234, "127.0.0.1", 2999, "https", "secret-a"), @"C:\League\lockfile", "found"),
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1235, "127.0.0.1", 3000, "https", "secret-b"), @"C:\League\lockfile", "refreshed")
        ],
        handlerFactory: _ =>
        {
            attempts++;
            return new RecordingHandler((_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout")));
        });

    try
    {
        _ = await timeoutAdapter.GetCurrentSummonerAsync();
        throw new InvalidOperationException("Expected timeout classification.");
    }
    catch (LcuException exception)
    {
        Assert(exception.Category == "lcu_timeout", "Expected timeout category after exactly one refresh.");
        Assert(attempts == 2, "Expected exactly one refresh before timeout classification.");
    }

    attempts = 0;
    var connectionAdapter = CreateAdapter(
        discoveries:
        [
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1234, "127.0.0.1", 2999, "https", "secret-a"), @"C:\League\lockfile", "found"),
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1235, "127.0.0.1", 3000, "https", "secret-b"), @"C:\League\lockfile", "refreshed")
        ],
        handlerFactory: _ =>
        {
            attempts++;
            return new RecordingHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("boom")));
        });

    try
    {
        _ = await connectionAdapter.GetCurrentSummonerAsync();
        throw new InvalidOperationException("Expected connection classification.");
    }
    catch (LcuException exception)
    {
        Assert(exception.Category == "lcu_connection_failed", "Expected connection failure category after exactly one refresh.");
        Assert(attempts == 2, "Expected exactly one refresh before final connection failure.");
    }
}

static async Task TestCompanionAnalysisNormalizerAsync()
{
    var normalizer = new CompanionAnalysisNormalizer();
    var fixture = LoadAnalysisFixture();
    var currentSummoner = new LcuCurrentSummoner(123456789, 987654321, "PlayerA", "player-a");
    var selectedMatch = new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T10:00:00Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null);
    var matchDetail = new LcuMatchDetailDto(
        431945471,
        450,
        "ARAM",
        "MATCHED",
        DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
        TimeSpan.FromMinutes(23),
        fixture.Participants.Select(participant => new LcuMatchParticipantDto(
            participant.Puuid,
            participant.RiotIdGameName,
            participant.RiotIdTagline,
            participant.ParticipantId,
            participant.TeamId,
            participant.Win,
            1,
            participant.ChampionName,
            participant.Kills,
            participant.Deaths,
            participant.Assists,
            participant.TotalDamageDealtToChampions,
            participant.TotalDamageTaken,
            participant.TimeCCingOthers,
            participant.TotalHealsOnTeammates,
            participant.TotalDamageShieldedOnTeammates)).ToArray());
    var timeline = new LcuTimelineResult(
        true,
        new LcuTimelineDto(
            [
                new LcuTimelineFrameDto(60000, new Dictionary<int, double> { [1] = 2500, [2] = 2400 }),
                new LcuTimelineFrameDto(120000, new Dictionary<int, double> { [1] = 4800, [2] = 4550 })
            ],
            [
                new LcuTimelineEventDto("CHAMPION_KILL", 61000, 1, 6, null, [2, 3], null),
                new LcuTimelineEventDto("BUILDING_KILL", 121000, null, null, 4, [], "OUTER_TURRET")
            ]),
        null);

    var normalized = normalizer.Normalize(currentSummoner, selectedMatch, matchDetail, timeline);
    Assert(normalized.RequestedParticipantPuuid == "player-a", "Expected requested participant to be preserved.");
    Assert(normalized.Participants.Count == 10, "Expected exactly ten participants.");
    Assert(normalized.Participants.Count(participant => participant.TeamId == 100) == 5, "Expected exactly five teammates.");
    Assert(normalized.Timeline is not null, "Expected timeline to be present for available timeline.");
    Assert(normalized.Timeline!.Frames.Count == 2, "Expected timeline frame bound to survive.");
    Assert(normalized.Timeline.Events.Count == 2, "Expected timeline event bound to survive.");

    var unavailable = normalizer.Normalize(
        currentSummoner,
        selectedMatch,
        matchDetail,
        new LcuTimelineResult(false, null, "timeline missing"));
    Assert(unavailable.Timeline is null, "Expected unavailable timeline to omit timeline payload.");
    Assert(unavailable.TimelineUnavailableReason == "timeline missing", "Expected unavailable reason to round-trip.");

    await AssertThrowsAsync<CompanionAnalysisException>(() => Task.FromResult(normalizer.Normalize(
        currentSummoner,
        new LcuRecentMatchSummary(431945471, 420, "CLASSIC", "MATCHED", DateTimeOffset.Parse("2026-07-25T10:00:00Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, false, "analysis_not_supported_for_queue"),
        matchDetail,
        timeline)), "Expected unsupported queue to fail.");

    await AssertThrowsAsync<CompanionAnalysisException>(() => Task.FromResult(normalizer.Normalize(
        currentSummoner,
        selectedMatch,
        new LcuMatchDetailDto(matchDetail.GameId, matchDetail.QueueId, matchDetail.GameMode, matchDetail.GameType, matchDetail.GameCreation, matchDetail.GameDuration, matchDetail.Participants.Take(9).ToArray()),
        timeline)), "Expected participant count validation to fail.");

    await AssertThrowsAsync<CompanionAnalysisException>(() => Task.FromResult(normalizer.Normalize(
        currentSummoner,
        selectedMatch,
        new LcuMatchDetailDto(matchDetail.GameId, matchDetail.QueueId, matchDetail.GameMode, matchDetail.GameType, matchDetail.GameCreation, matchDetail.GameDuration,
            matchDetail.Participants.Select((participant, index) => index == 0 ? participant with { TotalDamageDealtToChampions = double.PositiveInfinity } : participant).ToArray()),
        timeline)), "Expected metric bounds validation to fail.");

    await AssertThrowsAsync<CompanionAnalysisException>(() => Task.FromResult(normalizer.Normalize(
        currentSummoner,
        selectedMatch,
        new LcuMatchDetailDto(matchDetail.GameId, matchDetail.QueueId, matchDetail.GameMode, matchDetail.GameType, matchDetail.GameCreation, matchDetail.GameDuration,
            matchDetail.Participants.Select((participant, index) => index == 0 ? participant with { TeamId = 101 } : participant).ToArray()),
        timeline)), "Expected team-shape validation to fail.");
}

static async Task TestCompanionSessionManagerAsync()
{
    var now = DateTimeOffset.Parse("2026-07-25T10:00:00Z");
    var current = now;
    var manager = new InMemoryCompanionSessionManager(() => current);
    var client = new CompanionApiClient(new HttpClient(new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))) { BaseAddress = new Uri("https://companion.local/") });
    var redeem = new PairRedeemRequest("ABC-123", "Lab PC");
    var handler = new RecordingSessionHandler();
    var api = new CompanionApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://companion.local/") });

    await manager.RedeemAsync(api, redeem);
    Assert(manager.GetActiveSession() is not null, "Expected redeemed session to be stored in memory only.");
    Assert(manager.GetRequiredSessionToken() == "session-token-1", "Expected token to stay in memory only.");

    current = current.AddHours(3);
    Assert(manager.GetActiveSession() is null, "Expected expired session to clear automatically.");

    await manager.RedeemAsync(api, redeem);
    manager.Clear();
    Assert(manager.GetActiveSession() is null, "Expected manual clear to remove in-memory session.");

    await manager.RedeemAsync(api, redeem);
    handler.NextDeleteStatus = HttpStatusCode.Unauthorized;
    await manager.RevokeAsync(api);
    Assert(manager.GetActiveSession() is null, "Expected 401 revoke to clear session.");
    Assert(!manager.GetType().GetProperties().Any(property => property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)), "Expected no persistence/token surface on manager.");
}

static async Task TestCompanionApiClientAsync()
{
    var handler = new ApiClientHandler();
    using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://companion.local/") };
    var client = new CompanionApiClient(httpClient);

    var submit = await client.SubmitAnalysisAsync("session-token-1", Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""));
    var status = await client.GetAnalysisStatusAsync("session-token-1", "job-1");
    var version = await client.GetVersionAsync();
    var current = await client.GetCurrentSessionAsync("session-token-1");

    Assert(submit.JobId == "job-1", "Expected submit response to be parsed.");
    Assert(status.UserAction == "poll_status", "Expected status DTO to be parsed.");
    Assert(version.Current.LatestVersion == "1.2.3", "Expected version DTO to be parsed.");
    Assert(current.DeviceName == "Lab PC", "Expected current session DTO to be parsed.");
    Assert(handler.Requests.Count == 4, "Expected all API endpoints to be exercised.");
    Assert(handler.Requests[0].Authorization == "Bearer session-token-1", "Expected bearer auth on submit.");

    var errorHandler = new ApiClientHandler(HttpStatusCode.BadRequest, """{"error":"secret token leaked","details":"keep"}""");
    using var errorClient = new HttpClient(errorHandler) { BaseAddress = new Uri("https://companion.local/") };
    var errorApi = new CompanionApiClient(errorClient);
    try
    {
        await errorApi.SubmitAnalysisAsync("session-token-1", Encoding.UTF8.GetBytes("{}"));
        throw new InvalidOperationException("Expected submit failure.");
    }
    catch (CompanionApiException exception)
    {
        Assert(exception.Message == "secret token leaked", "Expected allowlisted error field only.");
        Assert(!exception.Message.Contains("details", StringComparison.OrdinalIgnoreCase), "Expected non-allowlisted fields to be ignored.");
    }
}

static async Task TestCompanionPairingControllerAsync()
{
    var sessionManager = new InMemoryCompanionSessionManager(
        () => DateTimeOffset.Parse("2026-07-25T10:00:00Z"));
    using var successClient = new HttpClient(new SessionHandler())
    {
        BaseAddress = new Uri("https://companion.local/")
    };
    var controller = new LoLCompanion.Core.Pairing.CompanionPairingController(
        new CompanionApiClient(successClient),
        sessionManager);

    var paired = await controller.PairAsync("  ABC-123  ", "  Lab PC  ");
    Assert(
        paired.State == LoLCompanion.Core.Pairing.CompanionPairingState.Paired,
        "Expected successful pairing state.");
    Assert(paired.Session is not null, "Expected paired session snapshot.");
    Assert(paired.Session!.DeviceName == "Lab PC", "Expected trimmed device name.");
    Assert(paired.Session.DiscordUserId == "discord-user-1", "Expected snapshot Discord user id.");
    Assert(sessionManager.GetActiveSession() is not null, "Expected stored session after pairing.");

    var blankCode = await controller.PairAsync("   ", "Lab PC");
    Assert(
        blankCode.State == LoLCompanion.Core.Pairing.CompanionPairingState.ValidationFailed,
        "Expected blank pair code validation.");

    var blankDevice = await controller.PairAsync("ABC-123", "   ");
    Assert(
        blankDevice.State == LoLCompanion.Core.Pairing.CompanionPairingState.ValidationFailed,
        "Expected blank device name validation.");

    var longDevice = await controller.PairAsync("ABC-123", new string('x', 41));
    Assert(
        longDevice.State == LoLCompanion.Core.Pairing.CompanionPairingState.ValidationFailed,
        "Expected long device validation.");

    foreach (var (status, expectedState) in new[]
             {
                 (HttpStatusCode.BadRequest, LoLCompanion.Core.Pairing.CompanionPairingState.InvalidOrExpiredCode),
                 (HttpStatusCode.Conflict, LoLCompanion.Core.Pairing.CompanionPairingState.CodeAlreadyUsed),
                 (HttpStatusCode.TooManyRequests, LoLCompanion.Core.Pairing.CompanionPairingState.RateLimited),
                 (HttpStatusCode.InternalServerError, LoLCompanion.Core.Pairing.CompanionPairingState.ServiceUnavailable)
             })
    {
        using var client = new HttpClient(new ApiClientHandler(status, """{"error":"backend message"}"""))
        {
            BaseAddress = new Uri("https://companion.local/")
        };
        var result = await new LoLCompanion.Core.Pairing.CompanionPairingController(
                new CompanionApiClient(client),
                new InMemoryCompanionSessionManager())
            .PairAsync("ABC-123", "Lab PC");

        Assert(result.State == expectedState, $"Expected mapped pairing state for {status}.");
        Assert(
            !result.Message.Contains("backend message", StringComparison.Ordinal),
            "Expected pairing result to hide backend error details.");
    }

    using (var client = new HttpClient(new ThrowingHttpMessageHandler(new HttpRequestException("boom")))
           {
               BaseAddress = new Uri("https://companion.local/")
           })
    {
        var result = await new LoLCompanion.Core.Pairing.CompanionPairingController(
                new CompanionApiClient(client),
                new InMemoryCompanionSessionManager())
            .PairAsync("ABC-123", "Lab PC");
        Assert(
            result.State == LoLCompanion.Core.Pairing.CompanionPairingState.NetworkUnavailable,
            "Expected network failure mapping.");
    }

    using (var client = new HttpClient(new ThrowingHttpMessageHandler(new TaskCanceledException("timeout")))
           {
               BaseAddress = new Uri("https://companion.local/")
           })
    {
        var result = await new LoLCompanion.Core.Pairing.CompanionPairingController(
                new CompanionApiClient(client),
                new InMemoryCompanionSessionManager())
            .PairAsync("ABC-123", "Lab PC");
        Assert(
            result.State == LoLCompanion.Core.Pairing.CompanionPairingState.TimedOut,
            "Expected timeout mapping.");
    }

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(
        () => controller.PairAsync("ABC-123", "Lab PC", cancellation.Token),
        "Expected caller cancellation to rethrow.");
}

static async Task TestCompanionAnalysisWorkflowAsync()
{
    var matchDetail = new LcuMatchDetailDto(
            431945471,
            450,
            "ARAM",
            "MATCHED",
            DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
            TimeSpan.FromMinutes(23),
            [
                new LcuMatchParticipantDto("player-a", "PlayerA", "TST1", 1, 100, true, 1, "Annie", 8, 2, 10, 25000, 14000, 30, 1000, 500),
                new LcuMatchParticipantDto("player-b", "PlayerB", "TST1", 2, 100, true, 2, "Lux", 6, 3, 12, 22000, 11000, 18, 1200, 700),
                new LcuMatchParticipantDto("player-c", "PlayerC", "TST1", 3, 100, true, 3, "Leona", 2, 4, 14, 9000, 21000, 40, 0, 200),
                new LcuMatchParticipantDto("player-d", "PlayerD", "TST1", 4, 100, true, 4, "Braum", 1, 5, 9, 7000, 18000, 28, 0, 500),
                new LcuMatchParticipantDto("player-e", "PlayerE", "TST1", 5, 100, true, 5, "Seraphine", 1, 3, 11, 11000, 9000, 12, 2400, 1000),
                new LcuMatchParticipantDto("player-x1", "PlayerX1", "TST1", 6, 200, false, 6, "Ahri", 4, 5, 3, 18000, 9000, 8, 0, 0),
                new LcuMatchParticipantDto("player-x2", "PlayerX2", "TST1", 7, 200, false, 7, "Ezreal", 5, 5, 2, 17000, 8500, 4, 0, 0),
                new LcuMatchParticipantDto("player-x3", "PlayerX3", "TST1", 8, 200, false, 8, "Nami", 2, 6, 5, 9000, 7000, 12, 1100, 400),
                new LcuMatchParticipantDto("player-x4", "PlayerX4", "TST1", 9, 200, false, 9, "Sett", 3, 4, 4, 13000, 16000, 16, 0, 0),
                new LcuMatchParticipantDto("player-x5", "PlayerX5", "TST1", 10, 200, false, 10, "Sona", 1, 5, 6, 8000, 6000, 6, 1800, 900)
            ]);
    var timeline = new LcuTimelineResult(false, null, "timeline missing");
    var source = new FakeAnalysisSource(
        new LcuCurrentSummoner(123456789, 987654321, "PlayerA", "player-a"),
        matchDetail,
        timeline);
    var attempts = 0;
    var requestIds = new List<string>();
    var bodies = new List<string>();
    var handler = new WorkflowHandler(() =>
    {
        attempts++;
        if (attempts == 1)
        {
            return new HttpResponseMessage(HttpStatusCode.RequestTimeout);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"jobId":"job-1","duplicate":false}""", Encoding.UTF8, "application/json")
        };
    },
    submitBody =>
    {
        requestIds.Add(submitBody.requestId);
        bodies.Add(submitBody.rawBody);
        attempts++;
        if (attempts == 1)
        {
            return new HttpResponseMessage(HttpStatusCode.RequestTimeout);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"jobId":"job-1","duplicate":false}""", Encoding.UTF8, "application/json")
        };
    },
    statusFactory: () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"schemaVersion":1,"jobId":"job-1","state":"processing","createdAt":"2026-07-25T10:00:00Z","completedAt":null,"reportAvailable":true,"deliveryState":"sending","userAction":"poll_status"}""", Encoding.UTF8, "application/json")
    },
    terminalFactory: () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"schemaVersion":1,"jobId":"job-1","state":"completed","createdAt":"2026-07-25T10:00:00Z","completedAt":"2026-07-25T10:01:00Z","reportAvailable":true,"deliveryState":"sent","userAction":"none"}""", Encoding.UTF8, "application/json")
    });

    var sessionManager = new InMemoryCompanionSessionManager(() => DateTimeOffset.Parse("2026-07-25T10:00:00Z"));
    var workflow = new CompanionAnalysisWorkflow(
        source,
        new CompanionApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://companion.local/") }),
        sessionManager,
        new CompanionAnalysisNormalizer(),
        new CompanionAnalysisWorkflowOptions(2, 2, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)),
        () => Guid.Parse("11111111-1111-4111-8111-111111111111"),
        () => DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
        (_, _) => Task.CompletedTask);

    await sessionManager.RedeemAsync(new CompanionApiClient(new HttpClient(new SessionHandler()) { BaseAddress = new Uri("https://companion.local/") }), new PairRedeemRequest("ABC-123", "Lab PC"));

    var result = await workflow.AnalyzeSelectedMatchAsync(new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T10:00:00Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null));
    Assert(result.RequestId == "11111111-1111-4111-8111-111111111111", "Expected workflow to reuse the same request id.");
    Assert(attempts == 2, "Expected exactly two upload attempts.");
    Assert(requestIds.Distinct().Count() == 1, "Expected upload retries to reuse the same request id.");
    Assert(result.FinalStatus.State == "completed", "Expected polling to terminate on completed state.");
    Assert(result.Events.Any(ev => ev.Kind == "observed" && ev.State == "processing"), "Expected polling events to be captured.");

    using var cts = new CancellationTokenSource();
    cts.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(() => workflow.AnalyzeSelectedMatchAsync(new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T10:00:00Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null), cts.Token), "Expected cancellation to flow.");
}

static LcuLeagueClientAdapter CreateAdapter(
    IReadOnlyList<LcuLockfileDiscoveryResult> discoveries,
    IReadOnlyList<HttpMessageHandler>? handlers = null,
    Func<LcuCredential, HttpMessageHandler>? handlerFactory = null)
{
    var discovery = new FakeDiscovery(discoveries);
    var factory = new LcuHttpClientFactory(
        TimeSpan.FromSeconds(2),
        handlerFactory ?? (_ => handlers?.Count > 0 ? handlers[0] : throw new InvalidOperationException("Missing handler.")));

    return new LcuLeagueClientAdapter(discovery, factory);
}

static HttpResponseMessage Json(string json) =>
    new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
        throw new InvalidOperationException(message);
    }
    catch (TException)
    {
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static CompanionAnalysisPayloadV1 LoadAnalysisFixture()
{
    var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "companion-analysis-request-v1.json");
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var payload = document.RootElement.GetProperty("payload");
    return new CompanionAnalysisPayloadV1(
        payload.GetProperty("requestedParticipantPuuid").GetString()!,
        payload.GetProperty("participants").EnumerateArray().Select(participant => new CompanionAnalysisParticipantV1(
            participant.GetProperty("puuid").GetString()!,
            participant.GetProperty("riotIdGameName").GetString()!,
            participant.GetProperty("riotIdTagline").GetString()!,
            participant.GetProperty("participantId").GetInt32(),
            participant.GetProperty("teamId").GetInt32(),
            participant.GetProperty("win").GetBoolean(),
            participant.GetProperty("championName").GetString()!,
            participant.GetProperty("kills").GetInt32(),
            participant.GetProperty("deaths").GetInt32(),
            participant.GetProperty("assists").GetInt32(),
            participant.TryGetProperty("totalDamageDealtToChampions", out var damage) ? damage.GetDouble() : null,
            participant.TryGetProperty("totalDamageTaken", out var taken) ? taken.GetDouble() : null,
            participant.TryGetProperty("timeCCingOthers", out var cc) ? cc.GetDouble() : null,
            participant.TryGetProperty("totalHealsOnTeammates", out var heal) ? heal.GetDouble() : null,
            participant.TryGetProperty("totalDamageShieldedOnTeammates", out var shield) ? shield.GetDouble() : null)).ToArray(),
        new CompanionAnalysisMatchV1(payload.GetProperty("match").GetProperty("matchId").GetString()!),
        null,
        payload.TryGetProperty("timelineUnavailableReason", out var unavailableReason) ? unavailableReason.GetString() : null);
}

#if false
sealed class FakeFileSystem : ILcuFileSystem
{
    private readonly IReadOnlyDictionary<string, string> _files;

    public FakeFileSystem(IReadOnlyDictionary<string, string> files)
    {
        _files = files;
    }

    public bool FileExists(string path) => _files.ContainsKey(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(_files[path]);
}

sealed class ThrowingFileSystem : ILcuFileSystem
{
    private readonly string _path;
    private readonly Exception _exception;

    public ThrowingFileSystem(string path, Exception exception)
    {
        _path = path;
        _exception = exception;
    }

    public bool FileExists(string path) => string.Equals(path, _path, StringComparison.OrdinalIgnoreCase);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) =>
        Task.FromException<string>(_exception);
}

sealed class FakeProcessLocator : ILeagueProcessLocator
{
    private readonly IReadOnlyList<string> _paths;

    public FakeProcessLocator(IReadOnlyList<string> paths)
    {
        _paths = paths;
    }

    public IReadOnlyList<string> GetExecutablePaths() => _paths;
}

sealed class FakeDiscovery : ILcuLockfileDiscovery
{
    private readonly Queue<LcuLockfileDiscoveryResult> _results;

    public FakeDiscovery(IReadOnlyList<LcuLockfileDiscoveryResult> results)
    {
        _results = new Queue<LcuLockfileDiscoveryResult>(results);
    }

    public Task<LcuLockfileDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_results.Count > 1 ? _results.Dequeue() : _results.Peek());
    }
}

sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        : this((request, cancellationToken) => Task.FromResult(handler(request, cancellationToken)))
    {
    }

    public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _handler(request, cancellationToken);
}
#endif
#if false
sealed class RecordingSessionHandler : HttpMessageHandler
{
    public HttpStatusCode NextDeleteStatus { get; set; } = HttpStatusCode.NoContent;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Delete)
        {
            return Task.FromResult(new HttpResponseMessage(NextDeleteStatus));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"sessionToken":"session-token-1","expiresAt":"2026-07-25T12:00:00Z","deviceName":"Lab PC","discordUserId":"discord-user-1"}""", Encoding.UTF8, "application/json")
        });
    }
}
#endif
#if false
sealed class ApiClientHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri? Uri, string? Authorization)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.Method, request.RequestUri, request.Headers.Authorization?.ToString()));
        return Task.FromResult(request.RequestUri?.AbsolutePath switch
        {
            "/companion/analyses" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"jobId":"job-1","duplicate":false}""", Encoding.UTF8, "application/json") },
            "/companion/analyses/job-1" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"schemaVersion":1,"jobId":"job-1","state":"processing","createdAt":"2026-07-25T10:00:00Z","completedAt":null,"reportAvailable":true,"deliveryState":"sending","userAction":"poll_status"}""", Encoding.UTF8, "application/json") },
            "/companion/version" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"schemaVersion":1,"current":{"latestVersion":"1.2.3","downloadUrl":"https://downloads.example.test/lol-companion","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}}""", Encoding.UTF8, "application/json") },
            "/companion/sessions/current" when request.Method == HttpMethod.Get => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"discordUserId":"discord-user-1","deviceName":"Lab PC","expiresAt":"2026-07-25T12:00:00Z"}""", Encoding.UTF8, "application/json") },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
        });
    }
}

sealed class WorkflowHandler : HttpMessageHandler
{
    private readonly Func<HttpResponseMessage> _uploadResponse;
    private readonly Func<(string requestId, string rawBody), HttpResponseMessage> _submitResponse;
    private readonly Func<HttpResponseMessage> _statusFactory;
    private readonly Func<HttpResponseMessage> _terminalFactory;

    public WorkflowHandler(
        Func<HttpResponseMessage> uploadResponse,
        Func<(string requestId, string rawBody), HttpResponseMessage> submitResponse,
        Func<HttpResponseMessage> statusFactory,
        Func<HttpResponseMessage> terminalFactory)
    {
        _uploadResponse = uploadResponse;
        _submitResponse = submitResponse;
        _statusFactory = statusFactory;
        _terminalFactory = terminalFactory;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (path == "/companion/analyses" && request.Method == HttpMethod.Post)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var requestId = JsonDocument.Parse(body).RootElement.GetProperty("requestId").GetString()!;
            var response = _submitResponse((requestId, body));
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return response;
            }
        }

        if (path.StartsWith("/companion/analyses/", StringComparison.OrdinalIgnoreCase))
        {
            return _statusFactory();
        }

        if (path == "/companion/version")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"schemaVersion":1,"current":{"latestVersion":"1.2.3","downloadUrl":"https://downloads.example.test/lol-companion","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}}""", Encoding.UTF8, "application/json")
            };
        }

        if (path == "/companion/pair/redeem")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"sessionToken":"session-token-1","expiresAt":"2026-07-25T12:00:00Z","deviceName":"Lab PC","discordUserId":"discord-user-1"}""", Encoding.UTF8, "application/json")
            };
        }

        return _uploadResponse();
    }
}
#endif
