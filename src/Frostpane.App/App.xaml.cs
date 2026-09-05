using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Frostpane.Core;
using Frostpane.Desktop;
using Frostpane.Interop;
using Frostpane.Model;
using Frostpane.Ui;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Frostpane;

public partial class App : Application
{
    private DesktopLayer? _layer;
    private PaneManager? _manager;
    private NotifyIcon? _tray;
    private ToolStripItem? _updateItem;
    private DispatcherTimer? _updateTimer;
    private Update? _pending;
    private Mutex? _singleInstance;
    private CommandChannel? _commands;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var requested = CommandFor(e.Args);

        // Two copies would fight over the same icons, so a second one only forwards its command.
        _singleInstance = new Mutex(initiallyOwned: true, "Frostpane.SingleInstance", out bool first);
        if (!first)
        {
            if (requested is not null) CommandChannel.Send(requested.Value);
            Shutdown();
            return;
        }

        _layer = new DesktopLayer();
        if (!_layer.IsValid)
        {
            MessageBox.Show("Could not find the Explorer desktop (SHELLDLL_DefView).",
                            "Frostpane", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _manager = new PaneManager(_layer);
        _manager.ContextMenuRequested += ShowPaneMenu;
        _manager.RenameRequested += AskRename;

        ShellMenu.Register();
        _commands = new CommandChannel();
        _commands.Received += Run;
        _commands.CloseRequested += () => Dispatcher.BeginInvoke(new Action(Shutdown));
        if (requested is not null) Run(requested.Value);

        _tray = new NotifyIcon
        {
            Icon = AppIcon(),
            Text = $"Frostpane {Updater.Current.ToString(3)}",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(),
        };
        _ = CheckForUpdatesAsync(announce: false);

        // A copy left running for days would otherwise never notice a release.
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromHours(6),
        };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(announce: false);
        _updateTimer.Start();
    }

