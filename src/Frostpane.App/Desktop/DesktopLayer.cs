using Frostpane.Interop;

namespace Frostpane.Desktop;

/// <summary>
/// The desktop's own windows, and the coordinate space its icons live in.
///
/// The shell reports icon positions relative to the client area of the window hosting the icon
/// view, whose origin is the top-left of the virtual screen — above and to the left of the primary
/// monitor whenever another monitor is placed there. Panes are stored in that same space, so a
/// pane and the icons it holds stay together however the monitors are arranged.
/// </summary>
internal sealed class DesktopLayer
{
    /// <summary>Progman, or whichever window currently hosts the icon view. The blur samples it.</summary>
    public IntPtr IconHost { get; private set; }

    public IntPtr DefView { get; private set; }

    /// <summary>Screen coordinates of the desktop origin.</summary>
    public POINT Origin { get; private set; }

    public bool IsValid => DefView != IntPtr.Zero && Win32.IsWindow(DefView);

    public DesktopLayer() => Refresh();

    /// <summary>Re-locates the desktop windows. Needed after an Explorer restart.</summary>
    public void Refresh()
    {
        (IconHost, DefView) = FindIconHost();

        var origin = new POINT(0, 0);
        if (DefView != IntPtr.Zero) Win32.ClientToScreen(DefView, ref origin);
        Origin = origin;
    }

    private static (IntPtr host, IntPtr defView) FindIconHost()
    {
        IntPtr host = IntPtr.Zero, defView = IntPtr.Zero;
        Win32.EnumWindows((h, _) =>
        {
            IntPtr view = Win32.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero) return true;
            host = h;
            defView = view;
            return false;
        }, IntPtr.Zero);
        return (host, defView);
    }

    public POINT ScreenToDesktop(POINT screen) => new(screen.X - Origin.X, screen.Y - Origin.Y);
}
