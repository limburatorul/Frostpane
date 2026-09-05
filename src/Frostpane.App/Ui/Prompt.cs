using System.Windows;
using System.Windows.Controls;

namespace Frostpane.Ui;

/// <summary>A one-line text prompt — the app's only modal dialog.</summary>
internal static class Prompt
{
    public static string? AskText(string title, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12), FontSize = 14 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Anulează", IsCancel = true, Width = 80 };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.Height,
            Width = 360,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
        };

        ok.Click += (_, _) => { window.DialogResult = true; };
        window.Loaded += (_, _) => { box.SelectAll(); box.Focus(); };

        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
