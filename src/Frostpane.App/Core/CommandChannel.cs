using System.Windows.Interop;
using Frostpane.Interop;

namespace Frostpane.Core;

internal enum ShellCommand { NewPane, NewPortal }

/// <summary>
/// The app's hidden top-level window: it carries desktop-menu commands to the copy that is already
/// running, and it is what Windows talks to when something needs Frostpane to close.
///
/// A shell verb can only start a process, but two copies managing the same icons would fight each
/// other, so the second one hands its command over here and exits.
///
/// The window is deliberately top-level rather than message-only. Message-only windows never
/// receive WM_QUERYENDSESSION, so the Restart Manager could not ask the app to quit — an installer
/// updating Frostpane would fail on its own locked files, and logging off would kill it outright,
/// leaving icons parked off-screen.
/// </summary>
internal sealed class CommandChannel : IDisposable
{
    /// <summary>Window text, not class name: WPF picks the class, so the title is the address.</summary>
    private const string WindowName = "Frostpane.Command";

    private const int WS_POPUP = unchecked((int)0x80000000);

    private const int WM_CLOSE = 0x0010;
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;

    private const int WM_APP = 0x8000;
    private const int NewPaneMessage = WM_APP + 1;
    private const int NewPortalMessage = WM_APP + 2;

    private readonly HwndSource _source;

    public CommandChannel()
    {
        _source = new HwndSource(new HwndSourceParameters(WindowName)
        {
            WindowStyle = WS_POPUP,                       // top-level, and never shown
            ExtendedWindowStyle = Win32.WS_EX_TOOLWINDOW, // keeps it out of Alt-Tab
            Width = 0,
            Height = 0,
        });
        _source.AddHook(OnMessage);
    }

    public event Action<ShellCommand>? Received;

    /// <summary>Raised when Windows or an installer asks the app to quit.</summary>
    public event Action? CloseRequested;

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NewPaneMessage:
                handled = true;
                Received?.Invoke(ShellCommand.NewPane);
                return IntPtr.Zero;

            case NewPortalMessage:
                handled = true;
                Received?.Invoke(ShellCommand.NewPortal);
                return IntPtr.Zero;

            case WM_QUERYENDSESSION:
                // TRUE means "nothing unsaved, go ahead"; WM_ENDSESSION then follows.
                handled = true;
                return new IntPtr(1);

            case WM_ENDSESSION when wParam != IntPtr.Zero:
            case WM_CLOSE:
                handled = true;
                CloseRequested?.Invoke();
                return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>Delivers a command to a running copy. False means there is none to deliver to.</summary>
    public static bool Send(ShellCommand command)
    {
        IntPtr target = Win32.FindWindow(null, WindowName);
        if (target == IntPtr.Zero) return false;

        int message = command == ShellCommand.NewPane ? NewPaneMessage : NewPortalMessage;
        return Win32.PostMessage(target, message, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose() => _source.Dispose();
}
