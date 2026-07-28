using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LoLCompanion.Core.Analysis;
using LoLCompanion.Core.Api;
using LoLCompanion.Core.Contracts;
using LoLCompanion.Core.Lcu;
using LoLCompanion.Core.RemoteControl;

await TestLockfileParserAsync();
await TestDiscoveryAsync();
await TestLoopbackAndAuthAsync();
await TestCurrentSummonerDisplayNameFallbackAsync();
await TestRecentMatchMappingAsync();
await TestNestedRecentMatchMappingAsync();
await TestDetailAndTimelineAsync();
await TestConfigurationFieldMissingAsync();
await TestNestedDetailMappingAsync();
await TestChampionSummaryResolutionAsync();
await TestChampionSummaryFallbackAsync();
await TestFlatDetailCompatibilityAsync();
await TestDetailSchemaFailureAsync();
await TestStaleRefreshAsync();
await TestCancellationAsync();
await TestTransportFailureClassificationAsync();
await TestSharingLockedLockfileAsync();
await TestCompanionAnalysisNormalizerAsync();
await TestCompanionSessionManagerAsync();
await TestCompanionApiClientAsync();
await TestCompanionPairingControllerAsync();
await TestCompanionAnalysisWorkflowAsync();
await TestRemoteControlCoordinatorAsync();
await TestRemoteControlAnalyzeSubmissionAsync();

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

static async Task TestCurrentSummonerDisplayNameFallbackAsync()
{
    var defaultAdapter = CreateAdapter(
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
                    return Json("""{"summonerId":1001,"accountId":2002,"displayName":"Tester","gameName":"AltTester","tagLine":"#1234","puuid":"puuid-1"}""");
                }

                return Json("""{"games":{"games":[]}}""");
            })
        ]);

    var defaultSummoner = await defaultAdapter.GetCurrentSummonerAsync();
    Assert(defaultSummoner.DisplayName == "Tester", "Expected non-empty displayName to win over gameName.");

    var fallbackAdapter = CreateAdapter(
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
                    return Json("""{"summonerId":1001,"accountId":2002,"displayName":"","gameName":"AltTester","tagLine":"#1234","puuid":"puuid-1"}""");
                }

                return Json("""{"games":{"games":[]}}""");
            })
        ]);

    var fallbackSummoner = await fallbackAdapter.GetCurrentSummonerAsync();
    Assert(fallbackSummoner.DisplayName == "AltTester", "Expected gameName to fill empty displayName.");

    var failingAdapter = CreateAdapter(
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
                    return Json("""{"summonerId":1001,"accountId":2002,"displayName":"","gameName":"","tagLine":"#1234","puuid":"puuid-1"}""");
                }

                return Json("""{"games":{"games":[]}}""");
            })
        ]);

    await AssertThrowsAsync<LcuException>(() => failingAdapter.GetCurrentSummonerAsync(), "Expected empty names to fail.");
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

static async Task TestNestedRecentMatchMappingAsync()
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

                return Json("""
                {
                  "games": {
                    "games": [
                      {
                        "gameId": 21,
                        "queueId": 2400,
                        "gameMode": "ARAM",
                        "gameType": "MATCHED",
                        "gameCreation": 1721892000000,
                        "gameDuration": 1300,
                        "participantIdentities": [
                          { "participantId": 1, "player": { "puuid": "other-puuid", "summonerId": 9999, "accountId": 8888 } },
                          { "participantId": 2, "player": { "puuid": "puuid-1", "summonerId": 1001, "accountId": 2002 } }
                        ],
                        "participants": [
                          { "participantId": 1, "championId": 99, "stats": { "win": false, "kills": 0, "deaths": 10, "assists": 1 } },
                          { "participantId": 2, "championId": 1, "championName": "Annie", "stats": { "win": true, "kills": 8, "deaths": 2, "assists": 11 } }
                        ]
                      }
                    ]
                  }
                }
                """);
            })
        ]);

    var matches = await adapter.GetRecentMatchesAsync();
    Assert(matches.Count == 1, "Expected one mapped nested recent match.");
    Assert(matches[0].Win, "Expected nested participant identity to select current player.");
    Assert(matches[0].Kills == 8 && matches[0].Deaths == 2 && matches[0].Assists == 11, "Expected stats to come from participant.stats.");
    Assert(matches[0].ChampionId == 1, "Expected champion id from nested participant.");
    Assert(matches[0].ChampionName == "Annie", "Expected champion name to round-trip when present.");
}

