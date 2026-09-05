using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using System.Windows.Threading;
using Frostpane.Core;
using Frostpane.Model;

namespace Frostpane.Ui;

/// <summary>
/// Appearance and behaviour settings. Every change applies to the open panes immediately, so the
/// window is a live preview rather than a form with an OK button.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly Action _changed;
    private readonly Func<string> _blurStatus;
    private readonly DispatcherTimer _statusTimer;

    private bool _loading = true;

    internal SettingsWindow(Settings settings, Action changed, Func<string> blurStatus)
    {
        InitializeComponent();

        _settings = settings;
        _changed = changed;
        _blurStatus = blurStatus;

        Load();
        _loading = false;

        foreach (var slider in new[]
                 {
                     SoftnessSlider, BrightnessSlider, SaturationSlider, GrainSlider,
                     OpacitySlider, TintSlider, CornerSlider,
                     TitleHeightSlider, TitleStrengthSlider, BorderThicknessSlider, BorderStrengthSlider,
                     IconSlider,
                 })
        {
            slider.ValueChanged += (_, _) => Apply();
        }

        foreach (var box in new[] { BlurBox, PeekBox })
        {
            box.Checked += (_, _) => Apply();
            box.Unchecked += (_, _) => Apply();
        }

        PresetGlass.Checked += (_, _) => UsePreset("glass", 120, 8, 0);
        PresetAcrylic.Checked += (_, _) => UsePreset("acrylic", 200, 16, 4);
        PresetFrosted.Checked += (_, _) => UsePreset("frosted", 110, 26, 6);

        TintColorButton.Click += (_, _) => PickColour(c => _settings.TintColor = c, _settings.TintColor);
        TitleColorButton.Click += (_, _) => PickColour(c => _settings.TitleBarColor = c, _settings.TitleBarColor);
        BorderColorButton.Click += (_, _) => PickColour(c => _settings.BorderColor = c, _settings.BorderColor);

        AutostartBox.Checked += (_, _) => Autostart.Enabled = true;
        AutostartBox.Unchecked += (_, _) => Autostart.Enabled = false;

        ResetButton.Click += (_, _) => Reset();
        CloseButton.Click += (_, _) => Close();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _statusTimer.Tick += (_, _) => ShowStatus();
        _statusTimer.Start();
        ShowStatus();
        Closed += (_, _) => _statusTimer.Stop();
    }

    private void ShowStatus() => BlurStatusText.Text = "Wallpaper capture: " + _blurStatus();

    private void Load()
    {
        BlurBox.IsChecked = _settings.BlurWallpaper;
        PeekBox.IsChecked = _settings.PeekOnHover;
        AutostartBox.IsChecked = Autostart.Enabled;

        SoftnessSlider.Value = _settings.BlurSoftness;
        BrightnessSlider.Value = _settings.BlurBrightness;
        SaturationSlider.Value = _settings.BlurSaturation;
        GrainSlider.Value = _settings.BlurGrain;

        switch (_settings.BlurPreset)
        {
            case "glass": PresetGlass.IsChecked = true; break;
            case "frosted": PresetFrosted.IsChecked = true; break;
            default: PresetAcrylic.IsChecked = true; break;
        }
        OpacitySlider.Value = _settings.BackgroundOpacity;
        TintSlider.Value = _settings.TintStrength;
        CornerSlider.Value = _settings.CornerRadius;
        TitleHeightSlider.Value = _settings.TitleBarHeight;
        TitleStrengthSlider.Value = _settings.TitleBarStrength;
        BorderThicknessSlider.Value = _settings.BorderThickness;
        BorderStrengthSlider.Value = _settings.BorderStrength;
        IconSlider.Value = _settings.IconSize;

        ShowSwatches();
    }

    private void ShowSwatches()
    {
        TintSwatch.Background = Brush(_settings.TintColor);
        TitleSwatch.Background = Brush(_settings.TitleBarColor);
        BorderSwatch.Background = Brush(_settings.BorderColor);
    }

    private static SolidColorBrush Brush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!); }
        catch (Exception) { return new SolidColorBrush(Colors.Gray); }
    }

    /// <summary>Uses the system colour picker rather than shipping one.</summary>
    private void PickColour(Action<string> assign, string current)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };

        try
        {
            var colour = (Color)ColorConverter.ConvertFromString(current)!;
            dialog.Color = System.Drawing.Color.FromArgb(colour.R, colour.G, colour.B);
        }
        catch (Exception)
        {
            // An unreadable stored colour just means the picker opens on its default.
        }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        assign($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        ShowSwatches();
        _changed();
    }

    private void Apply()
    {
        if (_loading) return;

        _settings.BlurWallpaper = BlurBox.IsChecked == true;
        _settings.PeekOnHover = PeekBox.IsChecked == true;

        _settings.BlurSoftness = (int)SoftnessSlider.Value;
        _settings.BlurBrightness = (int)BrightnessSlider.Value;
        _settings.BlurSaturation = (int)SaturationSlider.Value;
        _settings.BlurGrain = (int)GrainSlider.Value;
        _settings.BackgroundOpacity = (int)OpacitySlider.Value;
        _settings.TintStrength = (int)TintSlider.Value;
        _settings.CornerRadius = (int)CornerSlider.Value;
        _settings.TitleBarHeight = (int)TitleHeightSlider.Value;
        _settings.TitleBarStrength = (int)TitleStrengthSlider.Value;
        _settings.BorderThickness = (int)BorderThicknessSlider.Value;
        _settings.BorderStrength = (int)BorderStrengthSlider.Value;
        _settings.IconSize = (int)IconSlider.Value;

        _changed();
    }

    /// <summary>
    /// Applies a named look. The softness slider is left alone on purpose: the preset multiplies
    /// it, so whatever the user set stays meaningful across all three.
    /// </summary>
    private void UsePreset(string name, int saturation, int brightness, int grain)
    {
        if (_loading) return;

        _settings.BlurPreset = name;

        _loading = true;
        SaturationSlider.Value = saturation;
        BrightnessSlider.Value = brightness;
        GrainSlider.Value = grain;
        _loading = false;

        Apply();
    }

    private void Reset()
    {
        var defaults = new Settings();

        _settings.BlurPreset = defaults.BlurPreset;
        _settings.TintColor = defaults.TintColor;
        _settings.TitleBarColor = defaults.TitleBarColor;
        _settings.BorderColor = defaults.BorderColor;

        _loading = true;
        BlurBox.IsChecked = defaults.BlurWallpaper;
        PeekBox.IsChecked = defaults.PeekOnHover;
        SoftnessSlider.Value = defaults.BlurSoftness;
        BrightnessSlider.Value = defaults.BlurBrightness;
        SaturationSlider.Value = defaults.BlurSaturation;
        GrainSlider.Value = defaults.BlurGrain;
        PresetAcrylic.IsChecked = true;
        OpacitySlider.Value = defaults.BackgroundOpacity;
        TintSlider.Value = defaults.TintStrength;
        CornerSlider.Value = defaults.CornerRadius;
        TitleHeightSlider.Value = defaults.TitleBarHeight;
        TitleStrengthSlider.Value = defaults.TitleBarStrength;
        BorderThicknessSlider.Value = defaults.BorderThickness;
        BorderStrengthSlider.Value = defaults.BorderStrength;
        IconSlider.Value = defaults.IconSize;
        _loading = false;

        ShowSwatches();
        Apply();
    }
}
