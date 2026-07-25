using System;
using System.Drawing;
using System.Windows.Forms;
using LoLCompanion.Core.Api;
using LoLCompanion.Core.Pairing;

namespace LoLCompanion.App;

public sealed class MainForm : Form
{
    private readonly ICompanionSessionManager _sessionManager;
    private readonly CompanionPairingController _pairingController;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Label _pairingStatusValue;
    private readonly Label _sessionStatusValue;
    private readonly Label _expiresValue;
    private readonly Label _discordUserValue;
    private readonly Label _deviceValue;
    private readonly TextBox _pairCodeTextBox;
    private readonly TextBox _deviceNameTextBox;
    private readonly Button _pairButton;
    private readonly Label _pairResultLabel;
    private bool _pairingInProgress;
    private bool _disposed;

    public MainForm(
        ICompanionSessionManager sessionManager,
        CompanionPairingController pairingController)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _pairingController = pairingController ?? throw new ArgumentNullException(nameof(pairingController));

        Text = "LoL Companion";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        var title = new Label
        {
            AutoSize = true,
            Text = "LoL Companion",
            Font = new Font(Font.FontFamily, 20F, FontStyle.Bold),
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 0, 4)
        };

        var prompt = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = "先在 Discord 取得一次性配對碼，再輸入裝置名稱完成配對。",
            Padding = new Padding(0, 0, 0, 12)
        };

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

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 6,
            AutoSize = false
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        content.Controls.Add(title, 0, 0);
        content.Controls.Add(prompt, 0, 1);
        content.Controls.Add(pairingSectionTitle, 0, 2);
        content.Controls.Add(pairingGrid, 0, 3);
        content.Controls.Add(sessionSectionTitle, 0, 4);
        content.Controls.Add(sessionGrid, 0, 5);
        Controls.Add(content);

        RefreshSessionStatus();
        UpdatePairingUi(false, " ");
        FormClosing += OnFormClosing;
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
            return;
        }

        _pairingStatusValue.Text = "已配對";
        _sessionStatusValue.Text = "有效工作階段";
        _expiresValue.Text = session.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        _discordUserValue.Text = session.DiscordUserId;
        _deviceValue.Text = session.DeviceName;
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
    }

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
