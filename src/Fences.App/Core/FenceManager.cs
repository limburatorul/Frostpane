using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Size = System.Windows.Size;
using Fences.Desktop;
using Fences.Interop;
using Fences.Model;
using Fences.Ui;

namespace Fences.Core;

/// <summary>
/// Keeps the fences, their windows and the shell's icons in agreement.
///
/// An icon a fence owns is parked far off-screen so the shell stops drawing it, and the fence
/// window draws it instead. Icons no fence owns are left alone: the shell still lays them out,
/// launches them and handles their drag and drop, exactly as on a bare desktop.
/// </summary>
internal sealed class FenceManager : IDisposable
{
    /// <summary>Where owned icons are parked. Far below any real monitor, and the shell keeps it.</summary>
    private const int ParkY = 30000;
    private const int ParkedThreshold = 20000;

    private const int IconPixels = 48;

    private readonly DesktopLayer _layer;
    private readonly DesktopIcons _icons = new();
    private readonly Dictionary<string, FenceWindow> _windows = new();
    private readonly Dictionary<string, ImageSource?> _imageCache = new();
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private readonly Layout _layout;
    private WallpaperCapture? _capture;
    private BitmapSource? _wallpaper;

    public FenceManager(DesktopLayer layer)
    {
        _layer = layer;
        _layout = FenceStore.Load();

        _icons.AllowFreePositioning();

        foreach (var fence in _layout.Fences) OpenWindow(fence);

        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _timer.Tick += (_, _) => Reconcile();
        _timer.Start();

        StartCapture();
        Reconcile();
    }

    private void StartCapture()
    {
        try
        {
            _capture = new WallpaperCapture(_layer.IconHost);
            _capture.FrameReady += OnWallpaperFrame;
        }
        catch (Exception)
        {
            // No capture means no blur; the fences stay plainly translucent, which still works.
            _capture = null;
        }
    }

    /// <summary>
    /// Rebuilds everything that was tied to the old shell process. Explorer can restart at any
    /// time — a crash, a settings change — taking its desktop window, its shell view and the
    /// capture bound to that window with it.
    /// </summary>
    private void RecoverFromShellRestart()
    {
        _layer.Refresh();
        if (!_layer.IsValid) return;

        _icons.Invalidate();
        _icons.AllowFreePositioning();

        if (_capture is not null)
        {
            _capture.FrameReady -= OnWallpaperFrame;
            _capture.Dispose();
            _capture = null;
        }
        StartCapture();

        foreach (var fence in _layout.Fences)
            if (_windows.TryGetValue(fence.Id, out var window))
                Place(fence, window);
    }

    // ---------- blurred backdrop ----------

