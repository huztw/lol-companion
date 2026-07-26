using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using LoLCompanion.Core.Analysis;
using LoLCompanion.Core.Api;
using LoLCompanion.Core.Pairing;
using LoLCompanion.Core.Lcu;

namespace LoLCompanion.App;

public sealed class MainForm : Form
{
    private enum AnalysisStatusMode
    {
        Guidance,
        InProgress,
        Result
    }

    private readonly ICompanionSessionManager _sessionManager;
    private readonly CompanionPairingController _pairingController;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Label _pairingStatusValue;
    private readonly Label _sessionStatusValue;
    private readonly Label _expiresValue;
    private readonly Label _discordUserValue;
    private readonly Label _deviceValue;
    private readonly Label _leagueClientStatusValue;
    private readonly ListView _recentMatchesListView;
    private readonly Button _analyzeButton;
    private readonly Label _analysisStatusValue;
    private readonly TextBox _pairCodeTextBox;
    private readonly TextBox _deviceNameTextBox;
    private readonly Button _pairButton;
    private readonly Label _pairResultLabel;
    private readonly Func<CancellationToken, Task<IReadOnlyList<LcuRecentMatchSummary>>> _recentMatchesLoader;
    private readonly Func<LcuRecentMatchSummary, CancellationToken, Task<CompanionAnalysisWorkflowResult>> _analyzeSelectedMatch;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _pairingInProgress;
    private bool _refreshInProgress;
    private bool _analysisInProgress;
    private bool _suppressRecentMatchesSelectionUpdates;
    private AnalysisStatusMode _analysisStatusMode = AnalysisStatusMode.Guidance;
    private bool _disposed;

    public MainForm(
        ICompanionSessionManager sessionManager,
        CompanionPairingController pairingController,
        Func<CancellationToken, Task<IReadOnlyList<LcuRecentMatchSummary>>>? recentMatchesLoader = null,
        Func<LcuRecentMatchSummary, CancellationToken, Task<CompanionAnalysisWorkflowResult>>? analyzeSelectedMatch = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _pairingController = pairingController ?? throw new ArgumentNullException(nameof(pairingController));
        _recentMatchesLoader = recentMatchesLoader ?? (_ => Task.FromResult<IReadOnlyList<LcuRecentMatchSummary>>(Array.Empty<LcuRecentMatchSummary>()));
        _analyzeSelectedMatch = analyzeSelectedMatch ?? ThrowAnalysisNotConfigured;
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2500 };

