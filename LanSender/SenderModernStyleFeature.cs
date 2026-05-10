using System.ComponentModel;
using System.Drawing;
using System.Reflection;

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

    private static readonly string[] PreferredH264StartMethodNames =
    {
        "StartH264ScreenStreamAsync",
        "StartH264StreamAsync",
        "StartH264StreamingAsync",
        "StartH264VideoStreamAsync",
        "StartH264ScreenShareAsync",
        "StartH264ShareAsync",
        "StartScreenH264StreamAsync",
    };

    private static readonly string[] PreferredH264StopMethodNames =
    {
        "StopH264ScreenStream",
        "StopH264Stream",
        "StopH264Streaming",
        "StopH264VideoStream",
        "StopH264ScreenShare",
        "StopH264Share",
        "StopScreenH264Stream",
    };

    private readonly NotifyIcon _senderTrayIcon = new();
    private readonly ContextMenuStrip _senderTrayMenu = new();
    private SenderQuickActionForm? _senderQuickActionForm;
    private bool _modernFeatureInitialized;
    private bool _screenShareButtonsRebound;
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

        ApplyScreenSharingDefaults();
        RebindScreenSharingButtonsForPreferredCodec();
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

        var shareItem = new ToolStripMenuItem("Share screen (H.264)");
        shareItem.Click += async (_, _) => await QuickToggleScreenShareAsync();

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
            IsAnyScreenSharingActive);
        _senderQuickActionForm.Show();
    }

    private async Task QuickToggleScreenShareAsync()
    {
        if (IsAnyScreenSharingActive())
        {
            StopPreferredScreenShare();
            _senderQuickActionForm?.RefreshState();
            return;
        }

        if (_serverCts is null)
        {
            StartServer();
        }

        await StartPreferredScreenShareAsync(selectedOnly: false);
        _senderQuickActionForm?.RefreshState();
    }

    private async Task StartPreferredScreenShareAsync(bool selectedOnly)
    {
        if (_isSendingFile)
        {
            AddLog("ファイル送信が終わってから画面共有を開始してください。");
            return;
        }

        if (IsAnyScreenSharingActive())
        {
            AddLog("すでに画面共有中です。");
            return;
        }

        if (TryPerformH264StartButtonClick(selectedOnly))
        {
            return;
        }

        if (await TryInvokeH264StartMethodAsync(selectedOnly))
        {
            return;
        }

        AddLog("H.264 screen sharing entry point was not found. Falling back to DXGI/MJPEG screen streaming.");
        await StartScreenStreamAsync(selectedOnly);
    }

    private void StopPreferredScreenShare()
    {
        if (TryPerformH264StopButtonClick())
        {
            return;
        }

        if (TryInvokeH264StopMethod())
        {
            return;
        }

        StopScreenStream();
    }

    private void ApplyScreenSharingDefaults()
    {
        if (_screenScaleBox.Minimum <= 100 && _screenScaleBox.Maximum >= 100)
        {
            _screenScaleBox.Value = 100;
        }
    }

    private void RebindScreenSharingButtonsForPreferredCodec()
    {
        if (_screenShareButtonsRebound)
        {
            return;
        }

        bool reboundAll = TryReplaceClickHandlers(
            _startScreenAllButton,
            async (_, _) => await StartPreferredScreenShareAsync(selectedOnly: false));

        bool reboundSelected = TryReplaceClickHandlers(
            _startScreenSelectedButton,
            async (_, _) => await StartPreferredScreenShareAsync(selectedOnly: true));

        bool reboundStop = TryReplaceClickHandlers(
            _stopScreenButton,
            (_, _) => StopPreferredScreenShare());

        _screenShareButtonsRebound = reboundAll && reboundSelected && reboundStop;

        if (!_screenShareButtonsRebound)
        {
            AddLog("Screen sharing buttons could not be rebound. Existing handlers were kept.");
        }
    }

    private bool TryPerformH264StartButtonClick(bool selectedOnly)
    {
        Button? button = FindH264Button(
            requiredKeyword: "start",
            selectedOnly: selectedOnly)
            ?? FindH264Button(
                requiredKeyword: "share",
                selectedOnly: selectedOnly)
            ?? FindH264Button(
                requiredKeyword: "stream",
                selectedOnly: selectedOnly);

        if (button is null || !button.Enabled)
        {
            return false;
        }

        return TryRaiseButtonClick(button);
    }

    private bool TryPerformH264StopButtonClick()
    {
        Button? button = FindH264Button(requiredKeyword: "stop", selectedOnly: null);

        if (button is null || !button.Enabled)
        {
            return false;
        }

        return TryRaiseButtonClick(button);
    }

    private static bool TryRaiseButtonClick(Button button)
    {
        try
        {
            MethodInfo? onClick = typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);

            if (onClick is null)
            {
                button.PerformClick();
                return true;
            }

            void Raise() => onClick.Invoke(button, new object?[] { EventArgs.Empty });

            if (button.InvokeRequired)
            {
                button.Invoke(new Action(Raise));
            }
            else
            {
                Raise();
            }

            return true;
        }
        catch
        {
            try
            {
                button.PerformClick();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private Button? FindH264Button(string requiredKeyword, bool? selectedOnly)
    {
        foreach (FieldInfo field in GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (!typeof(Button).IsAssignableFrom(field.FieldType))
            {
                continue;
            }

            string name = field.Name;
            if (!ContainsIgnoreCase(name, "h264") || !ContainsIgnoreCase(name, requiredKeyword))
            {
                continue;
            }

            if (selectedOnly == true && !(ContainsIgnoreCase(name, "selected") || ContainsIgnoreCase(name, "one")))
            {
                continue;
            }

            if (selectedOnly == false && (ContainsIgnoreCase(name, "selected") || ContainsIgnoreCase(name, "one")))
            {
                continue;
            }

            if (field.GetValue(this) is Button button)
            {
                return button;
            }
        }

        return null;
    }

    private async Task<bool> TryInvokeH264StartMethodAsync(bool selectedOnly)
    {
        foreach (MethodInfo method in EnumeratePreferredH264StartMethods())
        {
            object?[]? args = BuildH264StartArguments(method, selectedOnly);

            if (args is null)
            {
                continue;
            }

            try
            {
                object? result = method.Invoke(this, args);

                if (result is Task task)
                {
                    await task;
                }

                return true;
            }
            catch (TargetInvocationException ex)
            {
                AddLog($"H.264 screen sharing failed: {ex.InnerException?.Message ?? ex.Message}");
                return true;
            }
            catch (Exception ex)
            {
                AddLog($"H.264 screen sharing failed: {ex.Message}");
                return true;
            }
        }

        return false;
    }

    private bool TryInvokeH264StopMethod()
    {
        foreach (MethodInfo method in EnumeratePreferredH264StopMethods())
        {
            if (method.GetParameters().Length != 0)
            {
                continue;
            }

            try
            {
                object? result = method.Invoke(this, Array.Empty<object?>());

                if (result is Task task)
                {
                    _ = task;
                }

                return true;
            }
            catch (TargetInvocationException ex)
            {
                AddLog($"H.264 screen sharing stop failed: {ex.InnerException?.Message ?? ex.Message}");
                return true;
            }
            catch (Exception ex)
            {
                AddLog($"H.264 screen sharing stop failed: {ex.Message}");
                return true;
            }
        }

        return false;
    }

    private IEnumerable<MethodInfo> EnumeratePreferredH264StartMethods()
    {
        MethodInfo[] methods = GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        foreach (string methodName in PreferredH264StartMethodNames)
        {
            foreach (MethodInfo method in methods.Where(x => string.Equals(x.Name, methodName, StringComparison.OrdinalIgnoreCase)))
            {
                yield return method;
            }
        }

        foreach (MethodInfo method in methods)
        {
            if (ContainsIgnoreCase(method.Name, "h264") && ContainsIgnoreCase(method.Name, "start"))
            {
                yield return method;
            }
        }
    }

    private IEnumerable<MethodInfo> EnumeratePreferredH264StopMethods()
    {
        MethodInfo[] methods = GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        foreach (string methodName in PreferredH264StopMethodNames)
        {
            foreach (MethodInfo method in methods.Where(x => string.Equals(x.Name, methodName, StringComparison.OrdinalIgnoreCase)))
            {
                yield return method;
            }
        }

        foreach (MethodInfo method in methods)
        {
            if (ContainsIgnoreCase(method.Name, "h264") && ContainsIgnoreCase(method.Name, "stop"))
            {
                yield return method;
            }
        }
    }

    private static object?[]? BuildH264StartArguments(MethodInfo method, bool selectedOnly)
    {
        ParameterInfo[] parameters = method.GetParameters();

        if (parameters.Length == 0)
        {
            return Array.Empty<object?>();
        }

        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
        {
            return new object?[] { selectedOnly };
        }

        return null;
    }

    private bool IsAnyScreenSharingActive()
    {
        if (_isStreamingScreen)
        {
            return true;
        }

        foreach (FieldInfo field in GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            string name = field.Name;
            if (!ContainsIgnoreCase(name, "h264"))
            {
                continue;
            }

            object? value = field.GetValue(this);
            if (value is bool flag && flag && (ContainsIgnoreCase(name, "stream") || ContainsIgnoreCase(name, "share")))
            {
                return true;
            }

            if (value is CancellationTokenSource cts && !cts.IsCancellationRequested)
            {
                return true;
            }
        }

        return false;
    }

    private void RestoreSenderWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _senderQuickActionForm?.Show();
        _senderQuickActionForm?.MoveToBottomRight();
        _senderQuickActionForm?.RefreshState();
    }

    private void MinimizeSenderToTray(bool showBalloon)
    {
        WindowState = FormWindowState.Minimized;
        ShowInTaskbar = false;
        Hide();
        _senderQuickActionForm?.Show();
        _senderQuickActionForm?.MoveToBottomRight();
        _senderQuickActionForm?.RefreshState();

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
        _screenStatusLabel.Text = "H.264画面共有を通常使用 / DXGIスケール100%";

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

    private static bool TryReplaceClickHandlers(Control control, EventHandler handler)
    {
        if (!TryClearClickHandlers(control))
        {
            return false;
        }

        control.Click += handler;
        return true;
    }

    private static bool TryClearClickHandlers(Control control)
    {
        object? clickEventKey = GetControlClickEventKey();
        if (clickEventKey is null)
        {
            return false;
        }

        PropertyInfo? eventsProperty = typeof(Component).GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic);
        if (eventsProperty?.GetValue(control) is not EventHandlerList events)
        {
            return false;
        }

        Delegate? existing = events[clickEventKey];
        if (existing is not null)
        {
            events.RemoveHandler(clickEventKey, existing);
        }

        return true;
    }

    private static object? GetControlClickEventKey()
    {
        string[] candidateNames =
        {
            "s_clickEvent",
            "EventClick",
            "ClickEvent",
        };

        foreach (string candidateName in candidateNames)
        {
            FieldInfo? field = typeof(Control).GetField(candidateName, BindingFlags.Static | BindingFlags.NonPublic);
            object? value = field?.GetValue(null);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static bool ContainsIgnoreCase(string value, string keyword)
    {
        return value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
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
