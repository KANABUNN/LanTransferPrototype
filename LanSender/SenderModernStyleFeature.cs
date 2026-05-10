using System.Drawing;

namespace LanSender;

public sealed partial class SenderForm
{
    private const bool DefaultAutoStartServer = true;
    private const bool DefaultMinimizeToTrayAfterAutoStart = true;

    private static readonly Color ModernBackground = Color.FromArgb(8, 12, 18);
    private static readonly Color ModernPanel = Color.FromArgb(16, 22, 31);
    private static readonly Color ModernPanelAlt = Color.FromArgb(23, 31, 43);
    private static readonly Color ModernBorder = Color.FromArgb(52, 64, 84);
    private static readonly Color ModernText = Color.FromArgb(235, 240, 246);
    private static readonly Color ModernSubText = Color.FromArgb(172, 181, 194);
    private static readonly Color ModernAccent = Color.FromArgb(34, 48, 68);
    private static readonly Color ModernAccentHover = Color.FromArgb(48, 66, 92);
    private static readonly Color ModernDanger = Color.FromArgb(92, 40, 48);

    private readonly NotifyIcon _senderTrayIcon = new();
    private readonly ContextMenuStrip _senderTrayMenu = new();
    private SenderQuickActionForm? _senderQuickActionForm;
    private bool _modernFeatureInitialized;
    private bool _allowActualClose;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_modernFeatureInitialized)
        {
            return;
        }

        _modernFeatureInitialized = true;
        Text = "LAN Transfer Sender";

        ApplyModernThemeTree(this);
        RewriteModernLabels();
        InitializeSenderTrayFeature();
        InitializeSenderQuickActions();

        if (DefaultAutoStartServer && _serverCts is null)
        {
            StartServer();
        }

        if (DefaultMinimizeToTrayAfterAutoStart)
        {
            BeginInvoke(new Action(() => MinimizeSenderToTray(showBalloon: false)));
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (_modernFeatureInitialized && WindowState == FormWindowState.Minimized)
        {
            MinimizeSenderToTray(showBalloon: false);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowActualClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            MinimizeSenderToTray(showBalloon: true);
            return;
        }

        _senderQuickActionForm?.Close();
        _senderQuickActionForm?.Dispose();
        _senderTrayIcon.Visible = false;
        _senderTrayIcon.Dispose();
        _senderTrayMenu.Dispose();

        base.OnFormClosing(e);
    }

    private void InitializeSenderTrayFeature()
    {
        if (_senderTrayIcon.Visible)
        {
            return;
        }

        _senderTrayMenu.BackColor = ModernPanel;
        _senderTrayMenu.ForeColor = ModernText;
        _senderTrayMenu.RenderMode = ToolStripRenderMode.System;

        var showItem = new ToolStripMenuItem("Show main window");
        showItem.Click += (_, _) => RestoreSenderWindow();

        var hideItem = new ToolStripMenuItem("Hide to tray");
        hideItem.Click += (_, _) => MinimizeSenderToTray(showBalloon: false);

        var startItem = new ToolStripMenuItem("Start server");
        startItem.Click += (_, _) => StartServer();

        var stopItem = new ToolStripMenuItem("Stop server");
        stopItem.Click += (_, _) => StopServer();

        var shareItem = new ToolStripMenuItem("Share screen");
        shareItem.Click += async (_, _) => await QuickToggleScreenShareAsync();

        var openBrowserItem = new ToolStripMenuItem("Open browser");
        openBrowserItem.Click += async (_, _) => await QuickOpenBrowserAsync();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            _allowActualClose = true;
            Close();
        };

        _senderTrayMenu.Items.AddRange(new ToolStripItem[]
        {
            showItem,
            hideItem,
            new ToolStripSeparator(),
            startItem,
            stopItem,
            new ToolStripSeparator(),
            shareItem,
            openBrowserItem,
            new ToolStripSeparator(),
            exitItem,
        });

        _senderTrayIcon.Text = "LAN Transfer Sender";
        _senderTrayIcon.Icon = TryGetApplicationIcon();
        _senderTrayIcon.ContextMenuStrip = _senderTrayMenu;
        _senderTrayIcon.Visible = true;
        _senderTrayIcon.DoubleClick += (_, _) => RestoreSenderWindow();
    }

    private void InitializeSenderQuickActions()
    {
        _senderQuickActionForm = new SenderQuickActionForm(
            QuickToggleScreenShareAsync,
            QuickOpenBrowserAsync,
            () => _isStreamingScreen);
        _senderQuickActionForm.Show();
    }

    private async Task QuickToggleScreenShareAsync()
    {
        if (_isStreamingScreen)
        {
            StopScreenStream();
            return;
        }

        if (_serverCts is null)
        {
            StartServer();
        }

        await StartScreenStreamAsync(selectedOnly: false);
    }

    private async Task QuickOpenBrowserAsync()
    {
        if (_serverCts is null)
        {
            StartServer();
        }

        if (!string.IsNullOrWhiteSpace(_manualOpenUrlBox.Text))
        {
            await SendManualUrlToAllAsync();
            return;
        }

        RefreshOpenTargetList();

        WindowTargetCandidate? browserCandidate = null;
        foreach (object? item in _openTargetList.Items)
        {
            if (item is not WindowTargetCandidate candidate)
            {
                continue;
            }

            string processName = candidate.ProcessName.ToLowerInvariant();
            if (processName is "chrome" or "msedge" or "firefox" or "brave" or "vivaldi" or "opera" or "iexplore")
            {
                browserCandidate = candidate;
                break;
            }
        }

        if (browserCandidate is null)
        {
            AddLog("Browser window was not found. Enter a URL in Manual URL open, or open a browser first.");
            RestoreSenderWindow();
            return;
        }

        _openTargetList.SelectedItem = browserCandidate;
        await SendOpenTargetToAllAsync();
    }

    private void RestoreSenderWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _senderQuickActionForm?.Show();
        _senderQuickActionForm?.MoveToBottomRight();
    }

    private void MinimizeSenderToTray(bool showBalloon)
    {
        WindowState = FormWindowState.Minimized;
        ShowInTaskbar = false;
        Hide();
        _senderQuickActionForm?.Show();
        _senderQuickActionForm?.MoveToBottomRight();

        if (showBalloon && _senderTrayIcon.Visible)
        {
            _senderTrayIcon.ShowBalloonTip(
                1600,
                "LAN Transfer Sender",
                "Sender is still running in the task tray.",
                ToolTipIcon.Info);
        }
    }

    private void RewriteModernLabels()
    {
        _startButton.Text = "起動";
        _stopButton.Text = "停止";
        _sendAllButton.Text = "全員へ送信";
        _sendSelectedButton.Text = "選択先へ送信";
        _sendScreenOnceAllButton.Text = "1フレーム送信";
        _sendScreenOnceSelectedButton.Text = "1フレーム選択先";
        _startScreenAllButton.Text = "画面を共有する";
        _startScreenSelectedButton.Text = "選択先へ共有";
        _stopScreenButton.Text = "共有停止";

        _refreshOpenTargetsButton.Text = "一覧更新";
        _sendOpenTargetAllButton.Text = "ブラウザを開く";
        _sendOpenTargetSelectedButton.Text = "選択先で開く";
        _sendFileAndOpenAllButton.Text = "ファイル送信して開く";
        _sendFileAndOpenSelectedButton.Text = "選択先へ送信して開く";
        _openManualUrlAllButton.Text = "URLを全員で開く";
        _openManualUrlSelectedButton.Text = "URLを選択先で開く";
    }

    private void ApplyModernThemeTree(Control root)
    {
        root.SuspendLayout();
        ApplyModernTheme(root);

        foreach (Control child in root.Controls)
        {
            ApplyModernThemeTree(child);
        }

        root.ResumeLayout(performLayout: false);
    }

    private void ApplyModernTheme(Control control)
    {
        control.ForeColor = ModernText;
        control.Font = new Font("Yu Gothic UI", control.Font.Size, control.Font.Style);

        switch (control)
        {
            case Form form:
                form.BackColor = ModernBackground;
                form.ForeColor = ModernText;
                break;

            case GroupBox groupBox:
                groupBox.BackColor = ModernPanel;
                groupBox.ForeColor = ModernText;
                groupBox.Padding = new Padding(12, 22, 12, 12);
                break;

            case TableLayoutPanel table:
                table.BackColor = control.Parent is GroupBox ? ModernPanel : ModernBackground;
                break;

            case FlowLayoutPanel flow:
                flow.BackColor = control.Parent is GroupBox ? ModernPanel : ModernBackground;
                flow.Padding = new Padding(0, 2, 0, 2);
                break;

            case Button button:
                StyleModernButton(button, isDanger: button == _stopButton || button == _stopScreenButton || button == _cancelTransferButton);
                break;

            case TextBox textBox:
                textBox.BackColor = ModernPanelAlt;
                textBox.ForeColor = ModernText;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            case ComboBox comboBox:
                comboBox.BackColor = ModernPanelAlt;
                comboBox.ForeColor = ModernText;
                comboBox.FlatStyle = FlatStyle.Flat;
                break;

            case NumericUpDown numeric:
                numeric.BackColor = ModernPanelAlt;
                numeric.ForeColor = ModernText;
                numeric.BorderStyle = BorderStyle.FixedSingle;
                break;

            case ListBox listBox:
                listBox.BackColor = ModernPanelAlt;
                listBox.ForeColor = ModernText;
                listBox.BorderStyle = BorderStyle.FixedSingle;
                listBox.IntegralHeight = false;
                break;

            case Label label:
                label.BackColor = Color.Transparent;
                label.ForeColor = label == _progressLabel || label == _screenStatusLabel || label == _openTargetStatusLabel
                    ? ModernSubText
                    : ModernText;
                break;
        }
    }

    private static void StyleModernButton(Button button, bool isDanger)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = isDanger ? ModernDanger : ModernAccent;
        button.ForeColor = ModernText;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = ModernBorder;
        button.FlatAppearance.MouseOverBackColor = ModernAccentHover;
        button.FlatAppearance.MouseDownBackColor = ModernPanelAlt;
        button.Height = Math.Max(button.Height, 32);
        button.Margin = new Padding(4, 3, 4, 3);
        button.Cursor = Cursors.Hand;
    }

    private static Icon TryGetApplicationIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }
}
