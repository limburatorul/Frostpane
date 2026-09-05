using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Frostpane.Core;

/// <summary>
/// Frostpane's entries on the right-click menu of the desktop.
///
/// Registered at every start rather than by the installer, so it also works when the app is run
/// from source and so it heals itself after the executable moves.
///
/// Two details matter and are easy to get wrong. The label has to go in the key's default value:
/// a plain string in <c>MUIVerb</c> is ignored here, and the entry then never renders. And the
/// desktop reads verbs from both <c>DesktopBackground\Shell</c> and <c>Directory\Background\shell</c>
/// depending on the Windows build, so both are written.
///
/// On Windows 11 an unpackaged verb like this lands under "Show more options"; the compact menu
/// only accepts handlers that come from a signed MSIX package.
/// </summary>
internal static class ShellMenu
{
    private static readonly string[] Roots =
    {
        @"Software\Classes\DesktopBackground\Shell",
        @"Software\Classes\Directory\Background\shell",
    };

    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public static void Register()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        bool wrote = false;
        foreach (string root in Roots)
        {
            wrote |= Verb(root, "Frostpane.NewPane", "Panou nou aici", "--new-pane", exe);
            wrote |= Verb(root, "Frostpane.NewPortal", "Portal nou aici…", "--new-portal", exe);
        }

        // Without this the shell can keep serving the verb list it read before we existed.
        if (wrote) SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Writes one verb. Returns false when it was already pointing here.</summary>
    private static bool Verb(string root, string key, string label, string argument, string exe)
    {
        string command = $"\"{exe}\" {argument}";

        using var verb = Registry.CurrentUser.CreateSubKey($@"{root}\{key}");
        if (verb is null) return false;

        using var commandKey = Registry.CurrentUser.CreateSubKey($@"{root}\{key}\command");
        if (commandKey?.GetValue("") as string == command) return false;

        verb.SetValue("", label);                   // the default value is the label the shell shows
        verb.SetValue("Icon", $"\"{exe}\",0");
        commandKey?.SetValue("", command);
        return true;
    }

    /// <summary>Used when running from source, where no uninstaller will ever clean this up.</summary>
    public static void Unregister()
    {
        foreach (string root in Roots)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{root}\Frostpane.NewPane", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"{root}\Frostpane.NewPortal", throwOnMissingSubKey: false);
        }
        SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
