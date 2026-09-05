using System.Windows.Interop;
using Frostpane.Interop;

namespace Frostpane.Core;

internal enum ShellCommand { NewPane, NewPortal }

/// <summary>
/// Carries a desktop menu command to the copy of Frostpane that is already running.
///
/// A shell verb can only start a process, but two copies managing the same icons would fight each
/// other, so the second one hands its command over through a message-only window and exits.
/// </summary>
internal sealed class CommandChannel : IDisposable
{
    /// <summary>Window text, not class name: WPF picks the class, so the title is the address.</summary>
    private const string WindowName = "Frostpane.Command";

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private const int WM_APP = 0x8000;
    private const int NewPaneMessage = WM_APP + 1;
    private const int NewPortalMessage = WM_APP + 2;

    private readonly HwndSource _source;

    public CommandChannel()
    {
        _source = new HwndSource(new HwndSourceParameters(WindowName)
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
        });
        _source.AddHook(OnMessage);
    }

    public event Action<ShellCommand>? Received;

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        ShellCommand? command = msg switch
        {
            NewPaneMessage => ShellCommand.NewPane,
            NewPortalMessage => ShellCommand.NewPortal,
            _ => null,
        };

        if (command is null) return IntPtr.Zero;

        handled = true;
        Received?.Invoke(command.Value);
        return IntPtr.Zero;
    }

    /// <summary>Delivers a command to a running copy. False means there is none to deliver to.</summary>
    public static bool Send(ShellCommand command)
    {
        IntPtr target = Win32.FindWindowEx(HWND_MESSAGE, IntPtr.Zero, null, WindowName);
        if (target == IntPtr.Zero) return false;

        int message = command == ShellCommand.NewPane ? NewPaneMessage : NewPortalMessage;
        return Win32.PostMessage(target, message, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose() => _source.Dispose();
}
