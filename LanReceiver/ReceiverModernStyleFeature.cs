using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace LanReceiver;

/// <summary>
/// Applies a modern dark theme to <see cref="ReceiverForm"/>.
///
/// Design notes
/// ------------
/// * This file is fully additive – it does not modify any existing partial
///   class file (ReceiverForm.cs, ReceiverTrayFeature.cs, etc.). It hooks
///   into the form lifecycle by overriding <see cref="OnHandleCreated"/>,
///   which is the only large lifecycle method not already overridden in
///   the existing partial classes.
/// * The theme is applied once after the first <c>Shown</c> event so all
///   controls (including the H.264 panel and Open-target panel that are
///   added dynamically during construction) are guaranteed to exist.
/// * Buttons have three semantic roles - Primary, Secondary, Danger - so
///   the user can tell at a glance which action is the "main" one in each
///   pane and which actions are destructive.
/// * The connection status label gets a small colored dot painted in
///   front of its text. Adding a separate StatusDot control would have
///   required modifying BuildUi; the Paint hook is much less invasive.
/// </summary>
public sealed partial class ReceiverForm
{
    // ---------------------------------------------------------------------
    // Palette - same values used by LanSender so both apps look identical.
    // ---------------------------------------------------------------------
    private static readonly Color ModernBackground = Color.FromArgb(13, 17, 23);
    private static readonly Color ModernPanel = Color.FromArgb(22, 27, 34);
    private static readonly Color ModernPanelAlt = Color.FromArgb(33, 38, 45);
    private static readonly Color ModernBorder = Color.FromArgb(48, 54, 61);
    private static readonly Color ModernText = Color.FromArgb(230, 237, 243);
    private static readonly Color ModernSubText = Color.FromArgb(139, 148, 158);
    private static readonly Color ModernAccent = Color.FromArgb(33, 38, 45);
    private static readonly Color ModernAccentHover = Color.FromArgb(48, 54, 61);
    private static readonly Color ModernDanger = Color.FromArgb(218, 54, 51);
    private static readonly Color ModernDangerHover = Color.FromArgb(248, 81, 73);
    private static readonly Color ModernPrimary = Color.FromArgb(31, 111, 235);
    private static readonly Color ModernPrimaryHover = Color.FromArgb(57, 132, 245);
    private static readonly Color ModernSuccess = Color.FromArgb(46, 160, 67);
    private static readonly Color ModernWarning = Color.FromArgb(210, 153, 34);
    private static readonly Color ModernIdle = Color.FromArgb(139, 148, 158);

    private static readonly Font ModernUiFont = new("Yu Gothic UI", 9.0f, FontStyle.Regular);
    private static readonly Font ModernButtonFont = new("Yu Gothic UI", 9.25f, FontStyle.Bold);

    private bool _modernStyleHookInstalled;
    private bool _modernStyleApplied;
    private bool _connectedNow;

    /// <summary>
    /// We use OnHandleCreated as the entry point because it is not
    /// overridden in any other partial class file, and it fires reliably
    /// before the first time the form becomes visible. The actual styling
    /// is deferred to the Shown event so that all controls (including the
    /// H.264 panel that the H264 feature appends to <c>Controls</c>) are
    /// fully realized.
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (_modernStyleHookInstalled)
        {
            return;
        }

