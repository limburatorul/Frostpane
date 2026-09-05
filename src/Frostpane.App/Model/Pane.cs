using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Frostpane.Model;

/// <summary>One pane: a rectangle on the desktop that owns a set of icons.</summary>
internal sealed class Pane
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Pane";

    /// <summary>Bounds in desktop coordinates — the same space the shell reports icon positions in.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 520;
    public int Height { get; set; } = 360;

    /// <summary>Height to restore to when the pane is unrolled.</summary>
    public int ExpandedHeight { get; set; } = 360;

    /// <summary>Top to restore to — a pane rolled against the bottom edge moves to stay there.</summary>
    public int ExpandedY { get; set; }

    public bool RolledUp { get; set; }

    /// <summary>Screen edge the pane is docked to: 0 none, 1 top, 2 bottom.</summary>
    public int Dock { get; set; }

    /// <summary>Docked panes unroll again when dragged away from their edge.</summary>
    [JsonIgnore] public bool RolledByEdge => Dock != 0;

    /// <summary>Parsing names of the desktop icons this pane owns, in display order.</summary>
    public List<string> Items { get; set; } = new();

    /// <summary>When set, the pane mirrors this folder instead of owning desktop icons.</summary>
    public string? PortalPath { get; set; }

    [JsonIgnore] public bool IsPortal => !string.IsNullOrEmpty(PortalPath);

    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

internal sealed class Layout
{
    public List<Pane> Panes { get; set; } = new();

    public Settings Settings { get; set; } = new();

    /// <summary>
    /// A release the user chose not to install. Kept here rather than in a settings file of its
    /// own because it is the only preference the app has that the installer must not reset.
    /// </summary>
    public string? SkippedUpdate { get; set; }
}

/// <summary>Reads and writes the pane layout under %APPDATA%\Frostpane.</summary>
internal static class PaneStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Frostpane", "layout.json");

    public static Layout Load()
    {
        if (!File.Exists(Path)) return new Layout();
        try
        {
            return JsonSerializer.Deserialize<Layout>(File.ReadAllText(Path), Options) ?? new Layout();
        }
        catch (JsonException)
        {
            // A corrupt layout must not stop the app from starting; the user still has their icons.
            File.Move(Path, Path + ".bad", overwrite: true);
            return new Layout();
        }
    }

    public static void Save(Layout layout)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(layout, Options));
    }
}
