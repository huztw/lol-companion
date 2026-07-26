using System.Net;
using System.Text;
using System.Text.Json;
using LoLCompanion.Core.Analysis;
using LoLCompanion.Core.Contracts;
using LoLCompanion.Core.Lcu;

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

sealed class ThrowingHttpMessageHandler : HttpMessageHandler
{
    private readonly Exception _exception;

    public ThrowingHttpMessageHandler(Exception exception)
    {
        _exception = exception;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(_exception);
}

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

sealed class ApiClientHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri? Uri, string? Authorization)> Requests { get; } = [];
    private readonly HttpStatusCode _errorStatusCode;
    private readonly string? _errorBody;

    public ApiClientHandler()
    {
    }

    public ApiClientHandler(HttpStatusCode errorStatusCode, string errorBody)
    {
        _errorStatusCode = errorStatusCode;
        _errorBody = errorBody;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.Method, request.RequestUri, request.Headers.Authorization?.ToString()));
        return Task.FromResult(request.RequestUri?.AbsolutePath switch
        {
            "/companion/analyses" when _errorStatusCode != 0 => new HttpResponseMessage(_errorStatusCode) { Content = new StringContent(_errorBody ?? string.Empty, Encoding.UTF8, "application/json") },
            "/companion/analyses" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"jobId":"job-1","duplicate":false}""", Encoding.UTF8, "application/json") },
            "/companion/analyses/job-1" when _errorStatusCode != 0 => new HttpResponseMessage(_errorStatusCode) { Content = new StringContent(_errorBody ?? string.Empty, Encoding.UTF8, "application/json") },
            "/companion/analyses/job-1" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"schemaVersion":1,"jobId":"job-1","state":"processing","createdAt":"2026-07-25T10:00:00Z","completedAt":null,"reportAvailable":true,"deliveryState":"sending","userAction":"poll_status"}""", Encoding.UTF8, "application/json") },
            "/companion/version" when _errorStatusCode != 0 => new HttpResponseMessage(_errorStatusCode) { Content = new StringContent(_errorBody ?? string.Empty, Encoding.UTF8, "application/json") },
            "/companion/version" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"schemaVersion":1,"current":{"latestVersion":"1.2.3","downloadUrl":"https://downloads.example.test/lol-companion","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}}""", Encoding.UTF8, "application/json") },
            "/companion/sessions/current" when request.Method == HttpMethod.Get && _errorStatusCode != 0 => new HttpResponseMessage(_errorStatusCode) { Content = new StringContent(_errorBody ?? string.Empty, Encoding.UTF8, "application/json") },
            "/companion/sessions/current" when request.Method == HttpMethod.Get => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"discordUserId":"discord-user-1","deviceName":"Lab PC","expiresAt":"2026-07-25T12:00:00Z"}""", Encoding.UTF8, "application/json") },
            _ when _errorStatusCode != 0 => new HttpResponseMessage(_errorStatusCode) { Content = new StringContent(_errorBody ?? string.Empty, Encoding.UTF8, "application/json") },
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
    private int _statusCalls;

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
            return _submitResponse((requestId, body));
        }

        if (path.StartsWith("/companion/analyses/", StringComparison.OrdinalIgnoreCase))
        {
            return _statusCalls++ == 0 ? _statusFactory() : _terminalFactory();
        }

        if (path == "/companion/version")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"schemaVersion":1,"current":{"latestVersion":"1.2.3","downloadUrl":"https://downloads.example.test/lol-companion","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"},"analysis":{"currentSchemaVersion":2,"minimumSchemaVersion":2}}""", Encoding.UTF8, "application/json")
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

sealed class SessionHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"sessionToken":"session-token-1","expiresAt":"2026-07-25T12:00:00Z","deviceName":"Lab PC","discordUserId":"discord-user-1"}""", Encoding.UTF8, "application/json")
        });
    }
}

sealed class FakeAnalysisSource : ICompanionLeagueAnalysisSource
{
    private readonly LcuCurrentSummoner _currentSummoner;
    private readonly LcuMatchDetailDto _matchDetail;
    private readonly LcuTimelineResult _timeline;

    public FakeAnalysisSource(LcuCurrentSummoner currentSummoner, LcuMatchDetailDto matchDetail, LcuTimelineResult timeline)
    {
        _currentSummoner = currentSummoner;
        _matchDetail = matchDetail;
        _timeline = timeline;
    }

    public Task<LcuCurrentSummoner> GetCurrentSummonerAsync(CancellationToken cancellationToken = default) => Task.FromResult(_currentSummoner);

    public Task<LcuMatchDetailDto> GetMatchDetailAsync(long gameId, CancellationToken cancellationToken = default) => Task.FromResult(_matchDetail);

    public Task<LcuTimelineResult> GetTimelineAsync(long gameId, CancellationToken cancellationToken = default) => Task.FromResult(_timeline);
}
