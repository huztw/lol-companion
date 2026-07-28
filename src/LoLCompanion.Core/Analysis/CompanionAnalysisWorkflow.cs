using LoLCompanion.Core.Api;
using LoLCompanion.Core.Contracts;
using LoLCompanion.Core.Lcu;

namespace LoLCompanion.Core.Analysis;

public interface ICompanionLeagueAnalysisSource
{
    Task<LcuCurrentSummoner> GetCurrentSummonerAsync(CancellationToken cancellationToken = default);
    Task<LcuMatchDetailDto> GetMatchDetailAsync(long gameId, CancellationToken cancellationToken = default);
    Task<LcuTimelineResult> GetTimelineAsync(long gameId, CancellationToken cancellationToken = default);
}

public sealed record CompanionAnalysisWorkflowOptions(
    int MaxUploadAttempts,
    int MaxPollAttempts,
    TimeSpan UploadRetryDelay,
    TimeSpan InitialPollDelay,
    TimeSpan MaxPollDelay,
    TimeSpan TotalPollTimeout
)
{
    public static CompanionAnalysisWorkflowOptions Default { get; } = new(
        MaxUploadAttempts: 2,
        MaxPollAttempts: 6,
        UploadRetryDelay: TimeSpan.FromSeconds(1),
        InitialPollDelay: TimeSpan.FromSeconds(1),
        MaxPollDelay: TimeSpan.FromSeconds(8),
        TotalPollTimeout: TimeSpan.FromSeconds(45)
    );
}

public sealed record CompanionAnalysisWorkflowEvent(
    string Kind,
    string Stage,
    int Attempt,
    string? JobId = null,
    string? State = null,
    string? UserAction = null
);

public sealed record CompanionAnalysisWorkflowResult(
    string RequestId,
    string JobId,
    bool Duplicate,
    CompanionAnalysisStatusDtoV1 FinalStatus,
    IReadOnlyList<CompanionAnalysisWorkflowEvent> Events
);

public sealed record CompanionAnalysisSubmissionResult(
    string RequestId,
    string JobId,
    bool Duplicate,
    IReadOnlyList<CompanionAnalysisWorkflowEvent> Events
);

public sealed class CompanionAnalysisWorkflow
{
    private readonly ICompanionLeagueAnalysisSource _leagueSource;
    private readonly CompanionApiClient _apiClient;
    private readonly ICompanionSessionManager _sessionManager;
    private readonly CompanionAnalysisNormalizer _normalizer;
    private readonly CompanionAnalysisWorkflowOptions _options;
    private readonly Func<Guid> _requestIdFactory;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public CompanionAnalysisWorkflow(
        ICompanionLeagueAnalysisSource leagueSource,
        CompanionApiClient apiClient,
        ICompanionSessionManager sessionManager,
        CompanionAnalysisNormalizer normalizer,
        CompanionAnalysisWorkflowOptions? options = null,
        Func<Guid>? requestIdFactory = null,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _leagueSource = leagueSource;
        _apiClient = apiClient;
        _sessionManager = sessionManager;
        _normalizer = normalizer;
        _options = options ?? CompanionAnalysisWorkflowOptions.Default;
        _requestIdFactory = requestIdFactory ?? Guid.NewGuid;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public async Task<CompanionAnalysisWorkflowResult> AnalyzeSelectedMatchAsync(
        LcuRecentMatchSummary selectedMatch,
        string? serverRequestId = null,
        CancellationToken cancellationToken = default)
    {
        var submission = await SubmitSelectedMatchCoreAsync(selectedMatch, serverRequestId, cancellationToken);
        var finalStatus = await PollUntilTerminalAsync(
            submission.JobId,
            submission.Events,
            cancellationToken);

        return new CompanionAnalysisWorkflowResult(
            submission.RequestId,
            submission.JobId,
            submission.Duplicate,
            finalStatus,
            submission.Events);
    }

    public async Task<CompanionAnalysisSubmissionResult> SubmitSelectedMatchAsync(
        LcuRecentMatchSummary selectedMatch,
        string serverRequestId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverRequestId))
        {
            throw new CompanionAnalysisException(
                "invalid_request_id",
                "Remote analysis requires a server-issued request id.",
                false);
        }