static async Task TestChampionSummaryResolutionAsync()
{
    var fixture = LoadAnalysisFixture();
    var summaryCalls = 0;
    var championSummaryJson = JsonSerializer.Serialize(new[]
    {
        new { id = 1, name = "Annie" },
        new { id = 2, name = "Lux" },
        new { id = 3, name = "Leona" },
        new { id = 4, name = "Braum" },
        new { id = 5, name = "Seraphine" },
        new { id = 6, name = "Ahri" },
        new { id = 7, name = "Ezreal" },
        new { id = 8, name = "Nami" },
        new { id = 9, name = "Sett" },
        new { id = 10, name = "Sona" }
    });

    var participantIdentities = fixture.Participants.Select(participant => new
    {
        participantId = participant.ParticipantId,
        player = new
        {
            puuid = participant.Puuid,
            gameName = participant.RiotIdGameName,
            tagLine = participant.RiotIdTagline
        }
    }).ToArray();

    var nestedParticipants = fixture.Participants.Select(participant => new
    {
        participantId = participant.ParticipantId,
        teamId = participant.TeamId,
        championId = participant.ParticipantId,
        stats = new
        {
            win = participant.Win,
            kills = participant.Kills,
            deaths = participant.Deaths,
            assists = participant.Assists,
            totalDamageDealtToChampions = participant.TotalDamageDealtToChampions ?? 0,
            totalDamageTaken = participant.TotalDamageTaken ?? 0,
            timeCCingOthers = participant.TimeCCingOthers ?? 0
        }
    }).ToArray();

    var adapter = CreateAdapter(
        discoveries:
        [
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1234, "127.0.0.1", 2999, "https", "secret"), @"C:\League\lockfile", "found")
        ],
        handlers:
        [
            new RecordingHandler((request, _) =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Contains("champion-summary.json", StringComparison.Ordinal))
                {
                    summaryCalls++;
                    return Json(championSummaryJson);
                }

                if (path.Contains("current-summoner", StringComparison.Ordinal))
                {
                    return Json("""{"summonerId":123456789,"accountId":987654321,"displayName":"PlayerA","puuid":"player-1"}""");
                }

                if (path.Contains("/games/431945471", StringComparison.Ordinal))
                {
                    return Json(JsonSerializer.Serialize(new
                    {
                        gameId = 431945471,
                        queueId = 450,
                        gameMode = "ARAM",
                        gameType = "MATCHED",
                        gameCreation = 1721892000000,
                        gameDuration = 1600,
                        participantIdentities,
                        participants = nestedParticipants
                    }));
                }

                return Json(JsonSerializer.Serialize(new
                {
                    games = new
                    {
                        games = new[]
                        {
                            new
                            {
                                gameId = 431945471,
                                queueId = 450,
                                gameMode = "ARAM",
                                gameType = "MATCHED",
                                gameCreation = 1721892000000,
                                gameDuration = 1600,
                                participantIdentities,
                                participants = nestedParticipants
                            }
                        }
                    }
                }));
            })
        ]);

    var matches = await adapter.GetRecentMatchesAsync();
    var detail = await adapter.GetMatchDetailAsync(431945471);
    var normalized = new CompanionAnalysisNormalizer().Normalize(
        new LcuCurrentSummoner(123456789, 987654321, "PlayerA", fixture.RequestedParticipantPuuid),
        matches[0],
        detail,
        LcuTimelineResult.Unavailable("timeline_unavailable"));

    Assert(summaryCalls == 1, "Expected champion summary to be fetched only once per adapter.");
    Assert(matches[0].ChampionName == "Annie", "Expected recent champion name to resolve from summary cache.");
    Assert(detail.Participants[0].ChampionName == "Annie", "Expected detail champion name to resolve from summary cache.");
    Assert(normalized.Participants.Count == 10, "Expected nested detail payload to normalize successfully.");
    Assert(normalized.Participants[0].ChampionName == "Annie", "Expected normalized payload to preserve resolved champion names.");
}

static async Task TestChampionSummaryFallbackAsync()
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
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Contains("champion-summary.json", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                if (path.Contains("current-summoner", StringComparison.Ordinal))
                {
                    return Json("""{"summonerId":123456789,"accountId":987654321,"displayName":"PlayerA","puuid":"player-a"}""");
                }

                if (path.Contains("/games/431945472", StringComparison.Ordinal))
                {
                    return Json("""
                    {
                      "gameId": 431945472,
                      "queueId": 450,
                      "gameMode": "ARAM",
                      "gameType": "MATCHED",
                      "gameCreation": 1721892000000,
                      "gameDuration": 1600,
                      "participantIdentities": [
                        { "participantId": 1, "player": { "puuid": "player-a", "gameName": "PlayerA", "tagLine": "TST1" } }
                      ],
                      "participants": [
                        { "participantId": 1, "teamId": 100, "championId": 1, "stats": { "win": true, "kills": 8, "deaths": 2, "assists": 10 } }
                      ]
                    }
                    """);
                }

                return Json("""
                {
                  "games": {
                    "games": [
                      {
                        "gameId": 431945472,
                        "queueId": 450,
                        "gameMode": "ARAM",
                        "gameType": "MATCHED",
                        "gameCreation": 1721892000000,
                        "gameDuration": 1600,
                        "participantIdentities": [
                          { "participantId": 1, "player": { "puuid": "player-a", "gameName": "PlayerA", "tagLine": "TST1" } }
                        ],
                        "participants": [
                          { "participantId": 1, "teamId": 100, "championId": 1, "stats": { "win": true, "kills": 8, "deaths": 2, "assists": 10 } }
                        ]
                      }
                    ]
                  }
                }
                """);
            })
        ]);

    var matches = await adapter.GetRecentMatchesAsync();
    var detail = await adapter.GetMatchDetailAsync(431945472);

    Assert(matches[0].ChampionName is null, "Expected recent champion name to remain nullable when summary is unavailable.");
    Assert(detail.Participants[0].ChampionName == "Champion #1", "Expected detail champion name to fall back to a stable label.");
}

