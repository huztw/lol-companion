using System.Net;
using System.Text;
using System.Text.Json;
using LoLCompanion.Core.Api;
using LoLCompanion.Core.Contracts;

var handler = new FakeHandler();
using var httpClient = new HttpClient(handler)
{
    BaseAddress = new Uri("https://companion.local/")
};

var client = new CompanionApiClient(httpClient);
var response = await client.RedeemPairCodeAsync(new PairRedeemRequest("ABC-DEF-GHJ", "Tournament Laptop"));
var current = await client.GetCurrentSessionAsync("session-token-1");
await client.RevokeCurrentSessionAsync("session-token-1");
var submitFixturePath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..",
    "fixtures", "companion-analysis-request-v4.json"));
var submitBody = await File.ReadAllBytesAsync(submitFixturePath);
using var submitDocument = JsonDocument.Parse(submitBody);
var submit = await client.SubmitAnalysisAsync("session-token-2", submitBody);
var status = await client.GetAnalysisStatusAsync("session-token-2", "job/with spaces?=yes", default);
var version = await client.GetVersionAsync();

Assert(handler.Requests[0].RequestUri?.ToString() == "https://companion.local/companion/pair/redeem", "Expected redeem endpoint path.");
Assert(handler.Requests[0].Body?.Contains("\"pairCode\":\"ABC-DEF-GHJ\"") == true, "Expected pairCode JSON field.");
Assert(handler.Requests[0].Body?.Contains("\"deviceName\":\"Tournament Laptop\"") == true, "Expected deviceName JSON field.");
Assert(handler.Requests[1].RequestUri?.ToString() == "https://companion.local/companion/sessions/current", "Expected current session endpoint path.");
Assert(handler.Requests[1].Authorization == "Bearer session-token-1", "Expected bearer token on current session lookup.");
Assert(handler.Requests[2].RequestUri?.ToString() == "https://companion.local/companion/sessions/current", "Expected revoke session endpoint path.");
Assert(handler.Requests[2].Method == HttpMethod.Delete, "Expected delete method for revoke.");
Assert(handler.Requests[2].Authorization == "Bearer session-token-1", "Expected bearer token on revoke.");
Assert(handler.Requests[3].RequestUri?.ToString() == "https://companion.local/companion/analyses", "Expected submit endpoint path.");
Assert(handler.Requests[3].Method == HttpMethod.Post, "Expected POST for submit.");
Assert(handler.Requests[3].Authorization == "Bearer session-token-2", "Expected bearer token on submit.");
Assert(handler.Requests[3].ContentType == "application/json; charset=utf-8", "Expected JSON utf-8 content type on submit.");
Assert(handler.Requests[3].Body == Encoding.UTF8.GetString(submitBody), "Expected submit body to pass through unchanged.");
Assert(submitDocument.RootElement.GetProperty("schemaVersion").GetInt32() == 4, "Expected shared schema 4 fixture.");
Assert(submitDocument.RootElement.GetProperty("payload").GetProperty("participants")[0].GetProperty("championId").GetInt32() == 1, "Expected numeric champion id in shared fixture.");
Assert(submitDocument.RootElement.GetProperty("payload").GetProperty("match").GetProperty("gameDataVersion").GetString() == "16.14.794.5912", "Expected shared game data version.");
Assert(handler.Requests[4].RequestUri?.AbsoluteUri == "https://companion.local/companion/analyses/job%2Fwith%20spaces%3F%3Dyes", "Expected escaped job id path.");
Assert(handler.Requests[4].Method == HttpMethod.Get, "Expected GET for status.");
Assert(handler.Requests[4].Authorization == "Bearer session-token-2", "Expected bearer token on status.");
Assert(handler.Requests[5].RequestUri?.ToString() == "https://companion.local/companion/version", "Expected version endpoint path.");
Assert(handler.Requests[5].Authorization is null, "Expected version endpoint to be anonymous.");
Assert(handler.Requests.Count == 6, "Expected no additional companion API requests.");
Assert(response.DeviceName == "Tournament Laptop", "Expected response device name.");
Assert(response.DiscordUserId == "discord-user-1", "Expected response Discord user id.");
Assert(response.SessionToken == "session-token-1", "Expected response session token.");
Assert(current.DeviceName == "Tournament Laptop", "Expected current session device name.");
Assert(submit.JobId == "job-123", "Expected submit job id.");
Assert(submit.Duplicate is false, "Expected submit duplicate false.");
Assert(status.SchemaVersion == 1, "Expected status schema version.");
Assert(status.State == "completed_delivery_unknown", "Expected status state.");
Assert(status.ReportAvailable is true, "Expected report availability.");
Assert(status.DeliveryState == "unknown", "Expected delivery state.");
Assert(status.UserAction == "check_discord_report", "Expected user action.");
Assert(version.SchemaVersion == 1, "Expected version schema version.");
Assert(version.Current.LatestVersion == "1.2.3", "Expected latest version.");
Assert(version.Current.DownloadUrl == "https://example.com/Companion.zip", "Expected download url.");
Assert(version.Current.Sha256 == "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "Expected sha256 checksum.");
Assert(version.Analysis?.CurrentSchemaVersion == 4, "Expected current analysis schema version.");
Assert(version.Analysis?.MinimumSchemaVersion == 4, "Expected minimum analysis schema version.");

Console.WriteLine("LoL Companion contract smoke passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeHandler : HttpMessageHandler
{
    public List<FakeRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new FakeRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Content?.Headers.ContentType?.ToString()
        ));

        if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/companion/sessions/current")
        {
            var currentSessionJson = """
            {
              "discordUserId": "discord-user-1",
              "deviceName": "Tournament Laptop",
              "expiresAt": "2026-07-24T14:00:00Z"
            }
            """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(currentSessionJson, Encoding.UTF8, "application/json")
            };
        }

        if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/companion/analyses/job%2Fwith%20spaces%3F%3Dyes")
        {
            var statusJson = """
            {
              "schemaVersion": 1,
              "jobId": "job-123",
              "state": "completed_delivery_unknown",
              "createdAt": "2026-07-25T10:00:00.000Z",
              "completedAt": "2026-07-25T10:01:00.000Z",
              "reportAvailable": true,
              "deliveryState": "unknown",
              "userAction": "check_discord_report"
            }
            """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(statusJson, Encoding.UTF8, "application/json")
            };
        }

        if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/companion/version")
        {
            var versionJson = """
            {
              "schemaVersion": 1,
              "current": {
                "latestVersion": "1.2.3",
                "downloadUrl": "https://example.com/Companion.zip",
                "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
              },
              "analysis": {
                "currentSchemaVersion": 4,
                "minimumSchemaVersion": 4
              }
            }
            """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(versionJson, Encoding.UTF8, "application/json")
            };
        }

        if (request.Method == HttpMethod.Delete)
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/companion/analyses")
        {
            var submitJson = """
            {
              "jobId": "job-123",
              "duplicate": false
            }
            """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(submitJson, Encoding.UTF8, "application/json")
            };
        }

        var redeemJson = """
        {
          "sessionToken": "session-token-1",
          "expiresAt": "2026-07-24T14:00:00Z",
          "deviceName": "Tournament Laptop",
          "discordUserId": "discord-user-1"
        }
        """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(redeemJson, Encoding.UTF8, "application/json")
        };
    }
}

sealed record FakeRequest(HttpMethod Method, Uri? RequestUri, string? Authorization, string? Body, string? ContentType);
