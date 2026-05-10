using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace LanSender;

public sealed partial class SenderForm
{
    private const bool DefaultAutoStartServer = true;
    private const bool DefaultMinimizeToTrayAfterAutoStart = true;

    // ---------------------------------------------------------------------
    // Modern color palette (GitHub Dark inspired).
    // The names are kept compatible with the previous version so that
    // SenderQuickActionForm.cs and any other call sites keep working,
    // but the values are tuned for higher contrast and clearer hierarchy.
    // ---------------------------------------------------------------------
    internal static readonly Color ModernBackground = Color.FromArgb(13, 17, 23);    // app background
    internal static readonly Color ModernPanel = Color.FromArgb(22, 27, 34);         // group panel
    internal static readonly Color ModernPanelAlt = Color.FromArgb(33, 38, 45);      // input bg
    internal static readonly Color ModernBorder = Color.FromArgb(48, 54, 61);
    internal static readonly Color ModernText = Color.FromArgb(230, 237, 243);
    internal static readonly Color ModernSubText = Color.FromArgb(139, 148, 158);
    internal static readonly Color ModernAccent = Color.FromArgb(33, 38, 45);        // secondary button bg
    internal static readonly Color ModernAccentHover = Color.FromArgb(48, 54, 61);
    internal static readonly Color ModernDanger = Color.FromArgb(218, 54, 51);       // danger button (vivid red)
    internal static readonly Color ModernDangerHover = Color.FromArgb(248, 81, 73);

    // New semantic colors (introduced in this revision).
    internal static readonly Color ModernPrimary = Color.FromArgb(31, 111, 235);     // primary action (blue)
    internal static readonly Color ModernPrimaryHover = Color.FromArgb(57, 132, 245);
    internal static readonly Color ModernSuccess = Color.FromArgb(46, 160, 67);      // green = connected/active
    internal static readonly Color ModernWarning = Color.FromArgb(210, 153, 34);     // yellow = transitional
    internal static readonly Color ModernIdle = Color.FromArgb(139, 148, 158);       // gray = idle/disconnected

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

    // Cached UI font so we don't keep allocating new Font instances per control.
    private static readonly Font ModernUiFont = new("Yu Gothic UI", 9.0f, FontStyle.Regular);
    private static readonly Font ModernButtonFont = new("Yu Gothic UI", 9.25f, FontStyle.Bold);

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
        InstallStatusDotPainter();
        InstallCustomProgressBarPainter();
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

        var showItem = new ToolStripMenuItem("メイン画面を表示");
        showItem.Click += (_, _) => RestoreSenderWindow();

        var hideItem = new ToolStripMenuItem("タスクトレイへ隠す");
        hideItem.Click += (_, _) => MinimizeSenderToTray(showBalloon: false);

        var startItem = new ToolStripMenuItem("サーバー起動");
        startItem.Click += (_, _) => StartServer();

        var stopItem = new ToolStripMenuItem("サーバー停止");
        stopItem.Click += (_, _) => StopServer();

        var shareItem = new ToolStripMenuItem("画面共有 (H.264)");
        shareItem.Click += async (_, _) => await QuickToggleScreenShareAsync();

        var exitItem = new ToolStripMenuItem("終了");
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