static async Task TestConfigurationFieldMissingAsync()
{
    var adapter = CreateAdapter(
        [new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1, "127.0.0.1", 2999, "https", "secret"), "lockfile", "found")],
        [new RecordingHandler((request, _) =>
        {
            var gameId = request.RequestUri!.AbsolutePath.EndsWith("/1", StringComparison.Ordinal) ? 1 : 2;
            var missingItem = gameId == 1;
            return Json($$"""{"gameId":{{gameId}},"queueId":450,"gameMode":"ARAM","gameType":"MATCHED","gameCreation":1,"gameDuration":1,"participants":[{"puuid":"p","participantId":1,"teamId":100,"win":true,"championId":1,"championName":"Annie","kills":0,"deaths":0,"assists":0,"item0":0,"item1":0,"item2":0,"item3":0,"item4":0,"item5":0{{(missingItem ? "" : ",\"item6\":0")}},"playerAugment1":0,"playerAugment2":0,"playerAugment3":0,"playerAugment4":0,"playerAugment5":0{{(missingItem ? ",\"playerAugment6\":0" : "")}}}] }""");
        })]);
    Assert((await adapter.GetMatchDetailAsync(1)).Participants[0].Items is null, "Expected missing item6 to produce null items.");
    Assert((await adapter.GetMatchDetailAsync(2)).Participants[0].Augments is null, "Expected missing playerAugment6 to produce null augments.");
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
                          "totalDamageShieldedOnTeammates": 500,
                          "item0": 1000, "item1": 1001, "item2": 1002, "item3": 1003, "item4": 1004, "item5": 1005, "item6": 1006,
                          "playerAugment1": 2001, "playerAugment2": 2002, "playerAugment3": 2003, "playerAugment4": 2004, "playerAugment5": 2005, "playerAugment6": 2006
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
                        },
                        {
                          "type": "BUILDING_KILL",
                          "timestamp": 62000,
                          "killerId": 0,
                          "victimId": 11,
                          "participantId": -1,
                          "assistingParticipantIds": [0, 2, 2, 3, 11],
                          "buildingType": "OUTER_TURRET"
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
    Assert(detail.Participants[0].Items!.SequenceEqual([1000, 1001, 1002, 1003, 1004, 1005, 1006]), "Expected adapter to parse item0..item6.");
    Assert(detail.Participants[0].Augments!.SequenceEqual([2001, 2002, 2003, 2004, 2005, 2006]), "Expected adapter to parse playerAugment1..playerAugment6.");
    var detailRequests = requests.Count(request => request?.AbsolutePath.EndsWith("/games/431945471", StringComparison.Ordinal) == true);
    var summaryRequests = requests.Count(request => request?.AbsolutePath.Contains("champion-summary.json", StringComparison.Ordinal) == true);
    var timelineRequestsBeforeCall = requests.Count(request => request?.AbsolutePath.Contains("/game-timelines/", StringComparison.Ordinal) == true);
    Assert(summaryRequests == 1, "Expected champion summary to be fetched once for detail mapping.");
    Assert(detailRequests == 1, "Detail should be fetched on demand only.");
    Assert(timelineRequestsBeforeCall == 0, "Timeline should not be requested before GetTimelineAsync is called.");

    var timeline = await adapter.GetTimelineAsync(431945471);
    Assert(timeline.IsAvailable, "Expected frame-based timeline to parse successfully.");
    Assert(timeline.Timeline?.Frames.Count == 1, "Expected timeline frame parsing.");
    Assert(timeline.Timeline?.Events.Count == 2, "Expected timeline events to come from frames[].events.");
    Assert(timeline.Timeline?.Events[1].KillerId is null, "Expected out-of-range killer id to become null.");
    Assert(timeline.Timeline?.Events[1].VictimId is null, "Expected out-of-range victim id to become null.");
    Assert(timeline.Timeline?.Events[1].ParticipantId is null, "Expected out-of-range participant id to become null.");
    Assert(timeline.Timeline?.Events[1].AssistingParticipantIds.SequenceEqual([2, 3]) == true, "Expected assists to be filtered and deduplicated.");

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

static async Task TestNestedDetailMappingAsync()
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
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (!path.Contains("/games/431945471", StringComparison.Ordinal))
                {
                    return Json("""{"frames": []}""");
                }

                return Json("""
                {
                  "gameId": 431945471,
                  "queueId": 450,
                  "gameMode": "ARAM",
                  "gameType": "MATCHED",
                  "gameCreation": 1721892000000,
                  "gameDuration": 1600,
                  "participantIdentities": [
                    { "participantId": 1, "player": { "puuid": "puuid-1", "gameName": "PlayerA", "tagLine": "TST1" } },
                    { "participantId": 2, "player": { "puuid": "puuid-2", "gameName": "PlayerB", "tagLine": "TST1" } }
                  ],
                  "participants": [
                    {
                      "participantId": 1,
                      "teamId": 100,
                      "championId": 1,
                      "stats": {
                        "win": true,
                        "kills": 8,
                        "deaths": 3,
                        "assists": 10,
                        "totalDamageDealtToChampions": 25000,
                        "totalDamageTaken": 14000,
                        "timeCCingOthers": 30,
                        "totalHeal": 900,
                        "damageSelfMitigated": 18000,
                        "damageDealtToTurrets": 450,
                        "damageDealtToObjectives": 700,
                        "totalTimeCrowdControlDealt": 95
                      }
                    },
                    {
                      "participantId": 2,
                      "teamId": 200,
                      "championId": 99,
                      "stats": {
                        "win": false,
                        "kills": 2,
                        "deaths": 7,
                        "assists": 5,
                        "totalDamageDealtToChampions": 12000,
                        "totalDamageTaken": 9000,
                        "timeCCingOthers": 18
                      }
                    }
                  ]
                }
                """);
            })
        ]);

    var detail = await adapter.GetMatchDetailAsync(431945471);
    Assert(detail.Participants.Count == 2, "Expected nested legacy detail participants.");
    Assert(detail.Participants[0].Puuid == "puuid-1", "Expected puuid from participant identity player.");
    Assert(detail.Participants[0].RiotIdGameName == "PlayerA", "Expected gameName fallback from participant identity player.");
    Assert(detail.Participants[0].RiotIdTagline == "TST1", "Expected tagLine from participant identity player.");
    Assert(detail.Participants[0].ChampionName == "Champion #1", "Expected missing championName to use the stable fallback label.");
    Assert(detail.Participants[0].TotalHealsOnTeammates is null, "Expected missing heal metric to remain null.");
    Assert(detail.Participants[0].TotalDamageShieldedOnTeammates is null, "Expected missing shield metric to remain null.");
    Assert(detail.Participants[0].TotalHeal == 900, "Expected nested total heal metric to round-trip.");
    Assert(detail.Participants[0].DamageSelfMitigated == 18000, "Expected nested self-mitigation metric to round-trip.");
    Assert(detail.Participants[0].DamageDealtToTurrets == 450, "Expected nested turret metric to round-trip.");
    Assert(detail.Participants[0].DamageDealtToObjectives == 700, "Expected nested objective metric to round-trip.");
    Assert(detail.Participants[0].TotalTimeCrowdControlDealt == 95, "Expected nested extended crowd-control metric to round-trip.");
}