        _modernStyleHookInstalled = true;
        Shown += (_, _) =>
        {
            if (_modernStyleApplied) return;
            _modernStyleApplied = true;

            Text = "LAN Transfer Receiver";
            RewriteModernLabels();
            ApplyModernThemeTree(this);
            InstallStatusDotPainter();
            InstallCustomProgressBarPainter();
            HookConnectionStateTracking();
        };
    }

    // ---------------------------------------------------------------------
    // Label localization (Japanese, to match LanSender)
    // ---------------------------------------------------------------------

    private void RewriteModernLabels()
    {
        _connectButton.Text = "接続";
        _disconnectButton.Text = "切断";
        _clearButton.Text = "クリア";
        _chooseFolderButton.Text = "選択";
        _openFolderButton.Text = "開く";
        _openFullScreenButton.Text = "全画面表示";
        _openFolderAfterReceiveCheck.Text = "受信完了後にフォルダを開く";
        _autoFullScreenCheck.Text = "最初の画面フレームを受信したら自動全画面";

        if (string.Equals(_statusLabel.Text, "Disconnected", StringComparison.OrdinalIgnoreCase))
        {
            _statusLabel.Text = "未接続";
        }
        if (string.Equals(_progressLabel.Text, "Idle", StringComparison.OrdinalIgnoreCase))
        {
            _progressLabel.Text = "待機中";
        }
        if (_screenInfoLabel.Text.StartsWith("No screen frame", StringComparison.OrdinalIgnoreCase))
        {
            _screenInfoLabel.Text = "まだ画面フレームを受信していません。";
        }

        // BuildUi creates inline labels and GroupBoxes locally rather than
        // storing them as fields, so we walk the tree and translate them
        // by current text. This is intentionally conservative: any text we
        // don't recognise is left untouched.
        TranslateLocalLabelsAndBoxes(this);
    }

    private static readonly Dictionary<string, string> InlineLabelTranslations = new(StringComparer.Ordinal)
    {
        // GroupBox titles
        { "Screen monitor preview", "画面プレビュー" },
        { "Received messages / files", "受信メッセージ・ファイル" },
        { "Log", "ログ" },
        // Inline labels
        { "Server IP:", "サーバーIP:" },
        { "Port:", "ポート:" },
        { "Save to:", "保存先:" },
    };

    private static void TranslateLocalLabelsAndBoxes(Control root)
    {
        foreach (Control child in root.Controls)
        {
            switch (child)
            {
                case GroupBox group when InlineLabelTranslations.TryGetValue(group.Text ?? "", out string? gj):
                    group.Text = gj;
                    break;
                case Label label when InlineLabelTranslations.TryGetValue(label.Text ?? "", out string? lj):
                    label.Text = lj;
                    break;
            }
            TranslateLocalLabelsAndBoxes(child);
        }
    }

    // ---------------------------------------------------------------------
    // Theme application
    // ---------------------------------------------------------------------

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

            case PictureBox pictureBox:
                pictureBox.BackColor = Color.Black;
                break;

            case Label label:
                label.BackColor = Color.Transparent;
                label.ForeColor = label == _progressLabel || label == _screenInfoLabel
                    ? ModernSubText
                    : ModernText;
                break;
        }
    }

    /// <summary>
    /// Classify each button as primary, secondary, or danger so the visual
    /// hierarchy lines up with what the user is actually trying to do.
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
        // Disconnect is a destructive action - if you click it during a
        // transfer the transfer drops, so make it stand out as red.
        if (button == _disconnectButton)
        {
            return ButtonRole.Danger;
        }

        // The headline action: connect to the server.
        if (button == _connectButton)
        {
            return ButtonRole.Primary;
        }

        return ButtonRole.Secondary;
    }

    // ---------------------------------------------------------------------
    // Status dot - draws a colored circle in front of the status text
    // ---------------------------------------------------------------------

    /// <summary>
    /// Tracks the connection state by intercepting calls to
    /// <c>SetConnectedUi</c>. We do that by watching the disconnect
    /// button's Enabled state, which is set to <c>true</c> when connected
    /// and <c>false</c> otherwise.
    /// </summary>
    private void HookConnectionStateTracking()
    {
        _disconnectButton.EnabledChanged += (_, _) =>
        {
            _connectedNow = _disconnectButton.Enabled;
            _statusLabel.Invalidate();
        };
        _connectedNow = _disconnectButton.Enabled;
    }

    private void InstallStatusDotPainter()
    {
        // Connection status dot
        _statusLabel.Padding = new Padding(20, 0, 0, 0);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Paint += (sender, e) =>
        {
            if (sender is not Label label) return;
            Color color = _connectedNow ? ModernSuccess : ModernIdle;
            DrawStatusDot(e.Graphics, label, color);
        };
        _statusLabel.TextChanged += (_, _) => _statusLabel.Invalidate();

        // Progress label dot - amber while a batch is active, idle otherwise
        _progressLabel.Padding = new Padding(20, 0, 0, 0);
        _progressLabel.TextAlign = ContentAlignment.MiddleLeft;
        _progressLabel.Paint += (sender, e) =>
        {
            if (sender is not Label label) return;
            Color color = _batchActive || (_progressBar.Value > 0 && _progressBar.Value < _progressBar.Maximum)
                ? ModernWarning
                : ModernIdle;
            DrawStatusDot(e.Graphics, label, color);
        };
        _progressLabel.TextChanged += (_, _) => _progressLabel.Invalidate();

        // Screen info label dot - green when frames are flowing
        _screenInfoLabel.Padding = new Padding(20, 0, 0, 0);
        _screenInfoLabel.TextAlign = ContentAlignment.MiddleLeft;
        _screenInfoLabel.Paint += (sender, e) =>
        {
            if (sender is not Label label) return;
            Color color = _activeStreamId is not null ? ModernSuccess : ModernIdle;
            DrawStatusDot(e.Graphics, label, color);
        };
        _screenInfoLabel.TextChanged += (_, _) => _screenInfoLabel.Invalidate();
    }

    private static void DrawStatusDot(Graphics g, Label label, Color color)
    {
        const int size = 10;
        int y = (label.ClientSize.Height - size) / 2;
        const int x = 4;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, x, y, size, size);

        // Soft halo for a slightly raised feel.
        using var ringPen = new Pen(Color.FromArgb(80, color), 1.5f);
        g.DrawEllipse(ringPen, x - 1.5f, y - 1.5f, size + 3, size + 3);
    }

    // ---------------------------------------------------------------------
    // Custom progress bar - flat dark-mode bar with a percentage readout
    // ---------------------------------------------------------------------

    private void InstallCustomProgressBarPainter()
    {
        // ProgressBar doesn't expose SetStyle publicly. We need UserPaint
        // to take over rendering completely. If reflection fails (future
        // .NET versions could rename the internal API), we silently keep
        // the OS default appearance.
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
        catch
        {
            // Keep default progress bar appearance.
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

        // Filled portion
        long range = (long)bar.Maximum - bar.Minimum;
        if (range <= 0) range = 1;
        double ratio = (bar.Value - bar.Minimum) / (double)range;
        ratio = Math.Clamp(ratio, 0.0, 1.0);

        int fillWidth = (int)(rect.Width * ratio);
        if (fillWidth > 2)
        {
            var fillRect = new Rectangle(rect.X, rect.Y, fillWidth, rect.Height);
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

        // Percentage overlay
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
}
