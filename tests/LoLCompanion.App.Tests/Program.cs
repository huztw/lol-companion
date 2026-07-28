using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using LoLCompanion.App;
using LoLCompanion.Core.Analysis;
using LoLCompanion.Core.Api;
using LoLCompanion.Core.Contracts;
using LoLCompanion.Core.Lcu;
using LoLCompanion.Core.Pairing;

ApplicationConfiguration.Initialize();

var defaultOptions = CompanionAppOptions.Load(_ => null);
Assert(
    defaultOptions.ApiBaseAddress.AbsoluteUri ==
    "https://aram-discord-api-891336206880.asia-east1.run.app/",
    "Expected default API base address.");

var blankOptions = CompanionAppOptions.Load(_ => "   ");
Assert(
    blankOptions.ApiBaseAddress.AbsoluteUri ==
    "https://aram-discord-api-891336206880.asia-east1.run.app/",
    "Expected blank env to use default.");

var trimmedOverride = CompanionAppOptions.Load(name =>
    name == CompanionAppOptions.ApiBaseUrlEnvironmentVariable
        ? "   https://example.test/api   "
        : null);
Assert(
    trimmedOverride.ApiBaseAddress.AbsoluteUri == "https://example.test/api/",
    "Expected trimmed override with trailing slash.");

var pathOverride = CompanionAppOptions.Load(name =>
    name == CompanionAppOptions.ApiBaseUrlEnvironmentVariable
        ? "https://example.test/api/v1"
        : null);
Assert(
    pathOverride.ApiBaseAddress.AbsoluteUri == "https://example.test/api/v1/",
    "Expected base path to be preserved and normalized.");

AssertInvalid("http://example.test/");
AssertInvalid("/relative");
AssertInvalid("https://user:pass@example.test/");
AssertInvalid("https://example.test/?q=1");
AssertInvalid("https://example.test/#frag");

const string secret = "apikey-123";
try
{
    CompanionAppOptions.Load(name =>
        name == CompanionAppOptions.ApiBaseUrlEnvironmentVariable
            ? $"https://user:{secret}@example.test/"
            : null);
    throw new InvalidOperationException("Expected invalid configuration.");
}
catch (InvalidOperationException exception)
{
    Assert(
        !exception.Message.Contains(secret, StringComparison.Ordinal),
        "Expected error message to avoid echoing secret input.");
}

TestVersionDisplay();
TestStatusOnlyRemoteControlUi();

Console.WriteLine("LoL Companion app options tests passed.");

static void TestStatusOnlyRemoteControlUi()
{
    using var form = CreateMainForm(
        new FakeSessionManager(new CompanionSessionSnapshot(
            DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
            "Arena Laptop",
            "discord-user-1")),
        _ => Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>([]));

    Assert(
        form.Controls.Find("recentMatchesListView", true).Length == 0,
        "Expected local recent-match selection list to be absent.");
    Assert(
        form.Controls.Find("analyzeButton", true).Length == 0,
        "Expected local analyze button to be absent.");
    Assert(
        FindControl<Label>(form, "analysisStatusValue").Text.Contains("等待 Discord", StringComparison.Ordinal),
        "Expected Discord remote-control status guidance.");
    Assert(
        FindControl<Label>(form, "leagueClientStatusValue").Text.Contains("Discord", StringComparison.Ordinal),
        "Expected on-demand League Client guidance.");
}

static void AssertInvalid(string value)
{
    try
    {
        CompanionAppOptions.Load(name =>
            name == CompanionAppOptions.ApiBaseUrlEnvironmentVariable ? value : null);
        throw new InvalidOperationException("Expected invalid configuration.");
    }
    catch (InvalidOperationException exception)
    {
        Assert(
            !exception.Message.Contains(value, StringComparison.Ordinal),
            "Expected error message to avoid echoing input.");
    }
}

static void TestVersionDisplay()
{
    using var form = CreateLoadedMainForm(_ => Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>(Array.Empty<LcuRecentMatchSummary>()));
    var title = FindControl<Label>(form, "appTitleLabel");
    var informationalVersion = typeof(MainForm).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion;
    var expectedVersion = string.IsNullOrWhiteSpace(informationalVersion)
        ? typeof(MainForm).Assembly.GetName().Version?.ToString() ?? string.Empty
        : informationalVersion;
    var metadataSeparator = expectedVersion.IndexOf('+', StringComparison.Ordinal);
    if (metadataSeparator >= 0)
    {
        expectedVersion = expectedVersion[..metadataSeparator];
    }

    var expectedTitle = $"LoL Companion v{expectedVersion}";
    Assert(form.Text == expectedTitle, "Expected form title to include normalized app version.");
    Assert(title.Text == expectedTitle, "Expected title label to include normalized app version.");
}

