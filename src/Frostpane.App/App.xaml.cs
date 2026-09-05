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
            MessageBox.Show("Nu am găsit desktopul Explorer-ului (SHELLDLL_DefView).",
                            "Frostpane", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _manager = new PaneManager(_layer);
        _manager.ContextMenuRequested += ShowPaneMenu;

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
        menu.Items.Add("Panou nou", null, (_, _) => NewPaneAtCursor());
        menu.Items.Add("Portal nou…", null, (_, _) => NewPortalAtCursor());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Eliberează toate iconițele", null, (_, _) => _manager!.ReleaseAllIcons());
        menu.Items.Add(new ToolStripSeparator());

        var startup = new ToolStripMenuItem("Pornește odată cu Windows") { Checked = Autostart.Enabled };
        startup.Click += (_, _) => startup.Checked = Autostart.Enabled = !Autostart.Enabled;
        menu.Items.Add(startup);

        menu.Items.Add(new ToolStripSeparator());

        _updateItem = menu.Items.Add("Verifică actualizări", null, (_, _) =>
        {
            if (_pending is not null) OfferPendingUpdate();
            else _ = CheckForUpdatesAsync(announce: true);
        });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ieșire", null, (_, _) => Shutdown());
        return menu;
    }

    /// <summary>Reads the verb the desktop menu was invoked with, if any.</summary>
    private static ShellCommand? CommandFor(string[] args)
    {
        if (args.Contains("--new-pane")) return ShellCommand.NewPane;
        if (args.Contains("--new-portal")) return ShellCommand.NewPortal;
        return null;
    }

    private void Run(ShellCommand command)
    {
        if (command == ShellCommand.NewPane) NewPaneAtCursor();
        else NewPortalAtCursor();
    }

    private void NewPaneAtCursor() =>
        _manager!.Create(_layer!.ScreenToDesktop(Win32.CursorPosition));

    private void NewPortalAtCursor()
    {
        using var picker = new FolderBrowserDialog { Description = "Alege folderul oglindit de portal" };
        if (picker.ShowDialog() != DialogResult.OK) return;

        _manager!.Create(_layer!.ScreenToDesktop(Win32.CursorPosition),
                         System.IO.Path.GetFileName(picker.SelectedPath.TrimEnd('\\')),
                         picker.SelectedPath);
    }

    private void ShowPaneMenu(Pane pane, IconTile? tile, POINT screen)
    {
        var menu = new ContextMenuStrip();

        if (tile is not null)
        {
            menu.Items.Add("Deschide", null, (_, _) => _manager!.InvokeVerb(pane, tile.Id, null));
            if (!pane.IsPortal)
            {
                menu.Items.Add("Redenumește fișierul…", null, (_, _) => _manager!.InvokeVerb(pane, tile.Id, "rename"));
                menu.Items.Add("Șterge fișierul", null, (_, _) => _manager!.InvokeVerb(pane, tile.Id, "delete"));
            }
            menu.Items.Add("Proprietăți", null, (_, _) => _manager!.InvokeVerb(pane, tile.Id, "properties"));
            menu.Items.Add(new ToolStripSeparator());
        }

        menu.Items.Add("Redenumește panoul…", null, (_, _) =>
        {
            string? name = Prompt.AskText("Redenumește panoul", pane.Label);
            if (!string.IsNullOrWhiteSpace(name)) _manager!.Rename(pane, name);
        });
        menu.Items.Add(pane.RolledUp ? "Depliază" : "Pliază", null, (_, _) => _manager!.ToggleRollUp(pane));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Panou nou aici", null, (_, _) => _manager!.Create(_layer!.ScreenToDesktop(screen)));
        menu.Items.Add("Șterge panoul", null, (_, _) => _manager!.Remove(pane));

        menu.Show(new System.Drawing.Point(screen.X, screen.Y));
    }

    // ---------- updates ----------

    private async Task CheckForUpdatesAsync(bool announce)
    {
        var update = await Updater.CheckAsync();
        if (update is null)
        {
            if (announce)
                MessageBox.Show($"Folosești deja cea mai nouă versiune ({Updater.Current.ToString(3)}).",
                                "Frostpane", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string label = update.Version.ToString(3);

        _pending = update;
        if (_updateItem is not null) _updateItem.Text = $"Instalează versiunea {label}";
        if (_tray is not null) _tray.Text = $"Frostpane {Updater.Current.ToString(3)} — {label} disponibilă";

        // A tray balloon goes through the Windows notification centre, where it is routinely
        // suppressed, so the offer has to be a dialog. Declining silences that one version.
        if (announce || _manager?.SkippedUpdate != label) OfferPendingUpdate();
    }

    private void OfferPendingUpdate()
    {
        if (_pending is not { } update) return;
        string label = update.Version.ToString(3);

        var answer = MessageBox.Show($"Versiunea {label} e disponibilă (ai {Updater.Current.ToString(3)}).\n\n" +
                                     "O instalez acum? Aplicația se va reporni singură.",
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

        MessageBox.Show("Nu am reușit să descarc actualizarea. Încearcă mai târziu.",
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
