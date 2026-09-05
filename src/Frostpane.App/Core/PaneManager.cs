using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Size = System.Windows.Size;
using Frostpane.Desktop;
using Frostpane.Interop;
using Frostpane.Model;
using Frostpane.Ui;

namespace Frostpane.Core;

/// <summary>
/// Keeps the panes, their windows and the shell's icons in agreement.
///
/// An icon a pane owns is parked far off-screen so the shell stops drawing it, and the pane
/// window draws it instead. Icons no pane owns are left alone: the shell still lays them out,
/// launches them and handles their drag and drop, exactly as on a bare desktop.
/// </summary>
internal sealed class PaneManager : IDisposable
{
    /// <summary>Where owned icons are parked. Far below any real monitor, and the shell keeps it.</summary>
    private const int ParkY = 30000;
    private const int ParkedThreshold = 20000;

    private const int IconPixels = 48;

    private readonly DesktopLayer _layer;
    private readonly DesktopIcons _icons = new();
    private readonly Dictionary<string, PaneWindow> _windows = new();
    private readonly Dictionary<string, ImageSource?> _imageCache = new();
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private readonly Layout _layout;
    private WallpaperCapture? _capture;
    private BitmapSource? _wallpaper;

    public PaneManager(DesktopLayer layer)
    {
        _layer = layer;
        _layout = PaneStore.Load();

        _icons.AllowFreePositioning();

        foreach (var pane in _layout.Panes) OpenWindow(pane);

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
            // No capture means no blur; the panes stay plainly translucent, which still works.
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

        foreach (var pane in _layout.Panes)
            if (_windows.TryGetValue(pane.Id, out var window))
                Place(pane, window);
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
        foreach (var pane in _layout.Panes) ApplyBackdrop(pane);
    }

    private Size _wallpaperSource;

    /// <summary>Cuts the part of the blurred desktop that lies behind this pane.</summary>
    private void ApplyBackdrop(Pane pane)
    {
        if (_wallpaper is null || !_windows.TryGetValue(pane.Id, out var window)) return;

        double scaleX = _wallpaper.PixelWidth / _wallpaperSource.Width;
        double scaleY = _wallpaper.PixelHeight / _wallpaperSource.Height;

        int x = (int)Math.Floor(pane.X * scaleX);
        int y = (int)Math.Floor(pane.Y * scaleY);
        int width = (int)Math.Ceiling(pane.Width * scaleX);
        int height = (int)Math.Ceiling(pane.Height * scaleY);

        x = Math.Clamp(x, 0, _wallpaper.PixelWidth - 1);
        y = Math.Clamp(y, 0, _wallpaper.PixelHeight - 1);
        width = Math.Clamp(width, 1, _wallpaper.PixelWidth - x);
        height = Math.Clamp(height, 1, _wallpaper.PixelHeight - y);

        var crop = new CroppedBitmap(_wallpaper, new Int32Rect(x, y, width, height));
        crop.Freeze();
        window.SetBackdrop(crop);
    }

    /// <summary>The release the user declined, so the app stops offering it.</summary>
    public string? SkippedUpdate
    {
        get => _layout.SkippedUpdate;
        set { _layout.SkippedUpdate = value; Save(); }
    }

    /// <summary>Raised when a pane is right-clicked, with the screen point and the tile under it.</summary>
    public event Action<Pane, IconTile?, POINT>? ContextMenuRequested;

    // ---------- panes ----------

    public Pane Create(POINT desktopPoint, string label = "Panou", string? portalPath = null)
    {
        var pane = new Pane
        {
            Label = label,
            PortalPath = portalPath,
            X = desktopPoint.X,
            Y = desktopPoint.Y,
            Width = 520,
            Height = 360,
            ExpandedHeight = 360,
        };
        _layout.Panes.Add(pane);
        OpenWindow(pane);
        Save();
        Reconcile();
        return pane;
    }

    public void Remove(Pane pane)
    {
        ReleaseIcons(pane, forget: true);         // never leave icons parked with no pane to show them

        if (_windows.Remove(pane.Id, out var window)) window.Close();
        _layout.Panes.Remove(pane);
        Save();
    }

