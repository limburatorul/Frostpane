using System.Windows;
using Frostpane.Interop;
using Point = System.Windows.Point;

namespace Frostpane.Ui;

/// <summary>Where a pane's context menu was asked for.</summary>
/// <param name="Screen">Physical screen point, for commands that act on a location.</param>
/// <param name="Local">The same point inside the pane, in WPF units, for placing the menu.</param>
/// <param name="Target">The pane the menu belongs to.</param>
internal sealed record PaneMenuContext(POINT Screen, Point Local, FrameworkElement Target);