        button.PerformClick();
        return true;
    }

    private bool TryPerformH264StopButtonClick()
    {
        Button? button = FindH264Button(requiredKeyword: "stop", selectedOnly: null);

        if (button is null || !button.Enabled)
        {
            return false;
        }

        button.PerformClick();
        return true;
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
                "送信側はタスクトレイで動作中です。",
                ToolTipIcon.Info);
        }
    }

    // ---------------------------------------------------------------------
    // Theming
    // ---------------------------------------------------------------------

    private void RewriteModernLabels()
    {
        _startButton.Text = "サーバー起動";
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
        if (!ReferenceEquals(control.Font, ModernUiFont))
        {
            control.Font = new Font(ModernUiFont.FontFamily, control.Font.Size, control.Font.Style);
        }

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
                StyleModernButton(button);
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

            case CheckBox checkBox:
                checkBox.BackColor = Color.Transparent;
                checkBox.ForeColor = ModernText;
                checkBox.FlatStyle = FlatStyle.Flat;
                break;

            case Label label:
                label.BackColor = Color.Transparent;
                label.ForeColor = label == _progressLabel || label == _screenStatusLabel || label == _openTargetStatusLabel
                    ? ModernSubText
                    : ModernText;
                break;
        }
    }

    /// <summary>
    /// Style a button based on its semantic role.
    /// Primary buttons (the "main action" of each panel) get a vivid blue
    /// accent so the user's eye lands on them first; danger buttons (stop,
    /// cancel, disconnect) get a vivid red so accidental clicks are unlikely;
    /// everything else uses a neutral panel-tone background.
    /// </summary>
    private void StyleModernButton(Button button)
    {
        ButtonRole role = ClassifyButton(button);

        Color bg, hover, down, border;
        switch (role)
        {
            case ButtonRole.Primary:
                bg = ModernPrimary;
                hover = ModernPrimaryHover;
                down = ControlPaint.Dark(ModernPrimary, 0.05f);
                border = ModernPrimaryHover;
                break;
            case ButtonRole.Danger:
                bg = ModernDanger;
                hover = ModernDangerHover;
                down = ControlPaint.Dark(ModernDanger, 0.05f);
                border = ModernDangerHover;
                break;
            default:
                bg = ModernAccent;
                hover = ModernAccentHover;
                down = ModernPanelAlt;
                border = ModernBorder;
                break;
        }

        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = bg;
        button.ForeColor = ModernText;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = border;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = down;
        button.Height = Math.Max(button.Height, 34);
        button.Margin = new Padding(4, 3, 4, 3);
        button.Cursor = Cursors.Hand;
        button.Font = ModernButtonFont;
        button.UseCompatibleTextRendering = false;
    }

    private enum ButtonRole { Secondary, Primary, Danger }

    private ButtonRole ClassifyButton(Button button)
    {
        // Danger first so a "stop" button never gets misclassified as primary.
        if (button == _stopButton
            || button == _stopScreenButton
            || button == _cancelTransferButton)
        {
            return ButtonRole.Danger;
        }

        // The primary action of each pane (start server, broadcast, share screen, etc.)
        if (button == _startButton
            || button == _sendAllButton
            || button == _sendFileAllButton
            || button == _startScreenAllButton
            || button == _sendScreenOnceAllButton
            || button == _sendOpenTargetAllButton
            || button == _sendFileAndOpenAllButton
            || button == _openManualUrlAllButton
            || button == _addFilesButton)
        {
            return ButtonRole.Primary;
        }

        return ButtonRole.Secondary;
    }

    // ---------------------------------------------------------------------
    // Decorative paint hooks: a colored status dot + a custom progress bar
    // ---------------------------------------------------------------------

    /// <summary>
    /// Paints a small colored circle in front of the status text.
    /// Color reflects the current sharing/transfer state without us having
    /// to add a new control to BuildUi.
    /// </summary>
    private void InstallStatusDotPainter()
    {
        if (_screenStatusLabel == null) return;

        // Make space for the dot.
        _screenStatusLabel.Padding = new Padding(20, 0, 0, 0);
        _screenStatusLabel.TextAlign = ContentAlignment.MiddleLeft;

        _screenStatusLabel.Paint += (sender, e) =>
        {
            if (sender is not Label label) return;

            Color dotColor =
                _isStreamingScreen ? ModernSuccess :
                _isSendingFile ? ModernWarning :
                _serverCts is not null ? ModernPrimary :
                ModernIdle;

            DrawStatusDot(e.Graphics, label, dotColor);
        };

        // Repaint the dot when state changes.
        _screenStatusLabel.TextChanged += (_, _) => _screenStatusLabel.Invalidate();

        // Same treatment for the progress label.
        if (_progressLabel != null)
        {
            _progressLabel.Padding = new Padding(20, 0, 0, 0);
            _progressLabel.TextAlign = ContentAlignment.MiddleLeft;
            _progressLabel.Paint += (sender, e) =>
            {
                if (sender is not Label label) return;
                Color dotColor = _isSendingFile ? ModernWarning : ModernIdle;
                DrawStatusDot(e.Graphics, label, dotColor);
            };
            _progressLabel.TextChanged += (_, _) => _progressLabel.Invalidate();
        }
    }

    private static void DrawStatusDot(Graphics g, Label label, Color color)
    {
        const int size = 10;
        int y = (label.ClientSize.Height - size) / 2;
        const int x = 4;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, x, y, size, size);

        // Soft outer ring for a more "polished" look.
        using var ringPen = new Pen(Color.FromArgb(80, color), 1.5f);
        g.DrawEllipse(ringPen, x - 1.5f, y - 1.5f, size + 3, size + 3);
    }

    /// <summary>
    /// Replaces the OS-default ProgressBar look with a flat dark-mode bar
    /// that includes a percentage readout. This is purely cosmetic - the
    /// underlying ProgressBar's Value/Min/Max semantics are unchanged.
    /// </summary>
    private void InstallCustomProgressBarPainter()
    {
        if (_progressBar == null) return;

        // Owner-draw needs SetStyle, but ProgressBar doesn't expose it; we
        // attach a custom drawer using the underlying paint pipeline. If
        // reflection fails for any reason (e.g. in a future .NET version
        // where the internal API has moved), we silently keep the OS
        // default appearance instead of producing a half-painted control.
        try
        {
            MethodInfo? setStyle = typeof(ProgressBar)
                .GetMethod("SetStyle", BindingFlags.Instance | BindingFlags.NonPublic);

            if (setStyle is null) return;

            setStyle.Invoke(_progressBar, new object[]
            {
                ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint,
                true,
            });

            _progressBar.Paint += DrawModernProgressBar;
        }
        catch (Exception ex)
        {
            AddLog($"Custom progress bar disabled: {ex.Message}");
        }
    }

    private void DrawModernProgressBar(object? sender, PaintEventArgs e)
    {
        if (sender is not ProgressBar bar) return;

        Rectangle rect = bar.ClientRectangle;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Track
        using (var trackBrush = new SolidBrush(ModernPanelAlt))
        using (var trackPath = RoundedRect(rect, 4))
        {
            g.FillPath(trackBrush, trackPath);
        }

        // Fill (clamped, never overflows)
        long range = (long)bar.Maximum - bar.Minimum;
        if (range <= 0) range = 1;
        double ratio = (bar.Value - bar.Minimum) / (double)range;
        ratio = Math.Clamp(ratio, 0.0, 1.0);

        int fillWidth = (int)(rect.Width * ratio);
        if (fillWidth > 2)
        {
            var fillRect = new Rectangle(rect.X, rect.Y, fillWidth, rect.Height);
            // Subtle gradient gives a more "alive" look than a flat fill.
            using var gradient = new LinearGradientBrush(
                fillRect,
                ModernPrimary,
                ModernPrimaryHover,
                LinearGradientMode.Horizontal);
            using var fillPath = RoundedRect(fillRect, 4);
            g.FillPath(gradient, fillPath);
        }

        // Border
        using (var borderPen = new Pen(ModernBorder, 1f))
        using (var borderPath = RoundedRect(new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1), 4))
        {
            g.DrawPath(borderPen, borderPath);
        }

        // Percentage text overlay, centered.
        string text = $"{ratio * 100:0.#} %";
        using var textBrush = new SolidBrush(ratio > 0.5 ? ModernText : ModernSubText);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, ModernUiFont, textBrush, rect, format);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();

        if (radius == 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);

        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);

        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);

        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }

    // ---------------------------------------------------------------------
    // Reflection helpers (unchanged from previous revision)
    // ---------------------------------------------------------------------

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
