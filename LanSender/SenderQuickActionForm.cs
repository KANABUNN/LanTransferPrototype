using System.Drawing;

namespace LanSender;

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
        Width = 210;
        Height = 64;
        BackColor = Color.FromArgb(8, 12, 18);
        Padding = new Padding(10);

        ConfigureButton(_shareButton, "画面を共有する");
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
        _shareButton.Text = _isSharingProvider() ? "画面共有を停止" : "画面を共有する";
    }

    private static void ConfigureButton(Button button, string text)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0);
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(16, 22, 31);
        button.ForeColor = Color.FromArgb(235, 240, 246);
        button.Font = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(52, 64, 84);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 66, 92);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(23, 31, 43);
        button.Cursor = Cursors.Hand;
    }
}
