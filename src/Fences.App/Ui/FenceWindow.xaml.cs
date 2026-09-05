using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Fences.Interop;
using Point = System.Windows.Point;

namespace Fences.Ui;

internal enum Grip { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// One fence, as a top-level window pinned to the bottom of the Z-order.
///
/// Top-level is the only placement that survives a GPU-composited wallpaper such as Wallpaper
/// Engine — a window parented into the desktop hierarchy is simply never drawn while one runs.
/// The cost is that the shell's icons can never appear on top of a fence, so the icons a fence
/// owns are parked off-screen and drawn here instead.
/// </summary>
public partial class FenceWindow : Window
{
    private static readonly IntPtr HWND_BOTTOM = new(1);

    private const int WM_WINDOWPOSCHANGING = 0x0046;

    /// <summary>Pointer travel, in pixels, before a press on a tile becomes a drag.</summary>
    private const int DragThreshold = 6;

    private const double ResizeEdge = 6;

    private Grip _grip;
    private POINT _grabCursor;
    private RECT _grabBounds;

    private IconTile? _pressedTile;
    private POINT _pressCursor;
    private bool _draggingTile;

    internal ObservableCollection<IconTile> Items { get; } = new();

    public FenceWindow()
    {
        InitializeComponent();
        Tiles.ItemsSource = Items;
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public string Label
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>Raised when a move or resize gesture finishes, with the new screen bounds.</summary>
    internal event Action<RECT>? BoundsChanged;

    internal event Action? RollUpToggled;

    /// <summary>Raised on right-click, with the screen point and the tile under it, if any.</summary>
    internal event Action<POINT, IconTile?>? ContextMenuRequested;

    internal event Action<IconTile>? ItemActivated;

    /// <summary>Raised when a tile is dropped, with the screen point it was released at.</summary>
    internal event Action<IconTile, POINT>? ItemDropped;

    public void SetRolledUp(bool rolled) => Body.Visibility = rolled ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Paints a blurred sample of the wallpaper behind the fence contents.</summary>
    internal void SetBackdrop(ImageSource? image) =>
        Backdrop.Background = image is null ? null : new ImageBrush(image) { Stretch = Stretch.Fill };

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        long ex = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);

        HwndSource.FromHwnd(Handle)?.AddHook(KeepAtBottom);
        Win32.SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0,
                           Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Rewrites every Z-order change to "bottom". A fence belongs on the desktop, so it must
    /// never rise above an ordinary window, however it came to be repositioned.
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

    // ---------- pointer ----------

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
            RollUpToggled?.Invoke();
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
            Resize(Win32.CursorPosition);
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
        ContextMenuRequested?.Invoke(Win32.CursorPosition, TileAt(e));
        e.Handled = true;
    }

    private void Resize(POINT cursor)
    {
        int dx = cursor.X - _grabCursor.X;
        int dy = cursor.Y - _grabCursor.Y;

        int left = _grabBounds.Left, top = _grabBounds.Top;
        int right = _grabBounds.Right, bottom = _grabBounds.Bottom;

        const int min = 160;

        if (_grip == Grip.Move)
        {
            left += dx; right += dx; top += dy; bottom += dy;
        }
        else
        {
            if (_grip is Grip.Left or Grip.TopLeft or Grip.BottomLeft) left = Math.Min(left + dx, right - min);
            if (_grip is Grip.Right or Grip.TopRight or Grip.BottomRight) right = Math.Max(right + dx, left + min);
            if (_grip is Grip.Top or Grip.TopLeft or Grip.TopRight) top = Math.Min(top + dy, bottom - min / 2);
            if (_grip is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight) bottom = Math.Max(bottom + dy, top + min / 2);
        }

        SetBounds(left, top, right - left, bottom - top);
    }

    /// <summary>Walks up from the clicked element to the tile that owns it, if any.</summary>
    private static IconTile? TileAt(MouseEventArgs e)
    {
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: IconTile tile }) return tile;
        return null;
    }
}