    public void Rename(Pane pane, string label)
    {
        pane.Label = label;
        if (_windows.TryGetValue(pane.Id, out var window)) window.Label = label;
        Save();
    }

    public void ToggleRollUp(Pane pane)
    {
        if (pane.RolledUp)
        {
            pane.RolledUp = false;
            pane.Height = pane.ExpandedHeight;
        }
        else
        {
            pane.ExpandedHeight = pane.Height;
            pane.RolledUp = true;
            pane.Height = TitleHeight(pane);
        }

        if (_windows.TryGetValue(pane.Id, out var window))
        {
            window.SetRolledUp(pane.RolledUp);
            Place(pane, window);
        }
        Save();
    }

    private int TitleHeight(Pane pane) =>
        _windows.TryGetValue(pane.Id, out var window)
            ? (int)Math.Round(30 * VisualTreeHelper.GetDpi(window).DpiScaleY)
            : 30;

    private void OpenWindow(Pane pane)
    {
        var window = new PaneWindow { Label = pane.Label };
        _windows[pane.Id] = window;

        window.BoundsChanged += bounds =>
        {
            var origin = _layer.Origin;
            pane.X = bounds.Left - origin.X;
            pane.Y = bounds.Top - origin.Y;
            pane.Width = bounds.Width;
            pane.Height = bounds.Height;
            if (!pane.RolledUp) pane.ExpandedHeight = pane.Height;
            ApplyBackdrop(pane);
            Save();
            Reconcile();
        };
        window.RollUpToggled += () => ToggleRollUp(pane);
        window.ContextMenuRequested += (pt, tile) => ContextMenuRequested?.Invoke(pane, tile, pt);
        window.ItemActivated += tile => InvokeVerb(pane, tile.Id, null);
        window.ItemDropped += (tile, pt) => DropItem(pane, tile, pt);

        window.Show();
        window.SetRolledUp(pane.RolledUp);
        Place(pane, window);
    }

    private void Place(Pane pane, PaneWindow window)
    {
        var origin = _layer.Origin;
        window.SetBounds(pane.X + origin.X, pane.Y + origin.Y, pane.Width, pane.Height);
    }

    // ---------- items ----------

