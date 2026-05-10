using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace LanShared.Ui;

public static class ModernScrollPalette
{
    public static Color Background { get; } = Color.FromArgb(9, 12, 20);
    public static Color Track { get; } = Color.FromArgb(17, 24, 39);
    public static Color Thumb { get; } = Color.FromArgb(75, 85, 99);
    public static Color ThumbHover { get; } = Color.FromArgb(107, 114, 128);
    public static Color ThumbDisabled { get; } = Color.FromArgb(31, 41, 55);
}

public sealed class ModernScrollHost : UserControl
{
    private const int ScrollBarThickness = 12;
    private const int WheelStep = 60;

    private readonly TableLayoutPanel _layout;
    private readonly Panel _viewport;
    private readonly Panel _contentSurface;
    private readonly ModernScrollBar _verticalScrollBar;
    private readonly ModernScrollBar _horizontalScrollBar;
    private readonly Panel _corner;

    private Control? _content;
    private Size _minimumContentSize;
    private int _scrollX;
    private int _scrollY;

    public ModernScrollHost()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = ModernScrollPalette.Background;

        _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ModernScrollPalette.Background,
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScrollBarThickness));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ScrollBarThickness));

        _viewport = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ModernScrollPalette.Background,
        };

        _contentSurface = new Panel
        {
            Location = Point.Empty,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = ModernScrollPalette.Background,
        };
        _viewport.Controls.Add(_contentSurface);

        _verticalScrollBar = new ModernScrollBar(Orientation.Vertical)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        _verticalScrollBar.ValueChanged += (_, _) =>
        {
            _scrollY = _verticalScrollBar.Value;
            UpdateContentOffset();
        };

        _horizontalScrollBar = new ModernScrollBar(Orientation.Horizontal)
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        _horizontalScrollBar.ValueChanged += (_, _) =>
        {
            _scrollX = _horizontalScrollBar.Value;
            UpdateContentOffset();
        };

        _corner = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = ModernScrollPalette.Track,
        };

        _layout.Controls.Add(_viewport, 0, 0);
        _layout.Controls.Add(_verticalScrollBar, 1, 0);
        _layout.Controls.Add(_horizontalScrollBar, 0, 1);
        _layout.Controls.Add(_corner, 1, 1);
        Controls.Add(_layout);

        Resize += (_, _) => UpdateScrollMetrics();
        _viewport.Resize += (_, _) => UpdateScrollMetrics();
        MouseWheel += HandleForwardedMouseWheel;
        _viewport.MouseWheel += HandleForwardedMouseWheel;
        _contentSurface.MouseWheel += HandleForwardedMouseWheel;
        MouseEnter += (_, _) => Focus();
        _viewport.MouseEnter += (_, _) => Focus();
    }

    public void SetContent(Control content, Size minimumContentSize)
    {
        _contentSurface.Controls.Clear();

        _content = content;
        _minimumContentSize = minimumContentSize;

        content.Dock = DockStyle.Fill;
        content.Margin = Padding.Empty;
        content.BackColor = ModernScrollPalette.Background;

        _contentSurface.Controls.Add(content);
        WireMouseWheel(content);
        UpdateScrollMetrics();
    }

    private void WireMouseWheel(Control control)
    {
        if (ShouldForwardMouseWheel(control))
        {
            control.MouseWheel += HandleForwardedMouseWheel;
        }

        control.ControlAdded += (_, e) =>
        {
            if (e.Control is not null)
            {
                WireMouseWheel(e.Control);
            }
        };

        foreach (Control child in control.Controls)
        {
            WireMouseWheel(child);
        }
    }

    private static bool ShouldForwardMouseWheel(Control control)
    {
        return control is not TextBoxBase
            && control is not ListBox
            && control is not ComboBox
            && control is not NumericUpDown;
    }

    private void HandleForwardedMouseWheel(object? sender, MouseEventArgs e)
    {
        if (ModifierKeys.HasFlag(Keys.Shift))
        {
            ScrollBy(-e.Delta / 120 * WheelStep, 0);
        }
        else
        {
            ScrollBy(0, -e.Delta / 120 * WheelStep);
        }
    }

    private void ScrollBy(int dx, int dy)
    {
        int maxX = Math.Max(0, _contentSurface.Width - _viewport.ClientSize.Width);
        int maxY = Math.Max(0, _contentSurface.Height - _viewport.ClientSize.Height);

        _scrollX = Math.Clamp(_scrollX + dx, 0, maxX);
        _scrollY = Math.Clamp(_scrollY + dy, 0, maxY);

        _horizontalScrollBar.Value = _scrollX;
        _verticalScrollBar.Value = _scrollY;
        UpdateContentOffset();
    }

    private void UpdateScrollMetrics()
    {
        if (_viewport.ClientSize.Width <= 0 || _viewport.ClientSize.Height <= 0)
        {
            return;
        }

        int contentWidth = Math.Max(_minimumContentSize.Width, _viewport.ClientSize.Width);
        int contentHeight = Math.Max(_minimumContentSize.Height, _viewport.ClientSize.Height);

        if (_content is not null)
        {
            Size preferred = _content.GetPreferredSize(Size.Empty);
            contentWidth = Math.Max(contentWidth, preferred.Width);
            contentHeight = Math.Max(contentHeight, preferred.Height);
        }

        _contentSurface.Size = new Size(contentWidth, contentHeight);

        int maxX = Math.Max(0, contentWidth - _viewport.ClientSize.Width);
        int maxY = Math.Max(0, contentHeight - _viewport.ClientSize.Height);

        _scrollX = Math.Clamp(_scrollX, 0, maxX);
        _scrollY = Math.Clamp(_scrollY, 0, maxY);

        _horizontalScrollBar.Maximum = maxX;
        _horizontalScrollBar.LargeChange = Math.Max(1, _viewport.ClientSize.Width);
        _horizontalScrollBar.Value = _scrollX;

        _verticalScrollBar.Maximum = maxY;
        _verticalScrollBar.LargeChange = Math.Max(1, _viewport.ClientSize.Height);
        _verticalScrollBar.Value = _scrollY;

        UpdateContentOffset();
    }

    private void UpdateContentOffset()
    {
        _contentSurface.Location = new Point(-_scrollX, -_scrollY);
    }
}

