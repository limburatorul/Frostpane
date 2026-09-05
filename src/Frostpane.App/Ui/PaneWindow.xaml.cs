using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Frostpane.Interop;
using Frostpane.Model;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;

namespace Frostpane.Ui;

internal enum Grip { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Which screen edge a pane was dropped against, if any.</summary>
internal enum SnapEdge { None, Top, Bottom }

/// <summary>
/// One pane, as a top-level window pinned to the bottom of the Z-order.
///
/// Top-level is the only placement that survives a GPU-composited wallpaper such as Wallpaper
/// Engine — a window parented into the desktop hierarchy is simply never drawn while one runs.
/// The cost is that the shell's icons can never appear on top of a pane, so the icons a pane owns
/// are parked off-screen and drawn here instead.
/// </summary>
public partial class PaneWindow : Window
{
    private static readonly IntPtr HWND_BOTTOM = new(1);

    private const int WM_WINDOWPOSCHANGING = 0x0046;

    /// <summary>Pointer travel, in pixels, before a press on a tile becomes a drag.</summary>
    private const int DragThreshold = 6;

    private const double ResizeEdge = 6;

    /// <summary>How close to a screen edge a dragged pane latches onto it, in pixels.</summary>
    private const int SnapDistance = 28;

    private const double RollDuration = 170;

    private Grip _grip;
    private POINT _grabCursor;
    private RECT _grabBounds;

    private IconTile? _pressedTile;
    private POINT _pressCursor;
    private bool _draggingTile;

    private DispatcherTimer? _roll;
    private bool _blurEnabled = true;

    private readonly DispatcherTimer _hover;
    private bool _rolled;
    private bool _peeking;

    internal ObservableCollection<IconTile> Items { get; } = new();

    public PaneWindow()
    {
        InitializeComponent();
        Tiles.ItemsSource = Items;

        _hover = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(280) };
        _hover.Tick += (_, _) => { _hover.Stop(); Peek(IsMouseOver); };

    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public string Label
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>Title bar height in physical pixels — the height of a rolled-up pane.</summary>
    public int TitleBarHeightPixels => (int)Math.Round(TitleBar.Height * VisualTreeHelper.GetDpi(this).DpiScaleY);

    /// <summary>The edge the pane latched onto during the last move.</summary>
    internal SnapEdge Edge { get; private set; }

    // ---------- appearance ----------

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(PaneWindow), new PropertyMetadata(36.0));

    public static readonly DependencyProperty TileSizeProperty =
        DependencyProperty.Register(nameof(TileSize), typeof(double), typeof(PaneWindow), new PropertyMetadata(86.0));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double TileSize
    {
        get => (double)GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }

    /// <summary>
    /// Applies the shared appearance. The dock edge matters: a pane latched onto the top of the
    /// screen should meet that edge squarely, so only its free corners stay rounded.
    /// </summary>
    internal void ApplyAppearance(Settings settings, SnapEdge dock)
    {
        Glass.Opacity = Math.Clamp(settings.BackgroundOpacity, 20, 100) / 100.0;

        // Capped well below opaque: a tint that covers the blurred sample defeats the point.
        Tint.Background = new SolidColorBrush(
            Tinted(settings.TintColor, Math.Clamp(settings.TintStrength, 0, 60)));

        double radius = Math.Clamp(settings.CornerRadius, 0, 20);
        var corners = dock switch
        {
            SnapEdge.Top => new CornerRadius(0, 0, radius, radius),
            SnapEdge.Bottom => new CornerRadius(radius, radius, 0, 0),
            _ => new CornerRadius(radius),
        };
        Backdrop.CornerRadius = corners;
        Tint.CornerRadius = corners;
        Frame.CornerRadius = corners;
        TitleBar.CornerRadius = new CornerRadius(corners.TopLeft, corners.TopRight, 0, 0);

        Frame.BorderThickness = new Thickness(Math.Clamp(settings.BorderThickness, 0, 4));
        Frame.BorderBrush = new SolidColorBrush(
            Tinted(settings.BorderColor, Math.Clamp(settings.BorderStrength, 0, 100)));

        TitleBar.Height = Math.Clamp(settings.TitleBarHeight, 16, 40);
        TitleBar.Background = new SolidColorBrush(
            Tinted(settings.TitleBarColor, Math.Clamp(settings.TitleBarStrength, 0, 100)));
        TitleText.FontSize = Math.Clamp(TitleBar.Height - 11, 9, 16);

        _blurEnabled = settings.BlurWallpaper;
        if (!_blurEnabled) Backdrop.Background = null;

        PeekOnHover = settings.PeekOnHover;
        IconSize = Math.Clamp(settings.IconSize, 24, 64);
        TileSize = IconSize + 50;
    }

    private static Color Tinted(string hex, int strength)
    {
        var color = ParseColor(hex);
        color.A = (byte)Math.Round(strength * 255 / 100.0);
        return color;
    }