    private void OnWallpaperFrame(WallpaperFrame frame) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () => ApplyWallpaper(frame));

    private void ApplyWallpaper(WallpaperFrame frame)
    {
        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null,
                                        frame.Pixels, frame.Width * 4);
        image.Freeze();
        _wallpaper = image;

        _wallpaperSource = new Size(frame.SourceWidth, frame.SourceHeight);
        foreach (var fence in _layout.Fences) ApplyBackdrop(fence);
    }

    private Size _wallpaperSource;

    /// <summary>Cuts the part of the blurred desktop that lies behind this fence.</summary>
    private void ApplyBackdrop(Fence fence)
    {
        if (_wallpaper is null || !_windows.TryGetValue(fence.Id, out var window)) return;

        double scaleX = _wallpaper.PixelWidth / _wallpaperSource.Width;
        double scaleY = _wallpaper.PixelHeight / _wallpaperSource.Height;

        int x = (int)Math.Floor(fence.X * scaleX);
        int y = (int)Math.Floor(fence.Y * scaleY);
        int width = (int)Math.Ceiling(fence.Width * scaleX);
        int height = (int)Math.Ceiling(fence.Height * scaleY);

        x = Math.Clamp(x, 0, _wallpaper.PixelWidth - 1);
        y = Math.Clamp(y, 0, _wallpaper.PixelHeight - 1);
        width = Math.Clamp(width, 1, _wallpaper.PixelWidth - x);
        height = Math.Clamp(height, 1, _wallpaper.PixelHeight - y);

        var crop = new CroppedBitmap(_wallpaper, new Int32Rect(x, y, width, height));
        crop.Freeze();
        window.SetBackdrop(crop);
    }

    /// <summary>Raised when a fence is right-clicked, with the screen point and the tile under it.</summary>
    public event Action<Fence, IconTile?, POINT>? ContextMenuRequested;

    // ---------- fences ----------

    public Fence Create(POINT desktopPoint, string label = "Fence", string? portalPath = null)
    {
        var fence = new Fence
        {
            Label = label,
            PortalPath = portalPath,
            X = desktopPoint.X,
            Y = desktopPoint.Y,
            Width = 520,
            Height = 360,
            ExpandedHeight = 360,
        };
        _layout.Fences.Add(fence);
        OpenWindow(fence);
        Save();
        Reconcile();
        return fence;
    }

    public void Remove(Fence fence)
    {
        ReleaseIcons(fence, forget: true);         // never leave icons parked with no fence to show them

        if (_windows.Remove(fence.Id, out var window)) window.Close();
        _layout.Fences.Remove(fence);
        Save();
    }

    public void Rename(Fence fence, string label)
    {
        fence.Label = label;
        if (_windows.TryGetValue(fence.Id, out var window)) window.Label = label;
        Save();
    }

    public void ToggleRollUp(Fence fence)
    {
        if (fence.RolledUp)
        {
            fence.RolledUp = false;
            fence.Height = fence.ExpandedHeight;
        }
        else
        {
            fence.ExpandedHeight = fence.Height;
            fence.RolledUp = true;
            fence.Height = TitleHeight(fence);
        }

        if (_windows.TryGetValue(fence.Id, out var window))
        {
            window.SetRolledUp(fence.RolledUp);
            Place(fence, window);
        }
        Save();
    }

    private int TitleHeight(Fence fence) =>
        _windows.TryGetValue(fence.Id, out var window)
            ? (int)Math.Round(30 * VisualTreeHelper.GetDpi(window).DpiScaleY)
            : 30;

    private void OpenWindow(Fence fence)
    {
        var window = new FenceWindow { Label = fence.Label };
        _windows[fence.Id] = window;

        window.BoundsChanged += bounds =>
        {
            var origin = _layer.Origin;
            fence.X = bounds.Left - origin.X;
            fence.Y = bounds.Top - origin.Y;
            fence.Width = bounds.Width;
            fence.Height = bounds.Height;
            if (!fence.RolledUp) fence.ExpandedHeight = fence.Height;
            ApplyBackdrop(fence);
            Save();
            Reconcile();
        };
        window.RollUpToggled += () => ToggleRollUp(fence);
        window.ContextMenuRequested += (pt, tile) => ContextMenuRequested?.Invoke(fence, tile, pt);
        window.ItemActivated += tile => InvokeVerb(fence, tile.Id, null);
        window.ItemDropped += (tile, pt) => DropItem(fence, tile, pt);

        window.Show();
        window.SetRolledUp(fence.RolledUp);
        Place(fence, window);
    }

    private void Place(Fence fence, FenceWindow window)
    {
        var origin = _layer.Origin;
        window.SetBounds(fence.X + origin.X, fence.Y + origin.Y, fence.Width, fence.Height);
    }

    // ---------- items ----------

    /// <summary>
    /// Runs a shell verb on an item. Desktop items go through the shell view so the user gets
    /// the real dialogs; portal items are plain paths and go through ShellExecute.
    /// </summary>
    public void InvokeVerb(Fence fence, string id, string? verb)
    {
        if (!fence.IsPortal)
        {
            WithIndex(id, index => _icons.Verb(index, verb));
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(id)
            {
                UseShellExecute = true,
                Verb = verb ?? string.Empty,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user cancelled an elevation prompt, or the file has no handler for the verb.
        }
    }

    private void WithIndex(string id, Action<int> action)
    {
        var icon = _icons.Snapshot().FirstOrDefault(i => i.Id == id);
        if (icon is not null) action(icon.Index);
    }

    /// <summary>Moves a dragged tile to whichever fence it was dropped on, or back to the desktop.</summary>
    private void DropItem(Fence from, IconTile tile, POINT screen)
    {
        // A portal mirrors a folder; dragging out of one would have to move files on disk.
        if (from.IsPortal) return;

        var point = _layer.ScreenToDesktop(screen);
        var target = FenceAt(point);

        if (ReferenceEquals(target, from))
        {
            Reorder(from, tile.Id, point);
        }
        else if (target is { IsPortal: false })
        {
            from.Items.Remove(tile.Id);
            if (!target.Items.Contains(tile.Id)) target.Items.Add(tile.Id);
        }
        else
        {
            from.Items.Remove(tile.Id);
            WithIndex(tile.Id, index => _icons.Move(new[] { (index, point) }));
        }

        Save();
        Reconcile();
    }

    /// <summary>Drops the item at the slot the pointer is over, so a fence can be ordered by hand.</summary>
    private void Reorder(Fence fence, string id, POINT point)
    {
        if (!_windows.TryGetValue(fence.Id, out var window)) return;

        int slot = SlotAt(fence, window, point);
        fence.Items.Remove(id);
        fence.Items.Insert(Math.Clamp(slot, 0, fence.Items.Count), id);
    }

    private int SlotAt(Fence fence, FenceWindow window, POINT point)
    {
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int tile = (int)Math.Round(90 * scale);          // tile box plus its margin
        int columns = Math.Max(1, (fence.Width - (int)(12 * scale)) / tile);

        int col = Math.Clamp((point.X - fence.X - (int)(6 * scale)) / tile, 0, columns - 1);
        int row = Math.Max(0, (point.Y - fence.Y - TitleHeight(fence) - (int)(4 * scale)) / tile);
        return row * columns + col;
    }

    private Fence? FenceAt(POINT desktopPoint) =>
        _layout.Fences.LastOrDefault(f => f.Contains(desktopPoint.X, desktopPoint.Y));

    // ---------- reconciliation ----------

    /// <summary>
    /// Adopts icons dropped onto a fence, parks every owned icon out of the shell's way, and
    /// refreshes what each fence window draws.
    /// </summary>
    public void Reconcile()
    {
        if (!_layer.IsValid) RecoverFromShellRestart();

        var icons = _icons.Snapshot();
        if (icons.Count == 0 && _layout.Fences.Count == 0) return;

        var byId = icons.ToDictionary(i => i.Id);
        var owned = new HashSet<string>(_layout.Fences.SelectMany(f => f.Items));
        bool changed = false;

        foreach (var icon in icons)
        {
            if (owned.Contains(icon.Id) || icon.Position.Y >= ParkedThreshold) continue;
            if (FenceAt(icon.Position) is not { IsPortal: false } fence) continue;
            fence.Items.Add(icon.Id);
            owned.Add(icon.Id);
            changed = true;
        }

        var moves = new List<(int, POINT)>();
        foreach (var fence in _layout.Fences)
        {
            if (fence.IsPortal) { RefreshPortal(fence); continue; }

            changed |= fence.Items.RemoveAll(id => !byId.ContainsKey(id)) > 0;

            for (int i = 0; i < fence.Items.Count; i++)
            {
                var icon = byId[fence.Items[i]];
                if (icon.Position.Y < ParkedThreshold) moves.Add((icon.Index, new POINT(i * 8, ParkY)));
            }

            Refresh(fence, byId);
        }

        RescueOrphans(icons, owned, moves);

        if (moves.Count > 0) _icons.Move(moves);
        if (changed) Save();
    }

    /// <summary>
    /// Brings back icons that are parked but belong to no fence. Without this a crash, or a
    /// layout file that failed to save, would leave icons parked off-screen and apparently gone.
    /// </summary>
    private void RescueOrphans(IReadOnlyList<DesktopIcon> icons, HashSet<string> owned, List<(int, POINT)> moves)
    {
        var orphans = icons.Where(i => i.Position.Y >= ParkedThreshold && !owned.Contains(i.Id)).ToList();
        if (orphans.Count == 0) return;

        var spacing = _icons.Spacing;
        var home = _layer.ScreenToDesktop(new POINT(0, 0));      // top-left of the primary monitor

        // Don't drop rescued icons on top of the ones the user still has out.
        var taken = new HashSet<(int, int)>();
        foreach (var icon in icons)
            if (icon.Position.Y < ParkedThreshold)
                taken.Add(((icon.Position.X - home.X) / spacing.X, (icon.Position.Y - home.Y) / spacing.Y));

        int column = 0, row = 0;
        foreach (var orphan in orphans)
        {
            while (taken.Contains((column, row)))
            {
                if (++row < 12) continue;
                row = 0;
                column++;
            }
            taken.Add((column, row));
            moves.Add((orphan.Index, new POINT(home.X + 24 + column * spacing.X, home.Y + 26 + row * spacing.Y)));
        }
    }

    /// <summary>Mirrors the portal's folder into its window.</summary>
    private void RefreshPortal(Fence fence)
    {
        if (!_windows.TryGetValue(fence.Id, out var window)) return;

        List<string> paths;
        try
        {
            paths = Directory.EnumerateFileSystemEntries(fence.PortalPath!)
                             .Where(path => !System.IO.Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(System.IO.Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                             .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;     // an offline drive or a folder we may not read; keep showing the last state
        }

        if (window.Items.Select(t => t.Id).SequenceEqual(paths)) return;

        var existing = window.Items.ToDictionary(t => t.Id);
        window.Items.Clear();

        foreach (var path in paths)
        {
            if (existing.TryGetValue(path, out var tile)) { window.Items.Add(tile); continue; }

            var fresh = new IconTile(path, DisplayName(path));
            if (!_imageCache.TryGetValue(path, out var image))
                _imageCache[path] = image = ShellImage.Load(path, IconPixels);
            fresh.Image = image;
            window.Items.Add(fresh);
        }
    }

    /// <summary>Names files the way Explorer does: shortcuts lose their .lnk suffix.</summary>
    private static string DisplayName(string path) =>
        System.IO.Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetFileNameWithoutExtension(path)
            : System.IO.Path.GetFileName(path);

    /// <summary>Rebuilds a fence window's tiles, reusing the ones already there.</summary>
    private void Refresh(Fence fence, Dictionary<string, DesktopIcon> byId)
    {
        if (!_windows.TryGetValue(fence.Id, out var window)) return;

        var wanted = fence.Items.Where(byId.ContainsKey).ToList();
        if (window.Items.Select(t => t.Id).SequenceEqual(wanted)) return;

        var existing = window.Items.ToDictionary(t => t.Id);
        window.Items.Clear();

        foreach (var id in wanted)
        {
            if (existing.TryGetValue(id, out var tile))
            {
                window.Items.Add(tile);
                continue;
            }

            var fresh = new IconTile(id, byId[id].Name);
            if (!_imageCache.TryGetValue(id, out var image))
                _imageCache[id] = image = ShellImage.Load(id, IconPixels);
            fresh.Image = image;
            window.Items.Add(fresh);
        }
    }

    /// <summary>
    /// Brings a fence's icons back onto the desktop, laid out where the fence is.
    /// <paramref name="forget"/> also empties the fence; without it the fence keeps its contents
    /// and simply re-parks them next time the app runs.
    /// </summary>
    private void ReleaseIcons(Fence fence, bool forget)
    {
        if (fence.IsPortal) return;

        var byId = _icons.Snapshot().ToDictionary(i => i.Id);
        var spacing = _icons.Spacing;
        var moves = new List<(int, POINT)>();

        for (int i = 0; i < fence.Items.Count; i++)
        {
            if (!byId.TryGetValue(fence.Items[i], out var icon)) continue;
            moves.Add((icon.Index, new POINT(fence.X + i % 4 * spacing.X, fence.Y + i / 4 * spacing.Y)));
        }

        if (forget) fence.Items.Clear();
        if (moves.Count > 0) _icons.Move(moves);
    }

    /// <summary>
    /// Puts every icon back on the desktop before the app goes away, so nothing is left parked
    /// off-screen looking deleted. Fence membership is kept, so the layout survives a restart.
    /// </summary>
    public void ReleaseIconsForShutdown()
    {
        foreach (var fence in _layout.Fences) ReleaseIcons(fence, forget: false);
        Save();
    }

    /// <summary>Empties every fence back onto the desktop — the tray command.</summary>
    public void ReleaseAllIcons()
    {
        foreach (var fence in _layout.Fences) ReleaseIcons(fence, forget: true);

        var byId = _icons.Snapshot().ToDictionary(i => i.Id);
        foreach (var fence in _layout.Fences) Refresh(fence, byId);
        Save();
    }

    public void Save() => FenceStore.Save(_layout);

    public void Dispose()
    {
        _timer.Stop();
        _capture?.Dispose();
        foreach (var window in _windows.Values) window.Close();
        _windows.Clear();
    }
}
