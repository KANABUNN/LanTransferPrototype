using System.ComponentModel;

namespace LanShared.Ui;

public sealed class ModernScrollHost : UserControl
{
    private const int ScrollBarThickness = 12;
    private const int WheelStep = 48;

    private readonly Panel _viewport = new();
    private readonly ModernScrollBar _verticalBar = new(ScrollBarOrientation.Vertical);
    private readonly ModernScrollBar _horizontalBar = new(ScrollBarOrientation.Horizontal);

    private Control? _content;
    private int _scrollX;
    private int _scrollY;

    public ModernScrollHost()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
        BackColor = Color.FromArgb(8, 12, 18);

        _viewport.Margin = Padding.Empty;
        _viewport.Padding = Padding.Empty;
        _viewport.BackColor = Color.Transparent;
        _viewport.Location = Point.Empty;
        _viewport.TabStop = false;
        _viewport.MouseWheel += (_, e) => HandleWheel(e);

        _verticalBar.Visible = false;
        _horizontalBar.Visible = false;
        _verticalBar.ValueChanged += (_, _) =>
        {
            _scrollY = _verticalBar.Value;
            UpdateContentLocation();
        };
        _horizontalBar.ValueChanged += (_, _) =>
        {
            _scrollX = _horizontalBar.Value;
            UpdateContentLocation();
        };

        Controls.Add(_viewport);
        Controls.Add(_verticalBar);
        Controls.Add(_horizontalBar);

        Resize += (_, _) => RefreshScrollState();
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Control? Content
    {
        get => _content;
        set => SetContent(value);
    }

    private void SetContent(Control? value)
    {
        if (ReferenceEquals(_content, value))
        {
            return;
        }

        if (_content is not null)
        {
            UnwireRecursive(_content);
            _content.Resize -= HandleContentSizeChanged;
            _viewport.Controls.Remove(_content);
        }

        _content = value;

        if (_content is not null)
        {
            _content.Margin = Padding.Empty;
            _content.Location = Point.Empty;
            _content.Resize += HandleContentSizeChanged;
            _viewport.Controls.Add(_content);
            WireRecursive(_content);
        }

        RefreshScrollState();
    }

    private void HandleContentSizeChanged(object? sender, EventArgs e)
    {
        RefreshScrollState();
    }

    private void WireRecursive(Control control)
    {
        control.MouseWheel += Content_MouseWheel;
        control.ControlAdded += Content_ControlAdded;
        control.ControlRemoved += Content_ControlRemoved;

        foreach (Control child in control.Controls)
        {
            WireRecursive(child);
        }
    }

    private void UnwireRecursive(Control control)
    {
        control.MouseWheel -= Content_MouseWheel;
        control.ControlAdded -= Content_ControlAdded;
        control.ControlRemoved -= Content_ControlRemoved;

        foreach (Control child in control.Controls)
        {
            UnwireRecursive(child);
        }
    }

    private void Content_ControlAdded(object? sender, ControlEventArgs e)
    {
        if (e.Control is not null)
        {
            WireRecursive(e.Control);
            RefreshScrollState();
        }
    }

    private void Content_ControlRemoved(object? sender, ControlEventArgs e)
    {
        if (e.Control is not null)
        {
            UnwireRecursive(e.Control);
            RefreshScrollState();
        }
    }

    private void Content_MouseWheel(object? sender, MouseEventArgs e)
    {
        HandleWheel(e);
    }

    private void HandleWheel(MouseEventArgs e)
    {
        if (!_verticalBar.Visible)
        {
            return;
        }

        int delta = e.Delta > 0 ? -WheelStep : WheelStep;
        _verticalBar.Value = _verticalBar.Value + delta;
    }

    private void RefreshScrollState()
    {
        Size client = ClientSize;
        if (client.Width <= 0 || client.Height <= 0)
        {
            return;
        }

        Size contentSize = GetContentSize();

        bool needVertical = contentSize.Height > client.Height;
        bool needHorizontal = contentSize.Width > client.Width;

        int viewportWidth = client.Width - (needVertical ? ScrollBarThickness : 0);
        int viewportHeight = client.Height - (needHorizontal ? ScrollBarThickness : 0);

        needVertical = contentSize.Height > viewportHeight;
        needHorizontal = contentSize.Width > viewportWidth;

        viewportWidth = Math.Max(0, client.Width - (needVertical ? ScrollBarThickness : 0));
        viewportHeight = Math.Max(0, client.Height - (needHorizontal ? ScrollBarThickness : 0));

        _viewport.Bounds = new Rectangle(0, 0, viewportWidth, viewportHeight);

        _verticalBar.Visible = needVertical;
        _horizontalBar.Visible = needHorizontal;

        if (needVertical)
        {
            _verticalBar.Bounds = new Rectangle(viewportWidth, 0, ScrollBarThickness, viewportHeight);
            _verticalBar.Maximum = Math.Max(0, contentSize.Height - 1);
            _verticalBar.LargeChange = Math.Max(1, viewportHeight);
            _verticalBar.SmallChange = WheelStep;
            _verticalBar.Value = Math.Clamp(_scrollY, 0, _verticalBar.MaxScrollableValue);
            _scrollY = _verticalBar.Value;
        }
        else
        {
            _scrollY = 0;
            _verticalBar.Value = 0;
        }

        if (needHorizontal)
        {
            _horizontalBar.Bounds = new Rectangle(0, viewportHeight, viewportWidth, ScrollBarThickness);
            _horizontalBar.Maximum = Math.Max(0, contentSize.Width - 1);
            _horizontalBar.LargeChange = Math.Max(1, viewportWidth);
            _horizontalBar.SmallChange = 36;
            _horizontalBar.Value = Math.Clamp(_scrollX, 0, _horizontalBar.MaxScrollableValue);
            _scrollX = _horizontalBar.Value;
        }
        else
        {
            _scrollX = 0;
            _horizontalBar.Value = 0;
        }

        UpdateContentLocation();
        Invalidate();
    }