public sealed class ModernScrollBar : Control
{
    private const int MinimumThumbLength = 32;

    private readonly Orientation _orientation;
    private bool _dragging;
    private bool _hovering;
    private int _dragStartMouse;
    private int _dragStartValue;
    private int _maximum;
    private int _largeChange = 1;
    private int _smallChange = 24;
    private int _value;

    public ModernScrollBar(Orientation orientation)
    {
        _orientation = orientation;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);

        BackColor = ModernScrollPalette.Track;
        TabStop = false;
        Cursor = Cursors.Hand;
    }

    public event EventHandler? ValueChanged;

    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(0, value);
            Value = _value;
            Invalidate();
        }
    }

    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int LargeChange
    {
        get => _largeChange;
        set
        {
            _largeChange = Math.Max(1, value);
            Invalidate();
        }
    }

    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SmallChange
    {
        get => _smallChange;
        set => _smallChange = Math.Max(1, value);
    }

    [Browsable(false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int newValue = Math.Clamp(value, 0, _maximum);
            if (_value == newValue)
            {
                return;
            }

            _value = newValue;
            ValueChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        using var trackBrush = new SolidBrush(ModernScrollPalette.Track);
        e.Graphics.FillRectangle(trackBrush, ClientRectangle);

        if (_maximum <= 0)
        {
            using var disabledBrush = new SolidBrush(ModernScrollPalette.ThumbDisabled);
            e.Graphics.FillRectangle(disabledBrush, GetDisabledThumbBounds());
            return;
        }

        using var thumbBrush = new SolidBrush(_dragging || _hovering
            ? ModernScrollPalette.ThumbHover
            : ModernScrollPalette.Thumb);

        Rectangle thumb = GetThumbBounds();
        e.Graphics.FillRectangle(thumbBrush, thumb);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (_maximum <= 0)
        {
            return;
        }

        Rectangle thumb = GetThumbBounds();
        int mouse = GetMouseAxis(e.Location);

        if (thumb.Contains(e.Location))
        {
            _dragging = true;
            _dragStartMouse = mouse;
            _dragStartValue = _value;
            Capture = true;
            Invalidate();
            return;
        }

        int direction = mouse < GetThumbStart(thumb) ? -1 : 1;
        Value += direction * Math.Max(SmallChange, LargeChange / 2);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_maximum <= 0)
        {
            return;
        }

        bool hovering = GetThumbBounds().Contains(e.Location);
        if (_hovering != hovering)
        {
            _hovering = hovering;
            Invalidate();
        }

        if (!_dragging)
        {
            return;
        }

        int trackLength = GetTrackLength();
        int thumbLength = GetThumbLength();
        int travel = Math.Max(1, trackLength - thumbLength);
        int delta = GetMouseAxis(e.Location) - _dragStartMouse;
        Value = _dragStartValue + (int)Math.Round(delta * (double)_maximum / travel);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hovering)
        {
            _hovering = false;
            Invalidate();
        }
    }

    private Rectangle GetThumbBounds()
    {
        int trackLength = GetTrackLength();
        int thickness = _orientation == Orientation.Vertical ? Width : Height;
        int thumbLength = GetThumbLength();
        int travel = Math.Max(1, trackLength - thumbLength);
        int start = _maximum <= 0 ? 0 : (int)Math.Round(_value * (double)travel / _maximum);

        return _orientation == Orientation.Vertical
            ? new Rectangle(2, start + 2, Math.Max(1, thickness - 4), Math.Max(1, thumbLength - 4))
            : new Rectangle(start + 2, 2, Math.Max(1, thumbLength - 4), Math.Max(1, thickness - 4));
    }

    private Rectangle GetDisabledThumbBounds()
    {
        int thickness = _orientation == Orientation.Vertical ? Width : Height;

        return _orientation == Orientation.Vertical
            ? new Rectangle(2, 2, Math.Max(1, thickness - 4), Math.Min(48, Math.Max(1, Height - 4)))
            : new Rectangle(2, 2, Math.Min(48, Math.Max(1, Width - 4)), Math.Max(1, thickness - 4));
    }

    private int GetThumbLength()
    {
        int trackLength = Math.Max(1, GetTrackLength());
        double visibleRatio = LargeChange / (double)(LargeChange + Maximum);
        return Math.Clamp((int)Math.Round(trackLength * visibleRatio), MinimumThumbLength, trackLength);
    }

    private int GetTrackLength()
    {
        return Math.Max(1, _orientation == Orientation.Vertical ? Height : Width);
    }

    private int GetMouseAxis(Point point)
    {
        return _orientation == Orientation.Vertical ? point.Y : point.X;
    }

    private int GetThumbStart(Rectangle thumb)
    {
        return _orientation == Orientation.Vertical ? thumb.Top : thumb.Left;
    }
}