static async Task TestRecentMatchesUiAsync()
{
    using var form = CreateLoadedMainForm(async _ => await Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>(
    [
        new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null),
        new LcuRecentMatchSummary(431945472, 400, "CLASSIC", "MATCHED", DateTimeOffset.Parse("2026-07-24T20:35:52.919Z"), TimeSpan.FromMinutes(31), false, 22, "Ashe", 3, 8, 4, false, "analysis_not_supported_for_queue")
    ]));

    await form.RefreshLeagueClientAsync();

    var listView = FindControl<ListView>(form, "recentMatchesListView");
    Assert(listView.Items.Count == 2, "Expected two recent matches.");
    Assert(listView.Items[0].SubItems[0].Text == "勝", "Expected first match win label.");
    Assert(listView.Items[0].SubItems[6].Text == "可分析", "Expected supported queue label.");
    Assert(listView.Items[1].SubItems[6].Text == "尚未支援分析", "Expected unsupported queue label.");

    var status = FindControl<Label>(form, "leagueClientStatusValue");
    Assert(status.Text.Contains("已連線", StringComparison.Ordinal), "Expected successful load status.");
}

static async Task TestRecentMatchesRecoveryAndCancellationAsync()
{
    var attempts = 0;
    using var form = CreateLoadedMainForm(_ =>
    {
        attempts++;
        if (attempts == 1)
        {
            throw new LcuException("lockfile_unavailable", "LCU lockfile not found.", true);
        }

        return Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>(
        [
            new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null)
        ]);
    });

    await form.RefreshLeagueClientAsync();
    Assert(
        FindControl<Label>(form, "leagueClientStatusValue").Text == "請先啟動 League Client，連線後將自動載入。",
        "Expected client unavailable recoverable category to use the startup guidance message.");

    await form.RefreshLeagueClientAsync();
    Assert(FindControl<ListView>(form, "recentMatchesListView").Items.Count == 1, "Expected recovery after League Client restart.");

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(() => form.RefreshLeagueClientAsync(cancellation.Token), "Expected cancellation to flow.");
}

static async Task TestTransientRecoverableErrorPreservesUiStateAsync()
{
    var match = new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null);
    var sessionManager = new FakeSessionManager(new CompanionSessionSnapshot(
        DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
        "Arena Laptop",
        "discord-user-1"));
    var attempts = 0;
    using var form = CreateMainForm(
        sessionManager,
        _ =>
        {
            attempts++;
            if (attempts == 1)
            {
                return Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>([match]);
            }

            throw new LcuException("lcu_timeout", "Timed out.", true);
        },
        (_, _) => Task.FromResult(new CompanionAnalysisWorkflowResult(
            "request-1",
            "job-1",
            false,
            new CompanionAnalysisStatusDtoV1(1, "job-1", "completed", "2026-07-25T10:00:00Z", "2026-07-25T10:01:00Z", true, "delivered", "none"),
            [])));

    await form.RefreshLeagueClientAsync();
    var listView = FindControl<ListView>(form, "recentMatchesListView");
    var originalItem = listView.Items[0];
    SelectRecentMatch(form, 431945471);
    SetTerminalAnalysisStatus(form, "分析已完成並已傳送 Discord。");

    await form.RefreshLeagueClientAsync();

    Assert(
        FindControl<Label>(form, "leagueClientStatusValue").Text == "近期對戰載入失敗，將自動重試。",
        "Expected transient recoverable category to use the retry guidance message.");
    Assert(ReferenceEquals(originalItem, listView.Items[0]), "Expected transient recoverable error to keep the existing ListView items.");
    Assert(listView.SelectedItems.Count == 1, "Expected transient recoverable error to preserve selection.");
    Assert(((LcuRecentMatchSummary)listView.SelectedItems[0].Tag!).GameId == 431945471, "Expected the same match to stay selected after transient recoverable error.");
    Assert(FindControl<Button>(form, "analyzeButton").Enabled, "Expected analysis button to remain enabled after transient recoverable error.");
    Assert(
        FindControl<Label>(form, "analysisStatusValue").Text == "分析已完成並已傳送 Discord。",
        "Expected transient recoverable error not to overwrite terminal analysis status.");
}

