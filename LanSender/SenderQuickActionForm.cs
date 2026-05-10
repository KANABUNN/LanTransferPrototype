using System.Drawing;
using System.Runtime.InteropServices;

namespace LanSender;

internal sealed class SenderQuickActionForm : Form
{
    private readonly Func<Task> _shareScreenAction;
    private readonly Func<Task> _openBrowserAction;
    private readonly Func<bool> _isSharingProvider;
    private readonly Button _shareButton = new();
    private readonly Button _browserButton = new();

    public SenderQuickActionForm(
        Func<Task> shareScreenAction,
        Func<Task> openBrowserAction,
        Func<bool> isSharingProvider)
    {
        _shareScreenAction = shareScreenAction;
        _openBrowserAction = openBrowserAction;
        _isSharingProvider = isSharingProvider;

        Text = "LAN Sender Quick Actions";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Width = 238;
        Height = 118;
        BackColor = Color.FromArgb(8, 12, 18);
        Padding = new Padding(10);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = Color.FromArgb(8, 12, 18),
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        ConfigureButton(_shareButton, "画面を共有する");
        ConfigureButton(_browserButton, "ブラウザを開く");

        _shareButton.Click += async (_, _) => await RunActionAsync(_shareScreenAction);
        _browserButton.Click += async (_, _) => await RunActionAsync(_openBrowserAction);

        layout.Controls.Add(_shareButton, 0, 0);
        layout.Controls.Add(_browserButton, 0, 1);
        Controls.Add(layout);

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

    private async Task RunActionAsync(Func<Task> action)
    {
        _shareButton.Enabled = false;
        _browserButton.Enabled = false;

        try
        {
            await action();
        }
        finally
        {
            UpdateShareButtonText();
            _shareButton.Enabled = true;
            _browserButton.Enabled = true;
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
        button.Margin = new Padding(0, 0, 0, 8);
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
