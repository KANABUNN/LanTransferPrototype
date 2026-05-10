using System.Drawing;

namespace LanSender;

/// <summary>
/// Floating "always-on-top" quick action panel that lets the user toggle
/// screen sharing without restoring the main sender window.
/// Now uses the same modern palette as the rest of the application and
/// renders a soft drop shadow + rounded button for a more polished feel.
/// </summary>
internal sealed class SenderQuickActionForm : Form
{
    private readonly Func<Task> _shareScreenAction;
    private readonly Func<bool> _isSharingProvider;
    private readonly Button _shareButton = new();

    public SenderQuickActionForm(
        Func<Task> shareScreenAction,
        Func<bool> isSharingProvider)
    {
        _shareScreenAction = shareScreenAction;
        _isSharingProvider = isSharingProvider;

        Text = "LAN Sender Quick Actions";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Width = 220;
        Height = 70;
        BackColor = SenderForm.ModernPanel;
        Padding = new Padding(10);
        DoubleBuffered = true;

        // A 1px outer border keeps the floating panel readable on bright
        // wallpapers while still feeling weightless on dark ones.
        Paint += (_, e) =>
        {
            using var pen = new Pen(SenderForm.ModernBorder, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        };

        ConfigureButton(_shareButton, "▶ 画面を共有する");
        _shareButton.Click += async (_, _) => await RunActionAsync(_shareScreenAction);
        Controls.Add(_shareButton);

        Shown += (_, _) => MoveToBottomRight();
        ResizeEnd += (_, _) => MoveToBottomRight();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= wsExToolWindow;
            return cp;
        }
    }

    public void MoveToBottomRight()
    {
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 22, area.Bottom - Height - 22);
    }

    public void RefreshState()
    {
        UpdateShareButtonText();
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        _shareButton.Enabled = false;

        try
        {
            await action();
        }
        finally
        {
            UpdateShareButtonText();
            _shareButton.Enabled = true;
            MoveToBottomRight();
        }
    }

    private void UpdateShareButtonText()
    {
        bool isSharing = _isSharingProvider();
        _shareButton.Text = isSharing ? "■ 画面共有を停止" : "▶ 画面を共有する";

        // Switch the accent color so the user instantly knows what the
        // button does in its current state - "stop" reads as red, "start"
        // reads as primary-blue.
        if (isSharing)
        {
            _shareButton.BackColor = SenderForm.ModernDanger;
            _shareButton.FlatAppearance.MouseOverBackColor = SenderForm.ModernDangerHover;
            _shareButton.FlatAppearance.BorderColor = SenderForm.ModernDangerHover;
        }
        else
        {
            _shareButton.BackColor = SenderForm.ModernPrimary;
            _shareButton.FlatAppearance.MouseOverBackColor = SenderForm.ModernPrimaryHover;
            _shareButton.FlatAppearance.BorderColor = SenderForm.ModernPrimaryHover;
        }
    }

    private static void ConfigureButton(Button button, string text)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = SenderForm.ModernPrimary;
        button.ForeColor = SenderForm.ModernText;
        button.Font = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = SenderForm.ModernPrimaryHover;
        button.FlatAppearance.MouseOverBackColor = SenderForm.ModernPrimaryHover;
        button.FlatAppearance.MouseDownBackColor = SenderForm.ModernPanelAlt;
        button.Cursor = Cursors.Hand;
        button.UseCompatibleTextRendering = false;
    }
}