static async Task TestClientUnavailableClearsListAndKeepsTerminalStatusAsync()
{
    var match = new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null);
    var sessionManager = new FakeSessionManager(new CompanionSessionSnapshot(
        DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
        "Arena Laptop",
        "discord-user-1"));
    var attempts = 0;
    using var form = CreateMainForm(
        sessionManager,
        _ =>
        {
            attempts++;
            if (attempts == 1)
            {
                return Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>([match]);
            }

            throw new LcuException("lockfile_unavailable", "LCU lockfile not found.", true);
        },
        (_, _) => Task.FromResult(new CompanionAnalysisWorkflowResult(
            "request-1",
            "job-1",
            false,
            new CompanionAnalysisStatusDtoV1(1, "job-1", "completed", "2026-07-25T10:00:00Z", "2026-07-25T10:01:00Z", true, "delivered", "none"),
            [])));

    await form.RefreshLeagueClientAsync();
    var listView = FindControl<ListView>(form, "recentMatchesListView");
    SelectRecentMatch(form, 431945471);
    SetTerminalAnalysisStatus(form, "分析已完成並已傳送 Discord。");

    await form.RefreshLeagueClientAsync();

    Assert(
        FindControl<Label>(form, "leagueClientStatusValue").Text == "請先啟動 League Client，連線後將自動載入。",
        "Expected client unavailable error to keep the startup guidance message.");
    Assert(listView.Items.Count == 0, "Expected client unavailable error to clear the current recent matches.");
    Assert(listView.SelectedItems.Count == 0, "Expected client unavailable error to clear selection.");
    Assert(!FindControl<Button>(form, "analyzeButton").Enabled, "Expected analysis button to disable when recent matches are cleared.");
    Assert(
        FindControl<Label>(form, "analysisStatusValue").Text == "分析已完成並已傳送 Discord。",
        "Expected client unavailable error not to overwrite terminal analysis status.");
}

static async Task TestRecentMatchesNoOpRefreshAsync()
{
    var sameMatch = new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null);
    var changedMatch = new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 9, 2, 10, true, null);
    var sessionManager = new FakeSessionManager(new CompanionSessionSnapshot(
        DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
        "Arena Laptop",
        "discord-user-1"));

    var loads = new Queue<IReadOnlyList<LcuRecentMatchSummary>>([
        [sameMatch],
        [sameMatch],
        [changedMatch]
    ]);

    using var form = CreateMainForm(
        sessionManager,
        _ => Task.FromResult(loads.Count == 0 ? Array.Empty<LcuRecentMatchSummary>() : loads.Dequeue()),
        (_, _) => Task.FromResult(new CompanionAnalysisWorkflowResult(
            "request-1",
            "job-1",
            false,
            new CompanionAnalysisStatusDtoV1(1, "job-1", "completed", "2026-07-25T10:00:00Z", "2026-07-25T10:01:00Z", true, "delivered", "none"),
            [])));

    await form.RefreshLeagueClientAsync();
    var listView = FindControl<ListView>(form, "recentMatchesListView");
    var originalItem = listView.Items[0];
    SelectRecentMatch(form, 431945471);
    SetTerminalAnalysisStatus(form, "分析已完成並已傳送 Discord。");

    await form.RefreshLeagueClientAsync();
    Assert(ReferenceEquals(originalItem, listView.Items[0]), "Expected no-op refresh to preserve the same ListViewItem instance.");
    Assert(FindControl<Button>(form, "analyzeButton").Enabled, "Expected analyze button to remain enabled after no-op refresh.");
    Assert(FindControl<Label>(form, "analysisStatusValue").Text == "分析已完成並已傳送 Discord。", "Expected terminal analysis status to survive no-op refresh.");

    await form.RefreshLeagueClientAsync();
    Assert(!ReferenceEquals(originalItem, listView.Items[0]), "Expected changed match data to rebuild the ListView item.");
    Assert(listView.Items[0].SubItems[3].Text == "9/2/10", "Expected rebuilt item to reflect updated KDA.");
}

