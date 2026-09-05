using System.Runtime.InteropServices;
using Frostpane.Interop;

namespace Frostpane.Desktop;

/// <summary>One item in the shell's desktop view.</summary>
/// <param name="Index">Position in the view; only valid until the view changes.</param>
/// <param name="Id">Parsing name (full path, or <c>::{GUID}</c> for virtual items) — stable across sessions.</param>
/// <param name="Name">Display name, as drawn under the icon.</param>
/// <param name="Position">Top-left of the icon in desktop coordinates.</param>
internal sealed record DesktopIcon(int Index, string Id, string Name, POINT Position);

/// <summary>
/// Reads and repositions the real desktop icons through the shell's own view object.
///
/// Everything goes through IFolderView2 on Explorer's desktop view, which is a documented,
/// cross-process COM interface — no memory is written into explorer.exe.
/// </summary>
internal sealed class DesktopIcons
{
    private IFolderView2? _view;

    /// <summary>Drops the cached view so the next call rebinds. Needed after an Explorer restart.</summary>
    public void Invalidate() => _view = null;

    private IFolderView2 View => _view ??= Bind();

    /// <summary>Grid step the shell uses between icons, in pixels.</summary>
    public POINT Spacing => Retry(v => v.GetSpacing(out var p) == 0 ? p : new POINT(75, 75), new POINT(75, 75));

    /// <summary>
    /// Turns off auto-arrange and snap-to-grid. While either is on the shell overrides every
    /// position we set, so a pane could never hold its icons in place.
    /// </summary>
    public void AllowFreePositioning() =>
        Retry(v => v.SetCurrentFolderFlags(ShellIds.FWF_AUTOARRANGE | ShellIds.FWF_SNAPTOGRID, 0), 0);

    public IReadOnlyList<DesktopIcon> Snapshot() => Retry(ReadAll, Array.Empty<DesktopIcon>());

    private static IReadOnlyList<DesktopIcon> ReadAll(IFolderView2 view)
    {
        if (view.ItemCount(ShellIds.SVGIO_ALLVIEW, out int count) != 0 || count <= 0)
            return Array.Empty<DesktopIcon>();

        var icons = new List<DesktopIcon>(count);
        for (int i = 0; i < count; i++)
        {
            if (view.Item(i, out IntPtr pidl) != 0 || pidl == IntPtr.Zero) continue;
            try
            {
                if (view.GetItemPosition(pidl, out POINT pos) != 0) continue;
                var (id, name) = ReadNames(view, i);
                if (id.Length != 0) icons.Add(new DesktopIcon(i, id, name, pos));
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        return icons;
    }

    private static (string id, string name) ReadNames(IFolderView2 view, int index)
    {
        var iid = ShellIds.IID_IShellItem;
        if (view.GetItem(index, ref iid, out IntPtr ppv) != 0 || ppv == IntPtr.Zero) return ("", "");
        try
        {
            var item = (IShellItem)Marshal.GetObjectForIUnknown(ppv);
            return (Name(item, ShellIds.SIGDN_DESKTOPABSOLUTEPARSING), Name(item, ShellIds.SIGDN_NORMALDISPLAY));
        }
        finally { Marshal.Release(ppv); }

        static string Name(IShellItem item, uint kind)
        {
            if (item.GetDisplayName(kind, out IntPtr p) != 0 || p == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; }
            finally { Marshal.FreeCoTaskMem(p); }
        }
    }

    /// <summary>
    /// Moves icons in one shot. Batching matters: each call repaints the desktop, so moving a
    /// pane full of icons one at a time would flicker.
    /// </summary>
    public void Move(IReadOnlyList<(int Index, POINT Position)> moves)
    {
        if (moves.Count == 0) return;

        Retry(view =>
        {
            var pidls = new IntPtr[moves.Count];
            var points = new POINT[moves.Count];
            int n = 0;
            try
            {
                foreach (var (index, position) in moves)
                {
                    if (view.Item(index, out IntPtr pidl) != 0 || pidl == IntPtr.Zero) continue;
                    pidls[n] = pidl;
                    points[n] = position;
                    n++;
                }
                if (n == 0) return 0;
                return view.SelectAndPositionItems((uint)n, pidls, points,
                                                   ShellIds.SVSI_POSITIONITEM | ShellIds.SVSI_NOSTATECHANGE);
            }
            finally
            {
                for (int i = 0; i < n; i++) Marshal.FreeCoTaskMem(pidls[i]);
            }
        }, 0);
    }

    /// <summary>
    /// Runs a shell verb on an item — "open", "rename", "delete", "properties", "copy".
    /// Going through the shell means the user gets the real dialogs and the real undo.
    /// </summary>
    public void Verb(int index, string? verb) => Retry(view =>
    {
        int hr = view.SelectItem(index, ShellIds.SVSI_SELECT | ShellIds.SVSI_DESELECTOTHERS | ShellIds.SVSI_FOCUSED);
        return hr != 0 ? hr : view.InvokeVerbOnSelection(verb);
    }, 0);

    /// <summary>Runs <paramref name="action"/>, rebinding once if the cached view died with Explorer.</summary>
    private T Retry<T>(Func<IFolderView2, T> action, T fallback)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { return action(View); }
            catch (COMException) { Invalidate(); }
            catch (InvalidOperationException) { Invalidate(); }
        }
        return fallback;
    }

    private static IFolderView2 Bind()
    {
        var type = Type.GetTypeFromCLSID(ShellIds.CLSID_ShellWindows)
                   ?? throw new InvalidOperationException("ShellWindows is not registered.");
        var shellWindows = (IShellWindows)Activator.CreateInstance(type)!;

        object empty = 0;   // VT_I4 0 — "no location", which is what SWC_DESKTOP expects
        int hr = shellWindows.FindWindowSW(ref empty, ref empty, ShellIds.SWC_DESKTOP, out _,
                                           ShellIds.SWFO_NEEDDISPATCH, out IntPtr pDisp);
        if (hr != 0 || pDisp == IntPtr.Zero)
            throw new InvalidOperationException($"Desktop shell view not found (0x{hr:X8}).");

        try
        {
            var provider = (Interop.IServiceProvider)Marshal.GetObjectForIUnknown(pDisp);
            var sid = ShellIds.SID_STopLevelBrowser;
            var iid = ShellIds.IID_IShellBrowser;
            hr = provider.QueryService(ref sid, ref iid, out IntPtr pBrowser);
            if (hr != 0) throw new InvalidOperationException($"QueryService failed (0x{hr:X8}).");

            try
            {
                var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pBrowser);
                hr = browser.QueryActiveShellView(out IntPtr pView);
                if (hr != 0) throw new InvalidOperationException($"QueryActiveShellView failed (0x{hr:X8}).");
                try { return (IFolderView2)Marshal.GetObjectForIUnknown(pView); }
                finally { Marshal.Release(pView); }
            }
            finally { Marshal.Release(pBrowser); }
        }
        finally { Marshal.Release(pDisp); }
    }
}
