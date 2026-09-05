using System.Runtime.InteropServices;
using System.Text;

namespace DesktopProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "dump";
        try
        {
            switch (cmd)
            {
                case "dump":
                    DumpWindows();
                    DumpDesktopView();
                    return 0;
                case "workerw":
                    DumpWorkerW();
                    return 0;
                case "move":
                    MoveItem(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]));
                    return 0;
                default:
                    Console.WriteLine("usage: DesktopProbe [dump|workerw|move <index> <x> <y>]");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: " + ex.Message);
            return 2;
        }
    }

    // ---------- window hierarchy ----------

    private static void DumpWindows()
    {
        Console.WriteLine("=== top-level Progman / WorkerW ===");
        Win32.EnumWindows((h, _) =>
        {
            string cls = GetClassName(h);
            if (cls is "Progman" or "WorkerW")
            {
                Win32.GetWindowRect(h, out var r);
                bool visible = Win32.IsWindowVisible(h);
                Console.WriteLine($"{cls,-8} 0x{h.ToInt64():X8} vis={visible} rect=({r.left},{r.top})-({r.right},{r.bottom})");
                foreach (var child in Children(h))
                    Console.WriteLine($"    child {GetClassName(child),-18} 0x{child.ToInt64():X8}");
            }
            return true;
        }, IntPtr.Zero);
    }

    private static void DumpWorkerW()
    {
        IntPtr progman = Win32.FindWindow("Progman", null);
        Console.WriteLine($"Progman = 0x{progman.ToInt64():X8}");

        // 0x052C asks Progman to spawn the WorkerW that hosts the wallpaper behind the icons.
        Win32.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        IntPtr wallpaperHost = FindWallpaperWorkerW();
        Console.WriteLine(wallpaperHost == IntPtr.Zero
            ? "wallpaper WorkerW: NOT FOUND"
            : $"wallpaper WorkerW = 0x{wallpaperHost.ToInt64():X8}");

        IntPtr defView = FindDefView();
        Console.WriteLine(defView == IntPtr.Zero
            ? "SHELLDLL_DefView: NOT FOUND"
            : $"SHELLDLL_DefView = 0x{defView.ToInt64():X8} (parent 0x{Win32.GetParent(defView).ToInt64():X8} = {GetClassName(Win32.GetParent(defView))})");
    }

    /// <summary>The WorkerW that paints the wallpaper: the sibling that follows the one hosting SHELLDLL_DefView.</summary>
    private static IntPtr FindWallpaperWorkerW()
    {
        IntPtr result = IntPtr.Zero;
        Win32.EnumWindows((h, _) =>
        {
            if (Win32.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                IntPtr sibling = Win32.FindWindowEx(IntPtr.Zero, h, "WorkerW", null);
                if (sibling != IntPtr.Zero) { result = sibling; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static IntPtr FindDefView()
    {
        IntPtr result = IntPtr.Zero;
        Win32.EnumWindows((h, _) =>
        {
            IntPtr dv = Win32.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero) { result = dv; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static IEnumerable<IntPtr> Children(IntPtr parent)
    {
        var list = new List<IntPtr>();
        Win32.EnumChildWindows(parent, (h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ---------- desktop shell view ----------

    private static void DumpDesktopView()
    {
        Console.WriteLine();
        Console.WriteLine("=== desktop IFolderView2 ===");
        var view = Shell.GetDesktopFolderView();

        Check(view.ItemCount(Shell.SVGIO_ALLVIEW, out int count), "ItemCount");
        Console.WriteLine($"items = {count}");

        Check(view.GetSpacing(out var spacing), "GetSpacing");
        Console.WriteLine($"spacing = {spacing}");
        Check(view.GetDefaultSpacing(out var defSpacing), "GetDefaultSpacing");
        Console.WriteLine($"default spacing = {defSpacing}");

        int auto = view.GetAutoArrange();
        Console.WriteLine($"auto-arrange = {(auto == 0 ? "ON" : "off")} (hr 0x{auto:X8})");

        Check(view.GetCurrentFolderFlags(out uint flags), "GetCurrentFolderFlags");
        Console.WriteLine($"folder flags = 0x{flags:X8} (autoarrange={(flags & Shell.FWF_AUTOARRANGE) != 0}, snaptogrid={(flags & Shell.FWF_SNAPTOGRID) != 0})");

        Check(view.GetViewModeAndIconSize(out uint mode, out int iconSize), "GetViewModeAndIconSize");
        Console.WriteLine($"view mode = {mode}, icon size = {iconSize}");

        Console.WriteLine();
        for (int i = 0; i < Math.Min(count, 25); i++)
        {
            int hr = view.Item(i, out IntPtr pidl);
            if (hr != 0 || pidl == IntPtr.Zero) { Console.WriteLine($"[{i}] Item failed 0x{hr:X8}"); continue; }
            try
            {
                int phr = view.GetItemPosition(pidl, out var pt);
                Console.WriteLine($"[{i,2}] {Shell.GetItemName(view, i),-40} pos={(phr == 0 ? pt.ToString() : $"<hr 0x{phr:X8}>")}");
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        if (count > 25) Console.WriteLine($"... and {count - 25} more");
    }

    private static void MoveItem(int index, int x, int y)
    {
        var view = Shell.GetDesktopFolderView();

        // Positioning only sticks when the shell is not auto-arranging or snapping.
        Check(view.SetCurrentFolderFlags(Shell.FWF_AUTOARRANGE | Shell.FWF_SNAPTOGRID, 0), "SetCurrentFolderFlags");

        Check(view.Item(index, out IntPtr pidl), "Item");
        try
        {
            Check(view.GetItemPosition(pidl, out var before), "GetItemPosition(before)");
            Console.WriteLine($"'{Shell.GetItemName(view, index)}' at {before} -> ({x},{y})");

            Check(view.SelectAndPositionItems(1, new[] { pidl }, new[] { new POINT(x, y) },
                                              Shell.SVSI_POSITIONITEM | Shell.SVSI_NOSTATECHANGE),
                  "SelectAndPositionItems");
        }
        finally { Marshal.FreeCoTaskMem(pidl); }

        // Re-read: the pidl can be invalidated by the move, so query by index again.
        Check(view.Item(index, out IntPtr pidl2), "Item(after)");
        try
        {
            Check(view.GetItemPosition(pidl2, out var after), "GetItemPosition(after)");
            Console.WriteLine($"now at {after}");
        }
        finally { Marshal.FreeCoTaskMem(pidl2); }
    }

    private static void Check(int hr, string what)
    {
        if (hr != 0) Console.WriteLine($"  !! {what} -> 0x{hr:X8}");
    }
}

internal static class Win32
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? name);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