        var submission = await SubmitSelectedMatchCoreAsync(
            selectedMatch,
            serverRequestId,
            cancellationToken);
        return new CompanionAnalysisSubmissionResult(
            submission.RequestId,
            submission.JobId,
            submission.Duplicate,
            submission.Events);
    }

    private async Task<(
        string RequestId,
        string JobId,
        bool Duplicate,
        List<CompanionAnalysisWorkflowEvent> Events)> SubmitSelectedMatchCoreAsync(
        LcuRecentMatchSummary selectedMatch,
        string? serverRequestId,
        CancellationToken cancellationToken)
    {
        var events = new List<CompanionAnalysisWorkflowEvent>();

        if (!selectedMatch.IsSupported)
        {
            throw new CompanionAnalysisException("unsupported_queue", selectedMatch.UnsupportedReason ?? "Only supported ARAM queues can be analyzed.");
        }

        events.Add(new CompanionAnalysisWorkflowEvent("started", "compatibility", 0));
        var version = await _apiClient.GetVersionAsync(cancellationToken);
        if (version.Analysis is null ||
            version.Analysis.MinimumSchemaVersion > CompanionAnalysisContract.SchemaVersion ||
            version.Analysis.CurrentSchemaVersion < CompanionAnalysisContract.SchemaVersion)
        {
            var downloadUrl = version.Current?.DownloadUrl;
            throw new CompanionAnalysisException(
                "analysis_schema_update_required",
                string.IsNullOrWhiteSpace(downloadUrl)
                    ? "A compatible LoL Companion version is required for timeline-v2 analysis."
                    : $"A compatible LoL Companion version is required for timeline-v2 analysis. Download: {downloadUrl}",
                false);
        }

        events.Add(new CompanionAnalysisWorkflowEvent("completed", "compatibility", 0));
        events.Add(new CompanionAnalysisWorkflowEvent("started", "current_summoner", 0));
        var currentSummoner = await _leagueSource.GetCurrentSummonerAsync(cancellationToken);

        events.Add(new CompanionAnalysisWorkflowEvent("started", "match_detail", 0));
        var matchDetail = await _leagueSource.GetMatchDetailAsync(selectedMatch.GameId, cancellationToken);

        events.Add(new CompanionAnalysisWorkflowEvent("started", "timeline", 0));
        var timeline = await _leagueSource.GetTimelineAsync(selectedMatch.GameId, cancellationToken);

        events.Add(new CompanionAnalysisWorkflowEvent("started", "normalize", 0));
        var payload = _normalizer.Normalize(currentSummoner, selectedMatch, matchDetail, timeline);
        var requestId = serverRequestId ?? _requestIdFactory().ToString();
        if (!Guid.TryParse(requestId, out _)) throw new CompanionAnalysisException("invalid_request_id", "Analysis request id must be a server-issued UUID.", false);
        var request = new CompanionAnalysisSubmitRequest(
            requestId,
            selectedMatch.GameId,
            CompanionAnalysisContract.SchemaVersion,
            selectedMatch.QueueId,
            payload
        );
        var utf8Body = _normalizer.SerializeRequest(request);

        CompanionAnalysisSubmitResponse submitResponse = await SubmitWithRetryAsync(
            requestId,
            utf8Body,
            events,
            cancellationToken,
            serverRequestId);
        return (requestId, submitResponse.JobId, submitResponse.Duplicate, events);
    }

    private async Task<CompanionAnalysisSubmitResponse> SubmitWithRetryAsync(
        string requestId,
        byte[] utf8Body,
        List<CompanionAnalysisWorkflowEvent> events,
        CancellationToken cancellationToken,
        string? controlJobId)
    {
        for (var attempt = 1; attempt <= _options.MaxUploadAttempts; attempt++)
        {
            events.Add(new CompanionAnalysisWorkflowEvent("started", "upload", attempt));
            var sessionToken = _sessionManager.GetRequiredSessionToken();

            try
            {
                var response = await _apiClient.SubmitAnalysisAsync(
                    sessionToken,
                    utf8Body,
                    cancellationToken,
                    controlJobId);
                events.Add(new CompanionAnalysisWorkflowEvent("completed", "upload", attempt, response.JobId));
                return response;
            }
            catch (CompanionApiException exception) when (exception.StatusCode == 401)
            {
                _sessionManager.ClearUnauthorized();
                throw new CompanionAnalysisException("session_unauthorized", "Companion session expired during analysis upload.", false, exception);
            }
            catch (Exception exception) when (attempt < _options.MaxUploadAttempts && IsTransientUploadFailure(exception, cancellationToken))
            {
                events.Add(new CompanionAnalysisWorkflowEvent("retrying", "upload", attempt));
                await _delay(_options.UploadRetryDelay, cancellationToken);
            }
        }

        throw new CompanionAnalysisException("upload_failed", $"Companion analysis upload failed after {_options.MaxUploadAttempts} attempts.", true);
    }

    private async Task<CompanionAnalysisStatusDtoV1> PollUntilTerminalAsync(
        string jobId,
        List<CompanionAnalysisWorkflowEvent> events,
        CancellationToken cancellationToken)
    {
        var startedAt = _now();
        var delay = _options.InitialPollDelay;

        for (var attempt = 1; attempt <= _options.MaxPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_now() - startedAt > _options.TotalPollTimeout)
            {
                break;
            }

            events.Add(new CompanionAnalysisWorkflowEvent("started", "poll", attempt, jobId));
            var sessionToken = _sessionManager.GetRequiredSessionToken();
            CompanionAnalysisStatusDtoV1 status;

            try
            {
                status = await _apiClient.GetAnalysisStatusAsync(sessionToken, jobId, cancellationToken);
            }
            catch (CompanionApiException exception) when (exception.StatusCode == 401)
            {
                _sessionManager.ClearUnauthorized();
                throw new CompanionAnalysisException("session_unauthorized", "Companion session expired during analysis polling.", false, exception);
            }

            events.Add(new CompanionAnalysisWorkflowEvent("observed", "poll", attempt, jobId, status.State, status.UserAction));
            if (IsTerminal(status.State))
            {
                return status;
            }

            if (attempt == _options.MaxPollAttempts)
            {
                break;
            }

            await _delay(delay, cancellationToken);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxPollDelay.Ticks));
        }

        throw new CompanionAnalysisException("poll_timeout", "Companion analysis did not reach a terminal state before the polling limit.", true);
    }

    private static bool IsTransientUploadFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            CompanionApiException apiException when apiException.StatusCode is 408 or 429 || apiException.StatusCode >= 500 => true,
            HttpRequestException => true,
            TaskCanceledException => true,
            CompanionAnalysisException workflowException when workflowException.IsRecoverable => true,
            _ => false
        };
    }

    private static bool IsTerminal(string state) =>
        state is "completed" or "completed_delivery_failed" or "completed_delivery_unknown";
}