static async Task TestRecentMatchesSelectionPersistenceAsync()
{
    var sessionManager = new FakeSessionManager(new CompanionSessionSnapshot(
        DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
        "Arena Laptop",
        "discord-user-1"));

    var loads = new Queue<IReadOnlyList<LcuRecentMatchSummary>>([
        [
            new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null),
            new LcuRecentMatchSummary(431945472, 400, "CLASSIC", "MATCHED", DateTimeOffset.Parse("2026-07-24T20:35:52.919Z"), TimeSpan.FromMinutes(31), false, 22, "Ashe", 3, 8, 4, false, "analysis_not_supported_for_queue")
        ],
        [
            new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null)
        ],
        [
            new LcuRecentMatchSummary(431945472, 400, "CLASSIC", "MATCHED", DateTimeOffset.Parse("2026-07-24T20:35:52.919Z"), TimeSpan.FromMinutes(31), false, 22, "Ashe", 3, 8, 4, false, "analysis_not_supported_for_queue")
        ]
    ]);

    using var form = CreateMainForm(
        sessionManager,
        _ => Task.FromResult(loads.Count == 0 ? Array.Empty<LcuRecentMatchSummary>() : loads.Dequeue()),
        (_, _) => Task.FromResult(new CompanionAnalysisWorkflowResult(
            "request-1",
            "job-1",
            false,
            new CompanionAnalysisStatusDtoV1(1, "job-1", "completed", "2026-07-25T10:00:00Z", "2026-07-25T10:01:00Z", true, "delivered", "none"),
            [])));

    await form.RefreshLeagueClientAsync();

    SelectRecentMatch(form, 431945471);
    Assert(FindControl<Button>(form, "analyzeButton").Enabled, "Expected supported selected match to enable analysis.");
    Assert(FindControl<Label>(form, "analysisStatusValue").Text == "可執行分析。", "Expected supported selected match status.");
    SetTerminalAnalysisStatus(form, "分析已完成並已傳送 Discord。");

    await form.RefreshLeagueClientAsync();
    var listView = FindControl<ListView>(form, "recentMatchesListView");
    Assert(listView.SelectedItems.Count == 1, "Expected selection to persist across refresh when the same match remains.");
    Assert(((LcuRecentMatchSummary)listView.SelectedItems[0].Tag!).GameId == 431945471, "Expected the same game to remain selected.");
    Assert(FindControl<Button>(form, "analyzeButton").Enabled, "Expected analysis button to stay enabled after refresh.");
    Assert(
        FindControl<Label>(form, "analysisStatusValue").Text == "分析已完成並已傳送 Discord。",
        "Expected terminal analysis status to persist when the selected match remains.");

    await form.RefreshLeagueClientAsync();
    Assert(listView.SelectedItems.Count == 0, "Expected selection to clear when the selected match disappears.");
    Assert(!FindControl<Button>(form, "analyzeButton").Enabled, "Expected analysis button to disable when selection disappears.");
    Assert(FindControl<Label>(form, "analysisStatusValue").Text == "請先選擇一場近期對戰。", "Expected selection cleared status.");
}

static async Task TestAnalysisRequiresPairingAsync()
{
    var sessionManager = new FakeSessionManager(null);
    using var form = CreateMainForm(
        sessionManager,
        _ => Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>(
        [
            new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null)
        ]),
        (_, _) => Task.FromResult(new CompanionAnalysisWorkflowResult(
            "request-1",
            "job-1",
            false,
            new CompanionAnalysisStatusDtoV1(1, "job-1", "completed", "2026-07-25T10:00:00Z", "2026-07-25T10:01:00Z", true, "delivered", "none"),
            [])));

    await InvokeAnalyzeSelectedMatchAsync(form, new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null));
    Assert(FindControl<Label>(form, "analysisStatusValue").Text == "請先完成 Discord 配對。", "Expected unpaired analysis status.");
}

static async Task TestAnalysisFlowAsync()
{
    var sessionManager = new FakeSessionManager(new CompanionSessionSnapshot(
        DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
        "Arena Laptop",
        "discord-user-1"));
    var invocations = 0;
    using var form = CreateMainForm(
        sessionManager,
        _ => Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>(
        [
            new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null),
            new LcuRecentMatchSummary(431945472, 400, "CLASSIC", "MATCHED", DateTimeOffset.Parse("2026-07-24T20:35:52.919Z"), TimeSpan.FromMinutes(31), false, 22, "Ashe", 3, 8, 4, false, "analysis_not_supported_for_queue")
        ]),
        (match, _) =>
        {
            invocations++;
            return Task.FromResult(new CompanionAnalysisWorkflowResult(
                "request-1",
                $"job-{match.GameId}",
                false,
                new CompanionAnalysisStatusDtoV1(1, $"job-{match.GameId}", "completed", "2026-07-25T10:00:00Z", "2026-07-25T10:01:00Z", true, "delivered", "none"),
                []));
        });

    var supportedMatch = new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null);
    var unsupportedMatch = new LcuRecentMatchSummary(431945472, 400, "CLASSIC", "MATCHED", DateTimeOffset.Parse("2026-07-24T20:35:52.919Z"), TimeSpan.FromMinutes(31), false, 22, "Ashe", 3, 8, 4, false, "analysis_not_supported_for_queue");

    await InvokeAnalyzeSelectedMatchAsync(form, unsupportedMatch);
    Assert(FindControl<Label>(form, "analysisStatusValue").Text == "尚未支援分析。", "Expected unsupported queue status.");

    await InvokeAnalyzeSelectedMatchAsync(form, supportedMatch);
    Assert(invocations == 1, "Expected a single workflow invocation.");

    Assert(FindControl<Label>(form, "analysisStatusValue").Text == "分析已完成並已傳送 Discord。", "Expected completed delivery status.");

    sessionManager.Clear();
    InvokeRefreshSessionStatus(form);
    Assert(!FindControl<Button>(form, "analyzeButton").Enabled, "Expected analysis button to disable after session expiry.");
    Assert(FindControl<Label>(form, "analysisStatusValue").Text == "請先完成 Discord 配對。", "Expected expired session status.");
}

