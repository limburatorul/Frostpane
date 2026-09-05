using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fences.Model;

/// <summary>One fence: a rectangle on the desktop that owns a set of icons.</summary>
internal sealed class Fence
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Fence";

    /// <summary>Bounds in desktop coordinates — the same space the shell reports icon positions in.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 520;
    public int Height { get; set; } = 360;

    /// <summary>Height to restore to when the fence is unrolled.</summary>
    public int ExpandedHeight { get; set; } = 360;

    public bool RolledUp { get; set; }

    /// <summary>Parsing names of the desktop icons this fence owns, in display order.</summary>
    public List<string> Items { get; set; } = new();

    /// <summary>When set, the fence mirrors this folder instead of owning desktop icons.</summary>
    public string? PortalPath { get; set; }

    [JsonIgnore] public bool IsPortal => !string.IsNullOrEmpty(PortalPath);

    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

internal sealed class Layout
{
    public List<Fence> Fences { get; set; } = new();
}

/// <summary>Reads and writes the fence layout under %APPDATA%\Fences.</summary>
internal static class FenceStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fences", "layout.json");

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
