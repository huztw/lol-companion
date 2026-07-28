using LoLCompanion.Core.Analysis;
using LoLCompanion.Core.Api;
using LoLCompanion.Core.Contracts;
using LoLCompanion.Core.Lcu;

namespace LoLCompanion.Core.RemoteControl;

public sealed record CompanionRemoteControlStatus(string State, string Message);

public sealed class CompanionRemoteControlCoordinator
{
    private readonly CompanionApiClient _api;
    private readonly ICompanionSessionManager _sessions;
    private readonly Func<CancellationToken, Task<IReadOnlyList<LcuRecentMatchSummary>>> _recentMatchesLoader;
    private readonly Func<LcuRecentMatchSummary, string, CancellationToken, Task<CompanionAnalysisSubmissionResult>> _submitAnalysis;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _unpairedInterval;
    private int _polling;

    public event Action<CompanionRemoteControlStatus>? StatusChanged;

    public CompanionRemoteControlCoordinator(
        CompanionApiClient api,
        ICompanionSessionManager sessions,
        Func<CancellationToken, Task<IReadOnlyList<LcuRecentMatchSummary>>> recentMatchesLoader,
        Func<LcuRecentMatchSummary, string, CancellationToken, Task<CompanionAnalysisSubmissionResult>> submitAnalysis,
        TimeSpan? pollInterval = null,
        TimeSpan? unpairedInterval = null)
    {
        _api = api;
        _sessions = sessions;
        _recentMatchesLoader = recentMatchesLoader;
        _submitAnalysis = submitAnalysis;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(CompanionRemoteControlContract.PollIntervalSeconds);
        _unpairedInterval = unpairedInterval ?? TimeSpan.FromSeconds(1);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_sessions.GetActiveSession() is null)
            {
                Publish("unpaired", "請先完成 Discord 配對。");
                await Task.Delay(_unpairedInterval, cancellationToken);
                continue;
            }

            await PollOnceAsync(cancellationToken);
            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0) return;

        CompanionControlJobDto? job = null;
        string? sessionToken = null;
        try
        {
            sessionToken = _sessions.GetRequiredSessionToken();
            Publish("waiting", "等待 Discord 指令。");
            job = await _api.GetNextControlJobAsync(sessionToken, cancellationToken);
            if (job is null) return;
            if (
                job.ProtocolVersion != CompanionRemoteControlContract.ProtocolVersion ||
                !Guid.TryParse(job.ControlJobId, out _)
            )
            {
                Publish("incompatible", "收到不相容的遠端指令，請更新 Companion。");
                return;
            }

            if (job.Type == "list_recent_matches")
            {
                Publish("listing", "正在取得近期對局。");
                var matches = (await _recentMatchesLoader(cancellationToken))
                    .Take(CompanionRemoteControlContract.MaxRecentMatches)
                    .Select(ToDto)
                    .ToArray();
                await _api.SubmitControlResultAsync(
                    sessionToken,
                    job.ControlJobId,
                    new CompanionControlResultDto("completed", Matches: matches),
                    cancellationToken);
                Publish("waiting_selection", "近期對局已送至 Discord，等待使用者選擇。");
                return;
            }

            var match = ToMatch(job);
            if (job.Type != "analyze_match" || match is null || !match.IsSupported)
            {
                await SubmitFailureAsync(sessionToken, job.ControlJobId, "unsupported_match", cancellationToken);
                Publish("failed", "這場對局無法分析，請回 Discord 重新選擇。");
                return;
            }

            Publish("analyzing", "正在讀取對局並送出分析。");
            var result = await _submitAnalysis(match, job.ControlJobId, cancellationToken);
            await _api.SubmitControlResultAsync(
                sessionToken,
                job.ControlJobId,
                new CompanionControlResultDto("completed", AnalysisJobId: result.JobId),
                cancellationToken);
            Publish("submitted", "分析已送出，完成後會傳送私人 Discord 報告。");
        }
        catch (CompanionApiException exception) when (exception.StatusCode == 401)
        {
            _sessions.ClearUnauthorized();
            Publish("unauthorized", "Discord 工作階段已失效，請重新配對。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (job is not null && sessionToken is not null && Guid.TryParse(job.ControlJobId, out _))
            {
                await TrySubmitFailureAsync(
                    sessionToken,
                    job.ControlJobId,
                    MapFailureCategory(exception),
                    cancellationToken);
            }
            Publish("failed", ToUserMessage(exception));
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private async Task SubmitFailureAsync(
        string sessionToken,
        string jobId,
        string category,
        CancellationToken cancellationToken) =>
        await _api.SubmitControlResultAsync(
            sessionToken,
            jobId,
            new CompanionControlResultDto("failed", category),
            cancellationToken);

    private async Task TrySubmitFailureAsync(
        string sessionToken,
        string jobId,
        string category,
        CancellationToken cancellationToken)
    {
        try
        {
            await SubmitFailureAsync(sessionToken, jobId, category, cancellationToken);
        }
        catch
        {
            // The Bot deadline remains the bounded fallback when failure reporting also fails.
        }
    }

    private void Publish(string state, string message) =>
        StatusChanged?.Invoke(new CompanionRemoteControlStatus(state, message));

    private static CompanionRecentMatchDto ToDto(LcuRecentMatchSummary match) =>
        new(
            match.GameId,
            match.QueueId,
            match.GameMode,
            match.GameType,
            match.CreatedAt,
            checked((int)Math.Clamp(match.Duration.TotalSeconds, 0, 86_400)),
            match.Win,
            match.ChampionId,
            match.ChampionName ?? $"#{match.ChampionId}",
            match.Kills,
            match.Deaths,
            match.Assists,
            match.IsSupported,
            match.UnsupportedReason);

    private static LcuRecentMatchSummary? ToMatch(CompanionControlJobDto job) =>
        job.GameId is long gameId &&
        job.QueueId is int queueId &&
        job.GameMode is not null &&
        job.GameType is not null &&
        job.CreatedAt is DateTimeOffset createdAt &&
        job.DurationSeconds is int durationSeconds &&
        job.Win is bool win &&
        job.ChampionId is int championId &&
        job.Kills is int kills &&
        job.Deaths is int deaths &&
        job.Assists is int assists &&
        job.IsSupported is bool supported
            ? new LcuRecentMatchSummary(
                gameId,
                queueId,
                job.GameMode,
                job.GameType,
                createdAt,
                TimeSpan.FromSeconds(durationSeconds),
                win,
                championId,
                job.ChampionName,
                kills,
                deaths,
                assists,
                supported,
                job.UnsupportedReason)
            : null;

    private static string MapFailureCategory(Exception exception) =>
        exception switch
        {
            LcuException
            {
                Category: "lockfile_unavailable" or "lockfile_invalid" or "lcu_connection_failed" or "lcu_auth_failed"
            } => "league_client_unavailable",
            LcuException => "recent_matches_unavailable",
            CompanionAnalysisException { Category: "unsupported_queue" } => "unsupported_match",
            CompanionAnalysisException { Category: "analysis_schema_update_required" } => "remote_control_incompatible",
            _ => "analysis_failed"
        };

    private static string ToUserMessage(Exception exception) =>
        MapFailureCategory(exception) switch
        {
            "league_client_unavailable" => "無法連接 League Client，請確認已開啟並登入。",
            "recent_matches_unavailable" => "暫時無法取得近期對局，請稍後重試。",
            "remote_control_incompatible" => "Companion 版本不相容，請更新後重試。",
            _ => "工作失敗，請回 Discord 重新操作。"
        };
}