static async Task TestAnalysisDeliveryStateAsync()
{
    var sessionManager = new FakeSessionManager(new CompanionSessionSnapshot(
        DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
        "Arena Laptop",
        "discord-user-1"));
    using var form = CreateMainForm(
        sessionManager,
        _ => Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>(
        [
            new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null)
        ]),
        (_, _) => Task.FromResult(new CompanionAnalysisWorkflowResult(
            "request-1",
            "job-1",
            false,
            new CompanionAnalysisStatusDtoV1(1, "job-1", "completed_delivery_failed", "2026-07-25T10:00:00Z", "2026-07-25T10:01:00Z", true, "failed", "check_discord_report"),
            [])));

    var match = new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null);
    await InvokeAnalyzeSelectedMatchAsync(form, match);

    Assert(
        FindControl<Label>(form, "analysisStatusValue").Text == "分析已完成，但傳送未完成，請到 Discord 使用 `/report`。",
        "Expected delivery failure fallback status.");
}

static async Task TestAnalysisSchemaUpdateGuidanceAsync()
{
    var sessionManager = new FakeSessionManager(new CompanionSessionSnapshot(
        DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
        "Arena Laptop",
        "discord-user-1"));
    var match = new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null);
    using var form = CreateMainForm(
        sessionManager,
        _ => Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>([match]),
        (_, _) => Task.FromException<CompanionAnalysisWorkflowResult>(new CompanionAnalysisException(
            "analysis_schema_update_required",
            "A compatible LoL Companion version is required. Download: https://example.test/download",
            false)));

    await InvokeAnalyzeSelectedMatchAsync(form, match);
    Assert(
        FindControl<Label>(form, "analysisStatusValue").Text.Contains("https://example.test/download", StringComparison.Ordinal),
        "Expected schema update guidance to preserve the download URL.");
}

static async Task TestAnalysisCancellationAndDuplicatePreventionAsync()
{
    var sessionManager = new FakeSessionManager(new CompanionSessionSnapshot(
        DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
        "Arena Laptop",
        "discord-user-1"));
    var started = new TaskCompletionSource();
    var release = new TaskCompletionSource();
    var invocations = 0;
    using var form = CreateMainForm(
        sessionManager,
        _ => Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>(
        [
            new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null)
        ]),
        async (_, cancellationToken) =>
        {
            invocations++;
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new CompanionAnalysisWorkflowResult(
                "request-1",
                "job-1",
                false,
                new CompanionAnalysisStatusDtoV1(1, "job-1", "completed", "2026-07-25T10:00:00Z", "2026-07-25T10:01:00Z", true, "delivered", "none"),
                []);
        });

    var match = new LcuRecentMatchSummary(431945471, 450, "ARAM", "MATCHED", DateTimeOffset.Parse("2026-07-25T20:35:52.919Z"), TimeSpan.FromMinutes(23), true, 1, "Annie", 8, 2, 10, true, null);
    var analysisTask = InvokeAnalyzeSelectedMatchAsync(form, match, CancellationToken.None);
    await started.Task;
    Assert(started.Task.IsCompleted, "Expected analysis workflow to start.");
    var duplicateTask = InvokeAnalyzeSelectedMatchAsync(form, match, CancellationToken.None);
    Assert(duplicateTask.IsCompleted, "Expected duplicate invocation to return immediately while analysis is in progress.");
    Assert(invocations == 1, "Expected no duplicate workflow invocation while analysis is in progress.");
    release.TrySetResult();
    await analysisTask;
}

