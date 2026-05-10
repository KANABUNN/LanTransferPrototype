namespace LanShared.Ui;

public static class ModernScrollPalette
{
    public static readonly Color Background = Color.FromArgb(8, 12, 18);
    public static readonly Color Surface = Color.FromArgb(14, 20, 30);
    public static readonly Color SurfaceRaised = Color.FromArgb(20, 28, 40);
    public static readonly Color Border = Color.FromArgb(42, 54, 72);
    public static readonly Color Text = Color.FromArgb(230, 236, 245);
    public static readonly Color MutedText = Color.FromArgb(150, 164, 184);
    public static readonly Color Accent = Color.FromArgb(82, 130, 255);
    public static readonly Color AccentHover = Color.FromArgb(104, 150, 255);
    public static readonly Color Danger = Color.FromArgb(245, 85, 105);
}

public static class ModernScrollHostCompatibilityExtensions
{
    public static void SetContent(this ModernScrollHost host, Control content, Size contentSize)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(content);

        if (contentSize.Width > 0 || contentSize.Height > 0)
        {
            content.MinimumSize = new Size(
                Math.Max(content.MinimumSize.Width, contentSize.Width),
                Math.Max(content.MinimumSize.Height, contentSize.Height));

            if (content.Width < contentSize.Width)
            {
                content.Width = contentSize.Width;
            }

            if (content.Height < contentSize.Height)
            {
                content.Height = contentSize.Height;
            }
        }

        host.Content = content;
    }
}