    private static Color ParseColor(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value)!; }
        catch (Exception) { return Color.FromRgb(0x14, 0x14, 0x19); }
    }

    /// <summary>Paints the blurred sample of the wallpaper behind the pane's contents.</summary>
    internal void SetBackdrop(ImageSource? image) =>
        Backdrop.Background = image is null || !_blurEnabled
            ? null
            : new ImageBrush(image) { Stretch = Stretch.Fill };

    // ---------- window ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        RefuseActivation();
        HwndSource.FromHwnd(Handle)?.AddHook(KeepAtBottom);
        Win32.SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0,
                           Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>Clicking a pane must never pull focus away from whatever the user was doing.</summary>
    private void RefuseActivation()
    {
        long ex = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
    }

    /// <summary>
    /// Rewrites every Z-order change to "bottom". A pane belongs on the desktop, so it must never
    /// rise above an ordinary window, however it came to be repositioned.
    /// </summary>
    private static IntPtr KeepAtBottom(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.hwndInsertAfter = HWND_BOTTOM;
            pos.flags &= ~Win32.SWP_NOZORDER;
            System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
        }
        return IntPtr.Zero;
    }

    /// <summary>Places the window in physical screen pixels, bypassing WPF's DPI-scaled Left/Top.</summary>
    public void SetBounds(int x, int y, int width, int height) =>
        Win32.SetWindowPos(Handle, IntPtr.Zero, x, y, width, height,
                           Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);

    public RECT Bounds
    {
        get { Win32.GetWindowRect(Handle, out var r); return r; }
    }

    public void SetRolledUp(bool rolled)
    {
        _rolled = rolled;
        _peeking = false;
        SetRolledUpVisual(rolled);
    }

    /// <summary>
    /// Shows or hides the contents without changing whether the pane counts as rolled up. Used by
    /// the hover peek, which is a look at a rolled-up pane rather than a change to it.
    /// </summary>
    internal void SetRolledUpVisual(bool rolled) =>
        Body.Visibility = rolled ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Whether hovering a rolled-up pane should open it for a look.</summary>
    internal bool PeekOnHover { get; set; } = true;

    /// <summary>Raised to open a rolled-up pane while the pointer is on it, and to close it after.</summary>
    internal event Action<bool>? PeekRequested;

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (PeekOnHover && _rolled && !_peeking) _hover.Start();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover.Stop();
        if (_peeking) Peek(false);
    }

    private void Peek(bool open)
    {
        if (_grip != Grip.None) return;          // never fight a drag
        if (open == _peeking || !_rolled) return;

        _peeking = open;
        PeekRequested?.Invoke(open);
    }

    /// <summary>Slides the pane to a new vertical position and height. Used for rolling up.</summary>
    internal void AnimateTo(int y, int height, Action? completed = null)
    {
        _roll?.Stop();

        var from = Bounds;
        var clock = Stopwatch.StartNew();

        _roll = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / RollDuration);
            double eased = 1 - Math.Pow(1 - t, 3);

            SetBounds(from.Left,
                      (int)Math.Round(from.Top + (y - from.Top) * eased),
                      from.Width,
                      (int)Math.Round(from.Height + (height - from.Height) * eased));

            if (t < 1) return;
            _roll!.Stop();
            completed?.Invoke();
        }, Dispatcher);
    }

    /// <summary>
    /// Raised when the user double-clicks the pane's name.
    ///
    /// Editing in place is not an option: a pane is a WS_EX_NOACTIVATE window pinned to the
    /// bottom of the Z-order, so it cannot hold keyboard focus — a text box put there loses focus
    /// the moment it gets it. The name is edited in a dialog instead.
    /// </summary>
    internal event Action? RenameRequested;

    // ---------- pointer ----------

    /// <summary>Raised when a move or resize gesture finishes, with the new screen bounds.</summary>
    internal event Action<RECT>? BoundsChanged;

    internal event Action? RollUpToggled;

    /// <summary>
    /// Raised on right-click, with everything needed to place a menu: the screen point for
    /// commands that act on a location, and the point inside this window for WPF's placement.
    /// </summary>
    internal event Action<POINT, Point, IconTile?>? ContextMenuRequested;

    internal event Action<IconTile>? ItemActivated;

    /// <summary>Raised when a tile is dropped, with the screen point it was released at.</summary>
    internal event Action<IconTile, POINT>? ItemDropped;

    /// <summary>Raised when files are dropped onto the pane from the shell.</summary>
    internal event Action<string[]>? FilesDropped;

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            FilesDropped?.Invoke(paths);
        e.Handled = true;
    }

    private Grip GripAt(Point p)
    {
        bool left = p.X < ResizeEdge;
        bool right = ActualWidth - p.X < ResizeEdge;
        bool top = p.Y < ResizeEdge;
        bool bottom = ActualHeight - p.Y < ResizeEdge;

        if (Body.Visibility == Visibility.Visible)
        {
            if (top && left) return Grip.TopLeft;
            if (top && right) return Grip.TopRight;
            if (bottom && left) return Grip.BottomLeft;
            if (bottom && right) return Grip.BottomRight;
            if (left) return Grip.Left;
            if (right) return Grip.Right;
            if (bottom) return Grip.Bottom;
            if (top) return Grip.Top;
        }

        return p.Y < TitleBar.Height ? Grip.Move : Grip.None;
    }

    private static Cursor CursorFor(Grip grip) => grip switch
    {
        Grip.Left or Grip.Right => Cursors.SizeWE,
        Grip.Top or Grip.Bottom => Cursors.SizeNS,
        Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
        Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (TileAt(e) is { } tile)
        {
            _pressedTile = tile;
            _pressCursor = Win32.CursorPosition;

            if (e.ClickCount == 2)
            {
                _pressedTile = null;
                ItemActivated?.Invoke(tile);
                e.Handled = true;
            }
            return;
        }

        var grip = GripAt(e.GetPosition(this));
        if (grip == Grip.None) return;

        if (grip == Grip.Move && e.ClickCount == 2)
        {
            // The name renames; the rest of the bar rolls the pane up.
            if (ReferenceEquals(e.OriginalSource, TitleText)) RenameRequested?.Invoke();
            else RollUpToggled?.Invoke();
            e.Handled = true;
            return;
        }

        _grip = grip;
        _grabCursor = Win32.CursorPosition;
        _grabBounds = Bounds;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);

        if (_grip != Grip.None)
        {
            Drag(Win32.CursorPosition);
            return;
        }

        if (_pressedTile is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            var now = Win32.CursorPosition;
            if (!_draggingTile
                && (Math.Abs(now.X - _pressCursor.X) > DragThreshold || Math.Abs(now.Y - _pressCursor.Y) > DragThreshold))
            {
                _draggingTile = true;
                _pressedTile.Opacity = 0.35;
                CaptureMouse();
            }
            return;
        }

        Cursor = CursorFor(GripAt(e.GetPosition(this)));
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);

        if (_grip != Grip.None)
        {
            _grip = Grip.None;
            ReleaseMouseCapture();
            BoundsChanged?.Invoke(Bounds);
            e.Handled = true;
            return;
        }

        if (_draggingTile && _pressedTile is not null)
        {
            var tile = _pressedTile;
            tile.Opacity = 1.0;
            ReleaseMouseCapture();
            ItemDropped?.Invoke(tile, Win32.CursorPosition);
            e.Handled = true;
        }

        _pressedTile = null;
        _draggingTile = false;
    }

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonUp(e);

        ContextMenuRequested?.Invoke(Win32.CursorPosition, e.GetPosition(this), TileAt(e));
        e.Handled = true;
    }

    private void Drag(POINT cursor)
    {
        int dx = cursor.X - _grabCursor.X;
        int dy = cursor.Y - _grabCursor.Y;

        int left = _grabBounds.Left, top = _grabBounds.Top;
        int right = _grabBounds.Right, bottom = _grabBounds.Bottom;

        const int min = 160;

        if (_grip == Grip.Move)
        {
            left += dx; right += dx; top += dy; bottom += dy;
            SnapToScreenEdge(ref top, ref bottom);
        }
        else
        {
            Edge = SnapEdge.None;
            if (_grip is Grip.Left or Grip.TopLeft or Grip.BottomLeft) left = Math.Min(left + dx, right - min);
            if (_grip is Grip.Right or Grip.TopRight or Grip.BottomRight) right = Math.Max(right + dx, left + min);
            if (_grip is Grip.Top or Grip.TopLeft or Grip.TopRight) top = Math.Min(top + dy, bottom - min / 2);
            if (_grip is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight) bottom = Math.Max(bottom + dy, top + min / 2);
        }

        SetBounds(left, top, right - left, bottom - top);
    }

    /// <summary>Latches the pane onto the top or bottom edge of the screen it is being dragged on.</summary>
    private void SnapToScreenEdge(ref int top, ref int bottom)
    {
        Edge = SnapEdge.None;

        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((_grabBounds.Left + _grabBounds.Right) / 2, top));
        var area = screen.WorkingArea;
        int height = bottom - top;

        if (Math.Abs(top - area.Top) <= SnapDistance)
        {
            top = area.Top;
            bottom = top + height;
            Edge = SnapEdge.Top;
        }
        else if (Math.Abs(bottom - area.Bottom) <= SnapDistance)
        {
            bottom = area.Bottom;
            top = bottom - height;
            Edge = SnapEdge.Bottom;
        }
    }

    /// <summary>Walks up from the clicked element to the tile that owns it, if any.</summary>
    private static IconTile? TileAt(MouseEventArgs e)
    {
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: IconTile tile }) return tile;
        return null;
    }
}