static async Task TestFlatDetailCompatibilityAsync()
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
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (!path.Contains("/games/987654321", StringComparison.Ordinal))
                {
                    return Json("""{"frames": []}""");
                }

                return Json("""
                {
                  "gameId": 987654321,
                  "queueId": 2400,
                  "gameMode": "ARAM",
                  "gameType": "MATCHED",
                  "gameCreation": 1721892000000,
                  "gameDuration": 1500,
                  "participants": [
                    {
                      "puuid": "puuid-flat-1",
                      "riotIdGameName": "FlatPlayer",
                      "riotIdTagline": "TAG",
                      "participantId": 1,
                      "teamId": 100,
                      "win": true,
                      "championId": 1,
                      "championName": "Annie",
                      "kills": 7,
                      "deaths": 1,
                      "assists": 9,
                      "totalDamageDealtToChampions": 22000,
                      "totalDamageTaken": 9000,
                      "timeCCingOthers": 22,
                      "totalHealsOnTeammates": 300,
                      "totalDamageShieldedOnTeammates": 120,
                      "totalHeal": 800,
                      "damageSelfMitigated": 12000,
                      "damageDealtToTurrets": 350,
                      "damageDealtToObjectives": 640,
                      "totalTimeCrowdControlDealt": 72
                    }
                  ]
                }
                """);
            })
        ]);

    var detail = await adapter.GetMatchDetailAsync(987654321);
    Assert(detail.Participants.Count == 1, "Expected flat schema compatibility.");
    Assert(detail.Participants[0].Puuid == "puuid-flat-1", "Expected flat puuid to round-trip.");
    Assert(detail.Participants[0].ChampionName == "Annie", "Expected flat champion name to keep explicit schema priority.");
    Assert(detail.Participants[0].TotalHealsOnTeammates == 300, "Expected flat heal metric to round-trip.");
    Assert(detail.Participants[0].TotalDamageShieldedOnTeammates == 120, "Expected flat shield metric to round-trip.");
    Assert(detail.Participants[0].TotalHeal == 800, "Expected flat total heal metric to round-trip.");
    Assert(detail.Participants[0].DamageSelfMitigated == 12000, "Expected flat self-mitigation metric to round-trip.");
    Assert(detail.Participants[0].DamageDealtToTurrets == 350, "Expected flat turret metric to round-trip.");
    Assert(detail.Participants[0].DamageDealtToObjectives == 640, "Expected flat objective metric to round-trip.");
    Assert(detail.Participants[0].TotalTimeCrowdControlDealt == 72, "Expected flat extended crowd-control metric to round-trip.");
}

static async Task TestDetailSchemaFailureAsync()
{
    var missingParticipantAdapter = CreateAdapter(
        discoveries:
        [
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1234, "127.0.0.1", 2999, "https", "secret"), @"C:\League\lockfile", "found")
        ],
        handlers:
        [
            new RecordingHandler((request, _) =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (!path.Contains("/games/111111111", StringComparison.Ordinal))
                {
                    return Json("""{"frames": []}""");
                }

                return Json("""
                {
                  "gameId": 111111111,
                  "queueId": 450,
                  "gameMode": "ARAM",
                  "gameType": "MATCHED",
                  "gameCreation": 1721892000000,
                  "gameDuration": 1600,
                  "participantIdentities": [
                    { "participantId": 1, "player": { "puuid": "puuid-1", "gameName": "PlayerA", "tagLine": "TST1" } }
                  ],
                  "participants": [
                    {
                      "participantId": 2,
                      "teamId": 100,
                      "championId": 1,
                      "stats": { "win": true, "kills": 1, "deaths": 1, "assists": 1 }
                    }
                  ]
                }
                """);
            })
        ]);

    await AssertThrowsAsync<LcuException>(() => missingParticipantAdapter.GetMatchDetailAsync(111111111), "Expected missing participant mapping to fail safely.");

    var duplicateParticipantAdapter = CreateAdapter(
        discoveries:
        [
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1234, "127.0.0.1", 2999, "https", "secret"), @"C:\League\lockfile", "found")
        ],
        handlers:
        [
            new RecordingHandler((request, _) =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (!path.Contains("/games/222222222", StringComparison.Ordinal))
                {
                    return Json("""{"frames": []}""");
                }

                return Json("""
                {
                  "gameId": 222222222,
                  "queueId": 450,
                  "gameMode": "ARAM",
                  "gameType": "MATCHED",
                  "gameCreation": 1721892000000,
                  "gameDuration": 1600,
                  "participantIdentities": [
                    { "participantId": 1, "player": { "puuid": "puuid-1", "gameName": "PlayerA", "tagLine": "TST1" } }
                  ],
                  "participants": [
                    {
                      "participantId": 1,
                      "teamId": 100,
                      "championId": 1,
                      "stats": { "win": true, "kills": 1, "deaths": 1, "assists": 1 }
                    },
                    {
                      "participantId": 1,
                      "teamId": 100,
                      "championId": 2,
                      "stats": { "win": true, "kills": 2, "deaths": 2, "assists": 2 }
                    }
                  ]
                }
                """);
            })
        ]);

    await AssertThrowsAsync<LcuException>(() => duplicateParticipantAdapter.GetMatchDetailAsync(222222222), "Expected duplicate participant id to fail safely.");

    var missingIdentityAdapter = CreateAdapter(
        discoveries:
        [
            new LcuLockfileDiscoveryResult(LcuDiscoveryStatus.Found, new LcuCredential(1234, "127.0.0.1", 2999, "https", "secret"), @"C:\League\lockfile", "found")
        ],
        handlers:
        [
            new RecordingHandler((request, _) =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (!path.Contains("/games/333333333", StringComparison.Ordinal))
                {
                    return Json("""{"frames": []}""");
                }

                return Json("""
                {
                  "gameId": 333333333,
                  "queueId": 450,
                  "gameMode": "ARAM",
                  "gameType": "MATCHED",
                  "gameCreation": 1721892000000,
                  "gameDuration": 1600,
                  "participantIdentities": [
                    { "participantId": 7, "player": { "puuid": "puuid-7", "gameName": "PlayerG", "tagLine": "TST1" } }
                  ],
                  "participants": [
                    {
                      "participantId": 1,
                      "teamId": 100,
                      "championId": 1,
                      "stats": { "win": true, "kills": 1, "deaths": 1, "assists": 1 }
                    }
                  ]
                }
                """);
            })
        ]);

    await AssertThrowsAsync<LcuException>(() => missingIdentityAdapter.GetMatchDetailAsync(333333333), "Expected missing identity mapping to fail safely.");
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