        var applicationTitle = GetVersionedApplicationTitle();
        Text = applicationTitle;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 760);
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        var title = new Label
        {
            Name = "appTitleLabel",
            AutoSize = true,
            Text = applicationTitle,
            Font = new Font(Font.FontFamily, 20F, FontStyle.Bold),
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 0, 4)
        };

        var prompt = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "先在 Discord 取得一次性配對碼，再輸入裝置名稱完成配對。",
            Padding = new Padding(0, 0, 0, 10)
        };

        _leagueClientStatusValue = CreateValueLabel();
        _leagueClientStatusValue.Name = "leagueClientStatusValue";
        var leagueClientStatusGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1
        };
        leagueClientStatusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        leagueClientStatusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        AddRow(leagueClientStatusGrid, "League Client 狀態", _leagueClientStatusValue, 0);

        var pairingSectionTitle = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "配對",
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 8, 0, 6)
        };

        var pairingGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 4
        };
        pairingGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        pairingGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var row = 0; row < pairingGrid.RowCount; row++)
        {
            pairingGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _pairCodeTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            MaxLength = 64,
            AccessibleName = "配對碼輸入框",
            PlaceholderText = "輸入一次性配對碼"
        };
        _deviceNameTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            MaxLength = 40,
            AccessibleName = "裝置名稱輸入框",
            PlaceholderText = "例如：家用電腦"
        };
        _pairButton = new Button
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            Text = "配對",
            Padding = new Padding(16, 4, 16, 4)
        };
        _pairButton.Click += OnPairButtonClickAsync;
        _pairResultLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = " ",
            Padding = new Padding(0, 8, 0, 0)
        };

        pairingGrid.Controls.Add(CreateFieldLabel("配對碼"), 0, 0);
        pairingGrid.Controls.Add(_pairCodeTextBox, 1, 0);
        pairingGrid.Controls.Add(CreateFieldLabel("裝置名稱"), 0, 1);
        pairingGrid.Controls.Add(_deviceNameTextBox, 1, 1);
        pairingGrid.Controls.Add(_pairButton, 1, 2);
        pairingGrid.Controls.Add(_pairResultLabel, 1, 3);

        var sessionSectionTitle = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "工作階段",
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 8, 0, 6)
        };

        var sessionGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 5
        };
        sessionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        sessionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        _pairingStatusValue = CreateValueLabel();
        _sessionStatusValue = CreateValueLabel();
        _expiresValue = CreateValueLabel();
        _discordUserValue = CreateValueLabel();
        _deviceValue = CreateValueLabel();

        AddRow(sessionGrid, "配對狀態", _pairingStatusValue, 0);
        AddRow(sessionGrid, "Session 狀態", _sessionStatusValue, 1);
        AddRow(sessionGrid, "到期時間", _expiresValue, 2);
        AddRow(sessionGrid, "Discord 使用者", _discordUserValue, 3);
        AddRow(sessionGrid, "裝置名稱", _deviceValue, 4);

        var topPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 7
        };
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        topPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topPanel.Controls.Add(title, 0, 0);
        topPanel.Controls.Add(prompt, 0, 1);
        topPanel.Controls.Add(leagueClientStatusGrid, 0, 2);
        topPanel.Controls.Add(pairingSectionTitle, 0, 3);
        topPanel.Controls.Add(pairingGrid, 0, 4);
        topPanel.Controls.Add(sessionSectionTitle, 0, 5);
        topPanel.Controls.Add(sessionGrid, 0, 6);

        _recentMatchesListView = CreateRecentMatchesListView();
        _recentMatchesListView.SelectedIndexChanged += OnRecentMatchesSelectionChanged;
        var recentMatchesPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 3
        };
        recentMatchesPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        recentMatchesPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        recentMatchesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        recentMatchesPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        recentMatchesPanel.Controls.Add(new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "近期對戰",
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 8, 0, 6)
        }, 0, 0);
        var analysisPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1
        };
        analysisPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        analysisPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _analyzeButton = new Button
        {
            Name = "analyzeButton",
            AutoSize = true,
            Text = "分析選取對局",
            Padding = new Padding(16, 4, 16, 4)
        };
        _analyzeButton.Click += OnAnalyzeButtonClickAsync;
        _analysisStatusValue = CreateValueLabel();
        _analysisStatusValue.Name = "analysisStatusValue";
        analysisPanel.Controls.Add(_analyzeButton, 0, 0);
        analysisPanel.Controls.Add(_analysisStatusValue, 1, 0);
        recentMatchesPanel.Controls.Add(analysisPanel, 0, 1);
        recentMatchesPanel.Controls.Add(_recentMatchesListView, 0, 2);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = false
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        content.Controls.Add(topPanel, 0, 0);
        content.Controls.Add(recentMatchesPanel, 0, 1);
        Controls.Add(content);

        RefreshSessionStatus();
        UpdatePairingUi(false, " ");
        SetLeagueClientUnavailable("請先啟動 League Client，連線後將自動載入。");
        UpdateAnalysisUi();
        Load += OnLoad;
        FormClosing += OnFormClosing;
        _refreshTimer.Tick += OnRefreshTimerTick;
    }

    public async Task RefreshLeagueClientAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || IsDisposed || _refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        try
        {
            var matches = await _recentMatchesLoader(cancellationToken);
            if (_disposed || IsDisposed)
            {
                return;
            }

            RunOnUiThread(() => ApplyRecentMatches(matches));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LcuException exception) when (exception.IsRecoverable)
        {
            if (!_disposed && !IsDisposed)
            {
                RunOnUiThread(() =>
                {
                    if (IsLeagueClientUnavailableCategory(exception.Category))
                    {
                        SetLeagueClientUnavailable("請先啟動 League Client，連線後將自動載入。");
                        return;
                    }

                    SetLeagueClientRefreshIssue("近期對戰載入失敗，將自動重試。");
                });
            }
        }
        catch (Exception)
        {
            if (!_disposed && !IsDisposed)
            {
                RunOnUiThread(() => SetLeagueClientRefreshIssue("近期對戰載入失敗，將自動重試。"));
            }
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    internal void RefreshSessionStatus()
    {
        var session = _sessionManager.GetActiveSession();
        if (session is null)
        {
            _pairingStatusValue.Text = "尚未配對";
            _sessionStatusValue.Text = "沒有有效工作階段";
            _expiresValue.Text = "沒有有效工作階段";
            _discordUserValue.Text = "-";
            _deviceValue.Text = "-";
            UpdateAnalysisUi();
            return;
        }

        _pairingStatusValue.Text = "已配對";
        _sessionStatusValue.Text = "有效工作階段";
        _expiresValue.Text = session.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        _discordUserValue.Text = session.DiscordUserId;
        _deviceValue.Text = session.DeviceName;
        UpdateAnalysisUi();
    }

    private async void OnPairButtonClickAsync(object? sender, EventArgs e)
    {
        if (_pairingInProgress)
        {
            return;
        }

        _pairingInProgress = true;
        UpdatePairingUi(true, "正在配對…");

        try
        {
            var result = await _pairingController.PairAsync(
                _pairCodeTextBox.Text,
                _deviceNameTextBox.Text,
                _lifetimeCancellation.Token);

            if (_disposed || IsDisposed)
            {
                return;
            }

            _pairResultLabel.Text = result.Message;
            if (result.State == CompanionPairingState.Paired)
            {
                _pairCodeTextBox.Clear();
                RefreshSessionStatus();
            }
        }
        catch (OperationCanceledException) when (
            _lifetimeCancellation.IsCancellationRequested ||
            _disposed ||
            IsDisposed)
        {
        }
        finally
        {
            _pairingInProgress = false;
            if (!_disposed && !IsDisposed)
            {
                UpdatePairingUi(false, _pairResultLabel.Text);
            }
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _lifetimeCancellation.Cancel();
        _refreshTimer.Stop();
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            await RefreshLeagueClientAsync(_lifetimeCancellation.Token);
            if (!_disposed && !IsDisposed)
            {
                _refreshTimer.Start();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested || _disposed || IsDisposed)
        {
        }
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        try
        {
            await RefreshLeagueClientAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested || _disposed || IsDisposed)
        {
        }
    }

    private async void OnAnalyzeButtonClickAsync(object? sender, EventArgs e)
    {
        var selectedMatch = GetSelectedMatch();
        if (selectedMatch is null)
        {
            UpdateAnalysisStatus("請先選擇一場近期對戰。");
            return;
        }

        if (_sessionManager.GetActiveSession() is null)
        {
            UpdateAnalysisStatus("請先完成 Discord 配對。");
            return;
        }

        if (!selectedMatch.IsSupported)
        {
            UpdateAnalysisStatus("尚未支援分析。");
            return;
        }

        await AnalyzeSelectedMatchAsync(selectedMatch, _lifetimeCancellation.Token);
    }

    internal async Task AnalyzeSelectedMatchAsync(
        LcuRecentMatchSummary selectedMatch,
        CancellationToken cancellationToken = default)
    {
        if (_analysisInProgress)
        {
            return;
        }

        if (_sessionManager.GetActiveSession() is null)
        {
            UpdateAnalysisStatus("請先完成 Discord 配對。");
            return;
        }

        if (!selectedMatch.IsSupported)
        {
            UpdateAnalysisStatus("尚未支援分析。");
            return;
        }

        _analysisInProgress = true;
        UpdateAnalysisUi();
        UpdateAnalysisStatus("正在讀取對局並送出分析…", AnalysisStatusMode.InProgress);

        try
        {
            var result = await _analyzeSelectedMatch(selectedMatch, cancellationToken);
            if (!_disposed && !IsDisposed)
            {
                UpdateAnalysisStatus(FormatAnalysisResult(result), AnalysisStatusMode.Result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetimeCancellation.IsCancellationRequested || _disposed || IsDisposed)
        {
        }
        catch (CompanionApiException exception) when (exception.StatusCode == 401)
        {
            _sessionManager.ClearUnauthorized();
            RefreshSessionStatus();
            UpdateAnalysisStatus("Discord 工作階段已失效，請重新配對。", AnalysisStatusMode.Guidance);
        }
        catch (CompanionAnalysisException exception) when (exception.Category is "unsupported_queue")
        {
            UpdateAnalysisStatus("尚未支援分析。", AnalysisStatusMode.Result);
        }
        catch (CompanionAnalysisException exception) when (exception.Category is "analysis_schema_update_required")
        {
            UpdateAnalysisStatus($"需要更新 LoL Companion 才能使用 timeline-v2 分析。{exception.Message}", AnalysisStatusMode.Guidance);
        }
        catch (Exception)
        {
            UpdateAnalysisStatus("分析暫時失敗，請稍後再試。", AnalysisStatusMode.Result);
        }
        finally
        {
            _analysisInProgress = false;
            if (!_disposed && !IsDisposed)
            {
                UpdateAnalyzeButtonState();
            }
        }
    }

    private void ApplyRecentMatches(IReadOnlyList<LcuRecentMatchSummary> matches)
    {
        var displayedMatches = matches.Take(20).ToArray();
        var selectedGameId = GetSelectedMatch()?.GameId;
        if (HasSameDisplayedMatches(displayedMatches))
        {
            _leagueClientStatusValue.Text = displayedMatches.Length == 0
                ? "已連線，但目前沒有可顯示的近期對戰。"
                : $"已連線，已載入 {displayedMatches.Length} 場近期對戰。";

            if (selectedGameId.HasValue)
            {
                UpdateAnalyzeButtonState();
            }
            else
            {
                UpdateAnalysisUi();
            }

            return;
        }

        _suppressRecentMatchesSelectionUpdates = true;
        var restoredSelection = false;
        _recentMatchesListView.BeginUpdate();
        try
        {
            _recentMatchesListView.Items.Clear();
            foreach (var match in displayedMatches)
            {
                var item = new ListViewItem(match.Win ? "勝" : "敗")
                {
                    Tag = match
                };
                item.SubItems.Add(match.ChampionName ?? $"#{match.ChampionId}");
                item.SubItems.Add($"{match.QueueId} / {match.GameMode}");
                item.SubItems.Add($"{match.Kills}/{match.Deaths}/{match.Assists}");
                item.SubItems.Add(match.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(FormatDuration(match.Duration));
                item.SubItems.Add(match.IsSupported ? "可分析" : "尚未支援分析");
                _recentMatchesListView.Items.Add(item);
            }

            if (selectedGameId.HasValue)
            {
                var restoredItem = _recentMatchesListView.Items
                    .Cast<ListViewItem>()
                    .FirstOrDefault(item => item.Tag is LcuRecentMatchSummary match && match.GameId == selectedGameId.Value);

                if (restoredItem is not null)
                {
                    restoredItem.Selected = true;
                    restoredItem.Focused = true;
                    restoredItem.EnsureVisible();
                    restoredSelection = true;
                }
            }

            if (_recentMatchesListView.SelectedItems.Count == 0 && _recentMatchesListView.Items.Count > 0)
            {
                _recentMatchesListView.SelectedIndices.Clear();
            }
        }
        finally
        {
            _recentMatchesListView.EndUpdate();
            _suppressRecentMatchesSelectionUpdates = false;
        }

        _leagueClientStatusValue.Text = matches.Count == 0
            ? "已連線，但目前沒有可顯示的近期對戰。"
            : $"已連線，已載入 {displayedMatches.Length} 場近期對戰。";

        if (restoredSelection)
        {
            UpdateAnalyzeButtonState();
        }
        else
        {
            UpdateAnalysisUi();
        }
    }

    private void SetLeagueClientUnavailable(string message)
    {
        _leagueClientStatusValue.Text = message;
        _suppressRecentMatchesSelectionUpdates = true;
        _recentMatchesListView.BeginUpdate();
        try
        {
            _recentMatchesListView.SelectedIndices.Clear();
            _recentMatchesListView.Items.Clear();
        }
        finally
        {
            _recentMatchesListView.EndUpdate();
            _suppressRecentMatchesSelectionUpdates = false;
        }

        if (ShouldPreserveAnalysisStatusOnLeagueClientUnavailable())
        {
            UpdateAnalyzeButtonState();
            return;
        }

        UpdateAnalysisUi();
    }

    private void SetLeagueClientRefreshIssue(string message)
    {
        _leagueClientStatusValue.Text = message;
        UpdateAnalyzeButtonState();
    }

    private void OnRecentMatchesSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressRecentMatchesSelectionUpdates)
        {
            return;
        }

        UpdateAnalysisUi();
    }

    private void UpdateAnalysisUi()
    {
        UpdateAnalyzeButtonState();

        var selectedMatch = GetSelectedMatch();
        var hasSession = _sessionManager.GetActiveSession() is not null;
        var supported = selectedMatch?.IsSupported == true;
        _analysisStatusMode = AnalysisStatusMode.Guidance;

        if (!hasSession)
        {
            _analysisStatusValue.Text = "請先完成 Discord 配對。";
            return;
        }

        if (selectedMatch is null)
        {
            _analysisStatusValue.Text = "請先選擇一場近期對戰。";
            return;
        }

        _analysisStatusValue.Text = supported ? "可執行分析。" : "尚未支援分析。";
    }

    private void UpdateAnalyzeButtonState()
    {
        var selectedMatch = GetSelectedMatch();
        var hasSession = _sessionManager.GetActiveSession() is not null;
        var supported = selectedMatch?.IsSupported == true;

        _analyzeButton.Enabled = !_analysisInProgress && hasSession && supported;
    }

    private void UpdateAnalysisStatus(string message, AnalysisStatusMode statusMode = AnalysisStatusMode.Guidance)
    {
        _analysisStatusMode = statusMode;
        _analysisStatusValue.Text = message;
    }

    private LcuRecentMatchSummary? GetSelectedMatch()
    {
        return _recentMatchesListView.SelectedItems.Count == 0
            ? null
            : _recentMatchesListView.SelectedItems[0].Tag as LcuRecentMatchSummary;
    }

    private static string FormatAnalysisResult(CompanionAnalysisWorkflowResult result)
    {
        if (result.FinalStatus.State == "completed")
        {
            return "分析已完成並已傳送 Discord。";
        }

        if (result.FinalStatus.State is "completed_delivery_failed" or "completed_delivery_unknown")
        {
            return "分析已完成，但傳送未完成，請到 Discord 使用 `/report`。";
        }

        return "分析已送出，請稍後再查看結果。";
    }

    private Task<CompanionAnalysisWorkflowResult> ThrowAnalysisNotConfigured(LcuRecentMatchSummary _, CancellationToken __) =>
        throw new InvalidOperationException("Analysis workflow is not configured.");

    private bool ShouldPreserveAnalysisStatusOnLeagueClientUnavailable() =>
        _analysisStatusMode is AnalysisStatusMode.InProgress or AnalysisStatusMode.Result;

    private static bool IsLeagueClientUnavailableCategory(string category) =>
        category is "lockfile_unavailable" or "lockfile_invalid" or "lcu_connection_failed" or "lcu_auth_failed";

    private static string GetVersionedApplicationTitle()
    {
        const string baseTitle = "LoL Companion";
        var version = typeof(MainForm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
        {
            version = typeof(MainForm).Assembly.GetName().Version?.ToString();
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return baseTitle;
        }

        var metadataSeparator = version.IndexOf('+', StringComparison.Ordinal);
        var normalizedVersion = metadataSeparator >= 0
            ? version[..metadataSeparator]
            : version;

        return string.IsNullOrWhiteSpace(normalizedVersion)
            ? baseTitle
            : $"{baseTitle} v{normalizedVersion}";
    }

    private void RunOnUiThread(Action action)
    {
        if (_disposed || IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            if (IsHandleCreated)
            {
                Invoke(action);
                return;
            }
        }

        action();
    }

    private static ListView CreateRecentMatchesListView()
    {
        var listView = new ListView
        {
            Name = "recentMatchesListView",
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };

        listView.Columns.Add("勝敗", 60);
        listView.Columns.Add("英雄", 130);
        listView.Columns.Add("queue / mode", 150);
        listView.Columns.Add("KDA", 100);
        listView.Columns.Add("日期時間", 150);
        listView.Columns.Add("時長", 80);
        listView.Columns.Add("分析", 120);
        return listView;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }

    private bool HasSameDisplayedMatches(IReadOnlyList<LcuRecentMatchSummary> matches)
    {
        if (_recentMatchesListView.Items.Count != matches.Count)
        {
            return false;
        }

        for (var index = 0; index < matches.Count; index++)
        {
            if (_recentMatchesListView.Items[index].Tag is not LcuRecentMatchSummary existingMatch ||
                !RecentMatchesEqual(existingMatch, matches[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RecentMatchesEqual(LcuRecentMatchSummary left, LcuRecentMatchSummary right) =>
        left.GameId == right.GameId &&
        left.QueueId == right.QueueId &&
        string.Equals(left.GameMode, right.GameMode, StringComparison.Ordinal) &&
        string.Equals(left.GameType, right.GameType, StringComparison.Ordinal) &&
        left.CreatedAt == right.CreatedAt &&
        left.Duration == right.Duration &&
        left.Win == right.Win &&
        left.ChampionId == right.ChampionId &&
        string.Equals(left.ChampionName, right.ChampionName, StringComparison.Ordinal) &&
        left.Kills == right.Kills &&
        left.Deaths == right.Deaths &&
        left.Assists == right.Assists &&
        left.IsSupported == right.IsSupported &&
        string.Equals(left.UnsupportedReason, right.UnsupportedReason, StringComparison.Ordinal);

    private void UpdatePairingUi(bool inProgress, string statusText)
    {
        _pairButton.Enabled = !inProgress;
        _pairCodeTextBox.Enabled = !inProgress;
        _deviceNameTextBox.Enabled = !inProgress;
        _pairResultLabel.Text = statusText;
    }

    private static Label CreateFieldLabel(string text) =>
        new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = text,
            Padding = new Padding(0, 6, 12, 0),
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
        };

    private static Label CreateValueLabel() =>
        new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "-"
        };

    private static void AddRow(TableLayoutPanel grid, string labelText, Label valueLabel, int row)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = labelText,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
        };

        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(valueLabel, 1, row);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _lifetimeCancellation.Cancel();
            _lifetimeCancellation.Dispose();
        }

        base.Dispose(disposing);
    }
}