    /// <summary>The icon compiled into the executable, so no separate file has to ship beside it.</summary>
    private static System.Drawing.Icon AppIcon()
    {
        try
        {
            return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                   ?? System.Drawing.SystemIcons.Application;
        }
        catch (Exception)
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("New pane", null, (_, _) => NewPaneAtCursor());
        menu.Items.Add("New portal…", null, (_, _) => NewPortalAtCursor());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Release all icons", null, (_, _) => _manager!.ReleaseAllIcons());
        menu.Items.Add(new ToolStripSeparator());

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.Enabled };
        startup.Click += (_, _) => startup.Checked = Autostart.Enabled = !Autostart.Enabled;
        menu.Items.Add(startup);
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());

        menu.Items.Add(new ToolStripSeparator());

        _updateItem = menu.Items.Add("Check for updates", null, (_, _) =>
        {
            if (_pending is not null) OfferPendingUpdate();
            else _ = CheckForUpdatesAsync(announce: true);
        });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        return menu;
    }

    /// <summary>Reads the verb the desktop menu was invoked with, if any.</summary>
    private static ShellCommand? CommandFor(string[] args)
    {
        if (args.Contains("--new-pane")) return ShellCommand.NewPane;
        if (args.Contains("--new-portal")) return ShellCommand.NewPortal;
        if (args.Contains("--settings")) return ShellCommand.Settings;
        return null;
    }

    private void Run(ShellCommand command)
    {
        switch (command)
        {
            case ShellCommand.NewPane: NewPaneAtCursor(); break;
            case ShellCommand.NewPortal: NewPortalAtCursor(); break;
            default: ShowSettings(); break;
        }
    }

    private void NewPaneAtCursor() =>
        _manager!.Create(_layer!.ScreenToDesktop(Win32.CursorPosition));

    private void NewPortalAtCursor()
    {
        using var picker = new FolderBrowserDialog { Description = "Choose the folder this portal mirrors" };
        if (picker.ShowDialog() != DialogResult.OK) return;

        _manager!.Create(_layer!.ScreenToDesktop(Win32.CursorPosition),
                         System.IO.Path.GetFileName(picker.SelectedPath.TrimEnd('\\')),
                         picker.SelectedPath);
    }

    /// <summary>
    /// The pane's own menu, built with WPF rather than WinForms.
    ///
    /// A WinForms ContextMenuStrip needs the WinForms message loop to stay open, which a WPF app
    /// does not run: the menu appeared and vanished in the same frame. WPF's own menu works from
    /// a window that never takes focus.
    /// </summary>
    private void ShowPaneMenu(Pane pane, IconTile? tile, PaneMenuContext where)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint,
            PlacementTarget = where.Target,
            HorizontalOffset = where.Local.X,
            VerticalOffset = where.Local.Y,
        };

        if (tile is not null)
        {
            Add(menu, "Open", () => _manager!.InvokeVerb(pane, tile.Id, null));
            if (!pane.IsPortal)
            {
                Add(menu, "Rename file…", () => _manager!.InvokeVerb(pane, tile.Id, "rename"));
                Add(menu, "Delete file", () => _manager!.InvokeVerb(pane, tile.Id, "delete"));
            }
            Add(menu, "Properties", () => _manager!.InvokeVerb(pane, tile.Id, "properties"));
            menu.Items.Add(new System.Windows.Controls.Separator());
        }

        Add(menu, "Rename pane…", () => AskRename(pane));
        Add(menu, pane.RolledUp ? "Unroll" : "Roll up", () => _manager!.ToggleRollUp(pane));
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add(menu, "New pane here", () => _manager!.Create(_layer!.ScreenToDesktop(where.Screen)));
        Add(menu, "Delete pane", () => _manager!.Remove(pane));
        menu.Items.Add(new System.Windows.Controls.Separator());
        Add(menu, "Settings…", ShowSettings);

        menu.IsOpen = true;
    }

    private static void Add(System.Windows.Controls.ContextMenu menu, string header, Action action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void AskRename(Pane pane)
    {
        string? name = Prompt.AskText("Rename pane", pane.Label);
        if (!string.IsNullOrWhiteSpace(name)) _manager!.Rename(pane, name);
    }

    private SettingsWindow? _settingsWindow;

    private void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_manager!.Settings, () => _manager!.ApplySettings(),
                                            () => _manager!.BlurStatus);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();   // the app is never foreground, so Show alone leaves it behind
    }

    // ---------- updates ----------

    private async Task CheckForUpdatesAsync(bool announce)
    {
        var update = await Updater.CheckAsync();
        if (update is null)
        {
            if (announce)
                MessageBox.Show($"You are already on the latest version ({Updater.Current.ToString(3)}).",
                                "Frostpane", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string label = update.Version.ToString(3);

        _pending = update;
        if (_updateItem is not null) _updateItem.Text = $"Install version {label}";
        if (_tray is not null) _tray.Text = $"Frostpane {Updater.Current.ToString(3)} — {label} available";

        // A tray balloon goes through the Windows notification centre, where it is routinely
        // suppressed, so the offer has to be a dialog. Declining silences that one version.
        if (announce || _manager?.SkippedUpdate != label) OfferPendingUpdate();
    }

    private void OfferPendingUpdate()
    {
        if (_pending is not { } update) return;
        string label = update.Version.ToString(3);

        var answer = MessageBox.Show($"Version {label} is available (you have {Updater.Current.ToString(3)}).\n\n" +
                                     "Install it now? Frostpane will restart itself.",
                                     "Frostpane", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            if (_manager is not null) _manager.SkippedUpdate = label;
            return;
        }

        if (_manager is not null) _manager.SkippedUpdate = null;
        _ = InstallAsync(update);
    }

    private async Task InstallAsync(Update update)
    {
        if (await Updater.InstallAsync(update))
        {
            Shutdown();     // the installer is waiting to replace the files this process is using
            return;
        }

        MessageBox.Show("The update could not be downloaded. Try again later.",
                        "Frostpane", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Icons left parked off-screen would look to the user as though they had been deleted.
        _manager?.ReleaseIconsForShutdown();
        _manager?.Dispose();

        _commands?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}