static async Task TestSharingLockedLockfileAsync()
{
    var tempDirectory = Path.Combine(Path.GetTempPath(), "lol-companion-core-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDirectory);

    try
    {
        var lockfilePath = Path.Combine(tempDirectory, "lockfile");
        var lockfileContent = "LeagueClientUx:1234:2999:super-secret:https";
        await File.WriteAllTextAsync(lockfilePath, lockfileContent, Encoding.UTF8);

        await using var sharingLock = new FileStream(
            lockfilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var fileSystem = new SystemLcuFileSystem();
        var readTask = fileSystem.ReadAllTextAsync(lockfilePath, CancellationToken.None);
        Assert(await readTask == lockfileContent, "Expected lockfile to be readable while another sharing handle is open.");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => fileSystem.ReadAllTextAsync(lockfilePath, cts.Token), "Expected cancellation to flow from lockfile read.");
    }
    finally
    {
        try
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
        catch
        {
        }
    }
}

static async Task TestCompanionAnalysisNormalizerAsync()
{
    var normalizer = new CompanionAnalysisNormalizer();
    var fixture = LoadAnalysisFixture();
    var currentSummoner = new LcuCurrentSummoner(123456789, 987654321, "PlayerA", fixture.RequestedParticipantPuuid);
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
            participant.ChampionId,
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
                new LcuTimelineEventDto("CHAMPION_KILL", 61000, 1, 6, null, [2, 3]),
                new LcuTimelineEventDto("BUILDING_KILL", 121000, null, null, 4, [], TeamId: 100, BuildingType: "TOWER_BUILDING", TowerType: "OUTER_TURRET")
            ]),
        null);

    var normalized = normalizer.Normalize(currentSummoner, selectedMatch, matchDetail, timeline);
    Assert(normalized.RequestedParticipantPuuid == fixture.RequestedParticipantPuuid, "Expected requested participant to be preserved.");
    Assert(normalized.Participants.Count == 10, "Expected exactly ten participants.");
    Assert(normalized.Participants.Count(participant => participant.TeamId == 100) == 5, "Expected exactly five teammates.");
    Assert(normalized.Participants[0].ChampionId == 1, "Expected numeric champion id to be preserved.");
    Assert(normalized.Timeline is not null, "Expected timeline to be present for available timeline.");
    Assert(normalized.Timeline!.Frames.Count == 2, "Expected timeline frame bound to survive.");
    Assert(normalized.Timeline.Events.Count == 2, "Expected timeline event bound to survive.");
    Assert(normalized.Timeline.Events[1].TeamId == 100, "Expected building event team id to survive.");
    Assert(normalized.Timeline.Events[1].TowerType == "OUTER_TURRET", "Expected tower subtype to survive.");
    Assert(normalized.Participants.All(participant => participant.Items.Count == 0 && participant.Augments.Count == 0), "Expected missing final configuration to degrade to empty lists.");

    var configuredMatchDetail = matchDetail with
    {
        Participants = matchDetail.Participants.Select((participant, index) => index == 0
            ? participant with { Items = [1, 2, 3, 4, 5, 6, 7], Augments = [11, 12, 13, 14, 15, 16] }
            : participant).ToArray()
    };
    var configured = normalizer.Normalize(currentSummoner, selectedMatch, configuredMatchDetail, timeline);
    Assert(configured.Participants[0].Items.SequenceEqual([1, 2, 3, 4, 5, 6, 7]), "Expected item0..item6 shape to round-trip.");
    Assert(configured.Participants[0].Augments.SequenceEqual([11, 12, 13, 14, 15, 16]), "Expected playerAugment1..playerAugment6 shape to round-trip.");
    await AssertThrowsAsync<CompanionAnalysisException>(() => Task.FromResult(normalizer.Normalize(currentSummoner, selectedMatch, matchDetail with { Participants = matchDetail.Participants.Select((participant, index) => index == 0 ? participant with { Items = Enumerable.Repeat(1, 8).ToArray() } : participant).ToArray() }, timeline)), "Expected oversized item list to fail.");
    await AssertThrowsAsync<CompanionAnalysisException>(() => Task.FromResult(normalizer.Normalize(currentSummoner, selectedMatch, matchDetail with { Participants = matchDetail.Participants.Select((participant, index) => index == 0 ? participant with { Augments = [-1] } : participant).ToArray() }, timeline)), "Expected invalid augment id to fail.");

    var timelineWithSystemIds = new LcuTimelineResult(
        true,
        new LcuTimelineDto(
            [
                new LcuTimelineFrameDto(60000, new Dictionary<int, double> { [1] = 2500 })
            ],
            [
                new LcuTimelineEventDto("BUILDING_KILL", 61000, 0, 11, -1, [0, 2, 2, 3, 11], BuildingType: "TOWER_BUILDING", TowerType: "OUTER_TURRET")
            ]),
        null);

    var normalizedWithSystemIds = normalizer.Normalize(currentSummoner, selectedMatch, matchDetail, timelineWithSystemIds);
    Assert(normalizedWithSystemIds.Timeline!.Events[0].KillerId is null, "Expected out-of-range killer id to normalize as null.");
    Assert(normalizedWithSystemIds.Timeline.Events[0].VictimId is null, "Expected out-of-range victim id to normalize as null.");
    Assert(normalizedWithSystemIds.Timeline.Events[0].ParticipantId is null, "Expected out-of-range participant id to normalize as null.");
    Assert(normalizedWithSystemIds.Timeline.Events[0].AssistingParticipantIds.SequenceEqual([2, 3]) == true, "Expected invalid assists to be filtered and deduplicated.");

    await AssertThrowsAsync<CompanionAnalysisException>(() => Task.FromResult(normalizer.Normalize(
        currentSummoner,
        selectedMatch,
        matchDetail,
        new LcuTimelineResult(true, new LcuTimelineDto(
            [new LcuTimelineFrameDto(60000, new Dictionary<int, double> { [1] = 2500 })],
            [new LcuTimelineEventDto("BUILDING_KILL", 61000, null, null, 1, [], TeamId: 100, BuildingType: new string('x', 65))]), null))),
        "Expected oversized timeline event label to fail.");

    var unavailable = normalizer.Normalize(
        currentSummoner,
        selectedMatch,
        matchDetail,
        new LcuTimelineResult(false, null, "timeline missing"));
    Assert(unavailable.Timeline is null, "Expected unavailable timeline to omit timeline payload.");
    Assert(unavailable.TimelineUnavailableReason == "timeline missing", "Expected unavailable reason to round-trip.");

    var serializationMatchDetail = new LcuMatchDetailDto(
        matchDetail.GameId,
        matchDetail.QueueId,
        matchDetail.GameMode,
        matchDetail.GameType,
        matchDetail.GameCreation,
        matchDetail.GameDuration,
        matchDetail.Participants.Select((participant, index) => index == 0
            ? participant with
            {
                Win = false,
                Kills = 0,
                TimeCCingOthers = null,
                TotalHealsOnTeammates = null,
                TotalDamageShieldedOnTeammates = null
            }
            : participant).ToArray());

    var serializationPayload = normalizer.Normalize(
        currentSummoner,
        selectedMatch,
        serializationMatchDetail,
        timeline);
    var serializedRequest = new CompanionAnalysisSubmitRequest(
        "request-1",
        serializationMatchDetail.GameId,
        CompanionAnalysisContract.SchemaVersion,
        serializationMatchDetail.QueueId,
        serializationPayload);
    using var serializedDocument = JsonDocument.Parse(normalizer.SerializeRequest(serializedRequest));
    var serializedRoot = serializedDocument.RootElement;
    var serializedPayload = serializedRoot.GetProperty("payload");
    Assert(!serializedPayload.TryGetProperty("timelineUnavailableReason", out _), "Expected timeline unavailable reason to be omitted when timeline is present.");
    var serializedParticipants = serializedPayload.GetProperty("participants");
    var firstParticipant = serializedParticipants[0];
    Assert(firstParticipant.GetProperty("win").ValueKind is JsonValueKind.False, "Expected false to be preserved in payload.");
    Assert(firstParticipant.GetProperty("kills").GetInt32() == 0, "Expected zero to be preserved in payload.");
    Assert(!firstParticipant.TryGetProperty("timeCCingOthers", out _), "Expected null metric to be omitted.");
    Assert(!firstParticipant.TryGetProperty("totalHealsOnTeammates", out _), "Expected null heal metric to be omitted.");
    Assert(!firstParticipant.TryGetProperty("totalDamageShieldedOnTeammates", out _), "Expected null shield metric to be omitted.");
    Assert(firstParticipant.GetProperty("championId").GetInt32() == 1, "Expected numeric champion id to be serialized.");

    var unavailableRequest = new CompanionAnalysisSubmitRequest(
        "request-2",
        serializationMatchDetail.GameId,
        CompanionAnalysisContract.SchemaVersion,
        serializationMatchDetail.QueueId,
        unavailable with
        {
            Timeline = null
        });
    using var unavailableDocument = JsonDocument.Parse(normalizer.SerializeRequest(unavailableRequest));
    var unavailablePayload = unavailableDocument.RootElement.GetProperty("payload");
    Assert(!unavailablePayload.TryGetProperty("timeline", out _), "Expected timeline to be omitted when unavailable.");
    Assert(unavailablePayload.GetProperty("timelineUnavailableReason").GetString() == "timeline missing", "Expected unavailable reason to be present when timeline is missing.");

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
            matchDetail.Participants.Select((participant, index) => index == 0 ? participant with { ChampionId = 0 } : participant).ToArray()),
        timeline)), "Expected non-positive champion id to fail.");

    await AssertThrowsAsync<CompanionAnalysisException>(() => Task.FromResult(normalizer.Normalize(
        currentSummoner,
        selectedMatch,
        new LcuMatchDetailDto(matchDetail.GameId, matchDetail.QueueId, matchDetail.GameMode, matchDetail.GameType, matchDetail.GameCreation, matchDetail.GameDuration,
            matchDetail.Participants.Select((participant, index) => index == 0 ? participant with { ChampionId = 10_000_000 } : participant).ToArray()),
        timeline)), "Expected oversized champion id to fail.");

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

    var selectedMatch = new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T10:00:00Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null);
    var result = await workflow.AnalyzeSelectedMatchAsync(selectedMatch);
    Assert(result.RequestId == "11111111-1111-4111-8111-111111111111", "Expected workflow to reuse the same request id.");
    Assert(attempts == 2, "Expected exactly two upload attempts.");
    Assert(requestIds.Distinct().Count() == 1, "Expected upload retries to reuse the same request id.");
    Assert(result.FinalStatus.State == "completed", "Expected polling to terminate on completed state.");
    Assert(result.Events.Any(ev => ev.Kind == "observed" && ev.State == "processing"), "Expected polling events to be captured.");

    var statusPolls = 0;
    var submitOnlyHandler = new RecordingHandler((request, _) =>
    {
        var path = request.RequestUri?.AbsolutePath;
        if (path == "/companion/version")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"schemaVersion":1,"analysis":{"currentSchemaVersion":4,"minimumSchemaVersion":4}}""", Encoding.UTF8, "application/json")
            };
        }
        if (path == "/companion/analyses" && request.Method == HttpMethod.Post)
        {
            Assert(
                request.Headers.TryGetValues("X-Companion-Control-Job-Id", out var values) &&
                values.Single() == "22222222-2222-4222-8222-222222222222",
                "Expected submit-only workflow to send the server-issued control job id.");
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("""{"jobId":"job-submit-only","duplicate":false}""", Encoding.UTF8, "application/json")
            };
        }
        if (path?.StartsWith("/companion/analyses/", StringComparison.Ordinal) == true)
        {
            statusPolls++;
        }
        throw new InvalidOperationException($"Unexpected submit-only path: {path}");
    });
    var submitOnlyWorkflow = new CompanionAnalysisWorkflow(
        source,
        new CompanionApiClient(new HttpClient(submitOnlyHandler) { BaseAddress = new Uri("https://companion.local/") }),
        sessionManager,
        new CompanionAnalysisNormalizer());
    var submission = await submitOnlyWorkflow.SubmitSelectedMatchAsync(
        selectedMatch,
        "22222222-2222-4222-8222-222222222222");
    Assert(submission.JobId == "job-submit-only", "Expected remote workflow to return after analysis submission.");
    Assert(statusPolls == 0, "Remote workflow must not wait for analysis terminal state inside the 60-second control job.");

    using var cts = new CancellationTokenSource();
    cts.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(() => workflow.AnalyzeSelectedMatchAsync(selectedMatch, cancellationToken: cts.Token), "Expected cancellation to flow.");
}

static async Task TestRemoteControlCoordinatorAsync()
{
    string? submittedBody = null;
    var controlJobId = "22222222-2222-4222-8222-222222222222";
    var handler = new RecordingHandler(async (request, cancellationToken) =>
    {
        var path = request.RequestUri?.AbsolutePath;
        if (path == "/companion/pair/redeem")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"sessionToken":"session-token","expiresAt":"2099-07-28T00:00:00Z","deviceName":"Test PC","discordUserId":"owner-1"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
        if (path == "/companion/control/jobs/next")
        {
            Assert(
                request.Headers.TryGetValues("X-Companion-Remote-Control-Protocol", out var values) &&
                values.Single() == "remote-control-v1",
                "Expected remote-control protocol header.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"protocolVersion":"remote-control-v1","controlJobId":"{{controlJobId}}","type":"list_recent_matches"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
        if (path == $"/companion/control/jobs/{controlJobId}/result")
        {
            submittedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
            };
        }
        throw new InvalidOperationException($"Unexpected path: {path}");
    });
    using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
    var api = new CompanionApiClient(httpClient);
    var sessions = new InMemoryCompanionSessionManager();
    await sessions.RedeemAsync(api, new PairRedeemRequest("ABC-DEF-GHJ", "Test PC"));
    var statuses = new List<string>();
    var matches = Enumerable.Range(1, 25)
        .Select(index => new LcuRecentMatchSummary(
            1_000 + index,
            450,
            "ARAM",
            "MATCHED_GAME",
            DateTimeOffset.Parse("2026-07-28T00:00:00Z"),
            TimeSpan.FromSeconds(1_000 + index),
            index % 2 == 0,
            index,
            $"Champion {index}",
            index,
            index,
            index,
            true,
            null))
        .ToArray();
    var coordinator = new CompanionRemoteControlCoordinator(
        api,
        sessions,
        _ => Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>(matches),
        (_, _, _) => throw new InvalidOperationException("Analysis should not run for list jobs."),
        pollInterval: TimeSpan.Zero);
    coordinator.StatusChanged += status => statuses.Add(status.State);

    await coordinator.PollOnceAsync();

    Assert(submittedBody is not null, "Expected list result submission.");
    using var submitted = JsonDocument.Parse(submittedBody!);
    var submittedMatches = submitted.RootElement.GetProperty("matches");
    Assert(submittedMatches.GetArrayLength() == 20, "Expected recent list to be capped at 20.");
    Assert(submittedMatches[0].TryGetProperty("durationSeconds", out _), "Expected seconds-based duration field.");
    Assert(submittedMatches[0].TryGetProperty("supported", out _), "Expected bounded supported field.");
    Assert(statuses.Contains("listing") && statuses.Contains("waiting_selection"), "Expected remote-control status events.");
}

static async Task TestRemoteControlAnalyzeSubmissionAsync()
{
    string? submittedBody = null;
    var controlJobId = "33333333-3333-4333-8333-333333333333";
    var handler = new RecordingHandler(async (request, cancellationToken) =>
    {
        var path = request.RequestUri?.AbsolutePath;
        if (path == "/companion/pair/redeem")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"sessionToken":"session-token","expiresAt":"2099-07-28T00:00:00Z","deviceName":"Test PC","discordUserId":"owner-1"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
        if (path == "/companion/control/jobs/next")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$$"""{"protocolVersion":"remote-control-v1","controlJobId":"{{{controlJobId}}}","type":"analyze_match","gameId":431945471,"queueId":450,"gameMode":"ARAM","gameType":"MATCHED_GAME","createdAt":"2026-07-28T00:00:00Z","durationSeconds":1200,"win":true,"championId":1,"championName":"Annie","kills":8,"deaths":2,"assists":10,"isSupported":true}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
        if (path == $"/companion/control/jobs/{controlJobId}/result")
        {
            submittedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
            };
        }
        throw new InvalidOperationException($"Unexpected path: {path}");
    });
    using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
    var api = new CompanionApiClient(httpClient);
    var sessions = new InMemoryCompanionSessionManager();
    await sessions.RedeemAsync(api, new PairRedeemRequest("ABC-DEF-GHJ", "Test PC"));
    var statuses = new List<string>();
    var coordinator = new CompanionRemoteControlCoordinator(
        api,
        sessions,
        _ => throw new InvalidOperationException("Recent matches should not load for analyze jobs."),
        (_, requestId, _) => Task.FromResult(new CompanionAnalysisSubmissionResult(
            requestId,
            "analysis-job-1",
            false,
            [])),
        pollInterval: TimeSpan.Zero);
    coordinator.StatusChanged += status => statuses.Add(status.State);

    await coordinator.PollOnceAsync();

    Assert(submittedBody?.Contains("\"analysisJobId\":\"analysis-job-1\"", StringComparison.Ordinal) == true, "Expected control completion immediately after analysis submission.");
    Assert(statuses.Contains("analyzing") && statuses.Contains("submitted"), "Expected remote analysis to report submitted instead of terminal completion.");
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