static MainForm CreateMainForm(
    ICompanionSessionManager sessionManager,
    Func<CancellationToken, Task<IReadOnlyList<LcuRecentMatchSummary>>> loader,
    Func<LcuRecentMatchSummary, CancellationToken, Task<CompanionAnalysisWorkflowResult>>? analyze = null)
{
    var apiClient = new CompanionApiClient(new HttpClient(new NoopHandler())
    {
        BaseAddress = new Uri("https://example.test/")
    });
    var pairingController = new CompanionPairingController(apiClient, sessionManager);
    var form = new MainForm(sessionManager, pairingController, loader, analyze);
    form.CreateControl();
    return form;
}

static MainForm CreateLoadedMainForm(Func<CancellationToken, Task<IReadOnlyList<LcuRecentMatchSummary>>> loader) =>
    CreateMainForm(
        new FakeSessionManager(new CompanionSessionSnapshot(
            DateTimeOffset.Parse("2026-07-25T10:00:00Z"),
            "Arena Laptop",
            "discord-user-1")),
        loader,
        (_, _) => Task.FromResult(new CompanionAnalysisWorkflowResult(
            "request-1",
            "job-1",
            false,
            new CompanionAnalysisStatusDtoV1(1, "job-1", "completed", "2026-07-25T10:00:00Z", "2026-07-25T10:01:00Z", true, "delivered", "none"),
            [])));

static T FindControl<T>(Control root, string name) where T : Control
{
    var control = root.Controls.Find(name, true).OfType<T>().FirstOrDefault();
    if (control is null)
    {
        throw new InvalidOperationException($"Expected to find {typeof(T).Name} named {name}.");
    }

    return control;
}

static void InvokeRefreshSessionStatus(MainForm form)
{
    var method = typeof(MainForm).GetMethod(
        "RefreshSessionStatus",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    if (method is null)
    {
        throw new InvalidOperationException("Expected RefreshSessionStatus method.");
    }

    method.Invoke(form, []);
}

static void SelectRecentMatch(MainForm form, long gameId)
{
    var listView = FindControl<ListView>(form, "recentMatchesListView");
    _ = listView.Handle;
    var item = listView.Items.Cast<ListViewItem>().FirstOrDefault(viewItem => viewItem.Tag is LcuRecentMatchSummary match && match.GameId == gameId);
    if (item is null)
    {
        throw new InvalidOperationException($"Expected to find match {gameId}.");
    }

    listView.SelectedIndices.Clear();
    item.Selected = true;
    item.Focused = true;
}

static Task InvokeAnalyzeSelectedMatchAsync(MainForm form, LcuRecentMatchSummary match, CancellationToken cancellationToken = default)
{
    var method = typeof(MainForm).GetMethod(
        "AnalyzeSelectedMatchAsync",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    if (method is null)
    {
        throw new InvalidOperationException("Expected AnalyzeSelectedMatchAsync method.");
    }

    return (Task)method.Invoke(form, [match, cancellationToken])!;
}

static void SetTerminalAnalysisStatus(MainForm form, string message)
{
    var method = typeof(MainForm).GetMethod(
        "UpdateAnalysisStatus",
        BindingFlags.Instance | BindingFlags.NonPublic);

    if (method is null)
    {
        throw new InvalidOperationException("Expected UpdateAnalysisStatus method.");
    }

    var statusModeParameter = method.GetParameters()[1].ParameterType;
    var resultMode = Enum.Parse(statusModeParameter, "Result");
    method.Invoke(form, [message, resultMode]);
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
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

sealed class NoopHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });
}

sealed class FakeSessionManager : ICompanionSessionManager
{
    private CompanionSessionSnapshot? _session;

    public FakeSessionManager(CompanionSessionSnapshot? session)
    {
        _session = session;
    }

    public CompanionSessionSnapshot? GetActiveSession() => _session;

    public Task<CompanionSessionSnapshot> RedeemAsync(CompanionApiClient apiClient, PairRedeemRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(_session ?? throw new InvalidOperationException("No session configured."));

    public Task RevokeAsync(CompanionApiClient apiClient, CancellationToken cancellationToken = default)
    {
        _session = null;
        return Task.CompletedTask;
    }

    public void Clear() => _session = null;

    public void ClearIfExpired() { }

    public void ClearUnauthorized() => _session = null;

    public string GetRequiredSessionToken() => "token";
}