    private Size GetContentSize()
    {
        if (_content is null)
        {
            return Size.Empty;
        }

        Size preferred = _content.GetPreferredSize(Size.Empty);
        return new Size(
            Math.Max(_content.Width, preferred.Width),
            Math.Max(_content.Height, preferred.Height));
    }

    private void UpdateContentLocation()
    {
        if (_content is null)
        {
            return;
        }

        _content.Location = new Point(-_scrollX, -_scrollY);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        HandleWheel(e);
        base.OnMouseWheel(e);
    }
}

internal enum ScrollBarOrientation
{
    Horizontal,
    Vertical,
}

internal sealed class ModernScrollBar : Control
{
    private readonly ScrollBarOrientation _orientation;
    private readonly Color _trackColor = Color.FromArgb(20, 28, 40);
    private readonly Color _thumbColor = Color.FromArgb(68, 96, 140);
    private readonly Color _thumbHoverColor = Color.FromArgb(92, 128, 184);
    private readonly Color _thumbPressedColor = Color.FromArgb(120, 156, 216);

    private bool _hovering;
    private bool _dragging;
    private int _dragPointerOffset;
    private int _maximum;
    private int _largeChange = 1;
    private int _smallChange = 16;
    private int _value;

    public ModernScrollBar(ScrollBarOrientation orientation)
    {
        _orientation = orientation;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
        TabStop = false;
        Cursor = orientation == ScrollBarOrientation.Vertical ? Cursors.Hand : Cursors.Hand;
        MinimumSize = orientation == ScrollBarOrientation.Vertical ? new Size(12, 24) : new Size(24, 12);
    }

    public event EventHandler? ValueChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            int next = Math.Max(0, value);
            if (_maximum == next)
            {
                return;
            }

            _maximum = next;
            Value = Math.Clamp(Value, 0, MaxScrollableValue);
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int LargeChange
    {
        get => _largeChange;
        set
        {
            int next = Math.Max(1, value);
            if (_largeChange == next)
            {
                return;
            }

            _largeChange = next;
            Value = Math.Clamp(Value, 0, MaxScrollableValue);
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SmallChange
    {
        get => _smallChange;
        set => _smallChange = Math.Max(1, value);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, 0, MaxScrollableValue);
            if (_value == clamped)
            {
                return;
            }

            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [Browsable(false)]
    public int MaxScrollableValue => Math.Max(0, Maximum - LargeChange + 1);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.Clear(_trackColor);
        Rectangle thumb = GetThumbBounds();
        if (thumb.Width <= 0 || thumb.Height <= 0)
        {
            return;
        }

        Color color = _dragging ? _thumbPressedColor : _hovering ? _thumbHoverColor : _thumbColor;
        using var brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, thumb);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovering = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (!_dragging)
        {
            _hovering = false;
            Invalidate();
        }

        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        Rectangle thumb = GetThumbBounds();
        if (thumb.Contains(e.Location))
        {
            _dragging = true;
            _dragPointerOffset = _orientation == ScrollBarOrientation.Vertical ? e.Y - thumb.Top : e.X - thumb.Left;
            Capture = true;
            Invalidate();
            return;
        }

        if (_orientation == ScrollBarOrientation.Vertical)
        {
            Value += e.Y < thumb.Top ? -LargeChange : LargeChange;
        }
        else
        {
            Value += e.X < thumb.Left ? -LargeChange : LargeChange;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging)
        {
            bool hit = GetThumbBounds().Contains(e.Location);
            if (_hovering != hit)
            {
                _hovering = hit;
                Invalidate();
            }
            return;
        }

        int trackLength = GetTrackLength();
        int thumbLength = GetThumbLength(trackLength);
        int movable = Math.Max(1, trackLength - thumbLength);
        int pointer = _orientation == ScrollBarOrientation.Vertical ? e.Y : e.X;
        int thumbPos = Math.Clamp(pointer - _dragPointerOffset, 0, movable);

        Value = MaxScrollableValue == 0 ? 0 : (int)Math.Round((double)thumbPos * MaxScrollableValue / movable);
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

    private Rectangle GetThumbBounds()
    {
        int trackLength = GetTrackLength();
        if (trackLength <= 0)
        {
            return Rectangle.Empty;
        }

        int thumbLength = GetThumbLength(trackLength);
        int movable = Math.Max(0, trackLength - thumbLength);
        int thumbPos = MaxScrollableValue <= 0 ? 0 : (int)Math.Round((double)Value * movable / MaxScrollableValue);

        return _orientation == ScrollBarOrientation.Vertical
            ? new Rectangle(0, thumbPos, Width, thumbLength)
            : new Rectangle(thumbPos, 0, thumbLength, Height);
    }

    private int GetTrackLength()
    {
        return _orientation == ScrollBarOrientation.Vertical ? Height : Width;
    }

    private int GetThumbLength(int trackLength)
    {
        if (Maximum <= 0)
        {
            return trackLength;
        }

        int length = (int)Math.Round((double)LargeChange / (Maximum + 1) * trackLength);
        return Math.Clamp(length, 28, trackLength);
    }
}