static CompanionAnalysisPayloadV2 LoadAnalysisFixture()
{
    var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "companion-analysis-request-v4.json");
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var payload = document.RootElement.GetProperty("payload");
    return new CompanionAnalysisPayloadV2(
        payload.GetProperty("requestedParticipantPuuid").GetString()!,
        payload.GetProperty("participants").EnumerateArray().Select(participant => new CompanionAnalysisParticipantV2(
            participant.GetProperty("puuid").GetString()!,
            participant.GetProperty("riotIdGameName").GetString()!,
            participant.GetProperty("riotIdTagline").GetString()!,
            participant.GetProperty("participantId").GetInt32(),
            participant.GetProperty("teamId").GetInt32(),
            participant.GetProperty("win").GetBoolean(),
            participant.GetProperty("championId").GetInt32(),
            participant.GetProperty("championName").GetString()!,
            participant.GetProperty("kills").GetInt32(),
            participant.GetProperty("deaths").GetInt32(),
            participant.GetProperty("assists").GetInt32(),
            participant.TryGetProperty("totalDamageDealtToChampions", out var damage) ? damage.GetDouble() : null,
            participant.TryGetProperty("totalDamageTaken", out var taken) ? taken.GetDouble() : null,
            participant.TryGetProperty("timeCCingOthers", out var cc) ? cc.GetDouble() : null,
            participant.TryGetProperty("totalHealsOnTeammates", out var heal) ? heal.GetDouble() : null,
            participant.TryGetProperty("totalDamageShieldedOnTeammates", out var shield) ? shield.GetDouble() : null,
            participant.TryGetProperty("items", out var items) ? items.EnumerateArray().Select(item => item.GetInt32()).ToArray() : Enumerable.Repeat(0, 7).ToArray(),
            participant.TryGetProperty("augments", out var augments) ? augments.EnumerateArray().Select(augment => augment.GetInt32()).ToArray() : Enumerable.Repeat(0, 6).ToArray())).ToArray(),
        new CompanionAnalysisMatchV2(
            payload.GetProperty("match").GetProperty("matchId").GetString()!,
            payload.TryGetProperty("match", out var match) && match.TryGetProperty("gameDataVersion", out var gameDataVersion)
                ? gameDataVersion.GetString()!
                : "16.14.794.5912"),
        new CompanionAnalysisTimelineV2(
            payload.GetProperty("timeline").GetProperty("frames").EnumerateArray().Select(frame => new CompanionAnalysisTimelineFrameV2(
                frame.GetProperty("timestamp").GetInt64(),
                frame.GetProperty("participantFrames").EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => new CompanionAnalysisParticipantFrameV2(property.Value.GetProperty("totalGold").GetDouble())))).ToArray(),
            payload.GetProperty("timeline").GetProperty("events").EnumerateArray().Select(@event => new CompanionAnalysisTimelineEventV2(
                @event.GetProperty("type").GetString()!,
                @event.GetProperty("timestamp").GetInt64(),
                @event.TryGetProperty("killerId", out var killer) && killer.ValueKind != JsonValueKind.Null ? killer.GetInt32() : null,
                @event.TryGetProperty("victimId", out var victim) && victim.ValueKind != JsonValueKind.Null ? victim.GetInt32() : null,
                @event.TryGetProperty("participantId", out var participantId) && participantId.ValueKind != JsonValueKind.Null ? participantId.GetInt32() : null,
                @event.GetProperty("assistingParticipantIds").EnumerateArray().Select(value => value.GetInt32()).ToArray(),
                @event.TryGetProperty("teamId", out var teamId) && teamId.ValueKind != JsonValueKind.Null ? teamId.GetInt32() : null,
                @event.TryGetProperty("buildingType", out var buildingType) && buildingType.ValueKind != JsonValueKind.Null ? buildingType.GetString() : null,
                @event.TryGetProperty("towerType", out var towerType) && towerType.ValueKind != JsonValueKind.Null ? towerType.GetString() : null,
                @event.TryGetProperty("laneType", out var laneType) && laneType.ValueKind != JsonValueKind.Null ? laneType.GetString() : null)).ToArray()),
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