    /// <summary>
    /// Runs a shell verb on an item. Desktop items go through the shell view so the user gets
    /// the real dialogs; portal items are plain paths and go through ShellExecute.
    /// </summary>
    public void InvokeVerb(Pane pane, string id, string? verb)
    {
        if (!pane.IsPortal)
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

    /// <summary>Moves a dragged tile to whichever pane it was dropped on, or back to the desktop.</summary>
    private void DropItem(Pane from, IconTile tile, POINT screen)
    {
        // A portal mirrors a folder; dragging out of one would have to move files on disk.
        if (from.IsPortal) return;

        var point = _layer.ScreenToDesktop(screen);
        var target = PaneAt(point);

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

    /// <summary>Drops the item at the slot the pointer is over, so a pane can be ordered by hand.</summary>
    private void Reorder(Pane pane, string id, POINT point)
    {
        if (!_windows.TryGetValue(pane.Id, out var window)) return;

        int slot = SlotAt(pane, window, point);
        pane.Items.Remove(id);
        pane.Items.Insert(Math.Clamp(slot, 0, pane.Items.Count), id);
    }

    private int SlotAt(Pane pane, PaneWindow window, POINT point)
    {
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int tile = (int)Math.Round(90 * scale);          // tile box plus its margin
        int columns = Math.Max(1, (pane.Width - (int)(12 * scale)) / tile);

        int col = Math.Clamp((point.X - pane.X - (int)(6 * scale)) / tile, 0, columns - 1);
        int row = Math.Max(0, (point.Y - pane.Y - TitleHeight(pane) - (int)(4 * scale)) / tile);
        return row * columns + col;
    }

    private Pane? PaneAt(POINT desktopPoint) =>
        _layout.Panes.LastOrDefault(f => f.Contains(desktopPoint.X, desktopPoint.Y));

    // ---------- reconciliation ----------

    /// <summary>
    /// Adopts icons dropped onto a pane, parks every owned icon out of the shell's way, and
    /// refreshes what each pane window draws.
    /// </summary>
    public void Reconcile()
    {
        if (!_layer.IsValid) RecoverFromShellRestart();

        var icons = _icons.Snapshot();
        if (icons.Count == 0 && _layout.Panes.Count == 0) return;

        var byId = icons.ToDictionary(i => i.Id);
        var owned = new HashSet<string>(_layout.Panes.SelectMany(f => f.Items));
        bool changed = false;

        foreach (var icon in icons)
        {
            if (owned.Contains(icon.Id) || icon.Position.Y >= ParkedThreshold) continue;
            if (PaneAt(icon.Position) is not { IsPortal: false } pane) continue;
            pane.Items.Add(icon.Id);
            owned.Add(icon.Id);
            changed = true;
        }

        var moves = new List<(int, POINT)>();
        foreach (var pane in _layout.Panes)
        {
            if (pane.IsPortal) { RefreshPortal(pane); continue; }

            changed |= pane.Items.RemoveAll(id => !byId.ContainsKey(id)) > 0;

            for (int i = 0; i < pane.Items.Count; i++)
            {
                var icon = byId[pane.Items[i]];
                if (icon.Position.Y < ParkedThreshold) moves.Add((icon.Index, new POINT(i * 8, ParkY)));
            }

            Refresh(pane, byId);
        }

        RescueOrphans(icons, owned, moves);

        if (moves.Count > 0) _icons.Move(moves);
        if (changed) Save();
    }

    /// <summary>
    /// Brings back icons that are parked but belong to no pane. Without this a crash, or a
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
    private void RefreshPortal(Pane pane)
    {
        if (!_windows.TryGetValue(pane.Id, out var window)) return;

        List<string> paths;
        try
        {
            paths = Directory.EnumerateFileSystemEntries(pane.PortalPath!)
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

    /// <summary>Rebuilds a pane window's tiles, reusing the ones already there.</summary>
    private void Refresh(Pane pane, Dictionary<string, DesktopIcon> byId)
    {
        if (!_windows.TryGetValue(pane.Id, out var window)) return;

        var wanted = pane.Items.Where(byId.ContainsKey).ToList();
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
    /// Brings a pane's icons back onto the desktop, laid out where the pane is.
    /// <paramref name="forget"/> also empties the pane; without it the pane keeps its contents
    /// and simply re-parks them next time the app runs.
    /// </summary>
    private void ReleaseIcons(Pane pane, bool forget)
    {
        if (pane.IsPortal) return;

        var byId = _icons.Snapshot().ToDictionary(i => i.Id);
        var spacing = _icons.Spacing;
        var moves = new List<(int, POINT)>();

        for (int i = 0; i < pane.Items.Count; i++)
        {
            if (!byId.TryGetValue(pane.Items[i], out var icon)) continue;
            moves.Add((icon.Index, new POINT(pane.X + i % 4 * spacing.X, pane.Y + i / 4 * spacing.Y)));
        }

        if (forget) pane.Items.Clear();
        if (moves.Count > 0) _icons.Move(moves);
    }

    /// <summary>
    /// Puts every icon back on the desktop before the app goes away, so nothing is left parked
    /// off-screen looking deleted. Pane membership is kept, so the layout survives a restart.
    /// </summary>
    public void ReleaseIconsForShutdown()
    {
        foreach (var pane in _layout.Panes) ReleaseIcons(pane, forget: false);
        Save();
    }

    /// <summary>Empties every pane back onto the desktop — the tray command.</summary>
    public void ReleaseAllIcons()
    {
        foreach (var pane in _layout.Panes) ReleaseIcons(pane, forget: true);

        var byId = _icons.Snapshot().ToDictionary(i => i.Id);
        foreach (var pane in _layout.Panes) Refresh(pane, byId);
        Save();
    }

    public void Save() => PaneStore.Save(_layout);

    public void Dispose()
    {
        _timer.Stop();
        _capture?.Dispose();
        foreach (var window in _windows.Values) window.Close();
        _windows.Clear();
    }
}
