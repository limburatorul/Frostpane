using System.ComponentModel;
using System.Windows.Media;

namespace Frostpane.Ui;

/// <summary>One icon as drawn inside a pane.</summary>
internal sealed class IconTile : INotifyPropertyChanged
{
    private ImageSource? _image;
    private double _opacity = 1.0;

    public IconTile(string id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>Shell parsing name — the identity the pane layout is stored against.</summary>
    public string Id { get; }

    public string Name { get; }

    public ImageSource? Image
    {
        get => _image;
        set { _image = value; Raise(nameof(Image)); }
    }

    /// <summary>Dimmed while the tile is being dragged.</summary>
    public double Opacity
    {
        get => _opacity;
        set { _opacity = value; Raise(nameof(Opacity)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
