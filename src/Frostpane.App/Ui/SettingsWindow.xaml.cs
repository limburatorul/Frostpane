using System.Windows;
using Frostpane.Core;
using Frostpane.Model;

namespace Frostpane.Ui;

/// <summary>
/// Appearance and behaviour settings. Every change applies to the open panes immediately, so the
/// window is a live preview rather than a form with an OK button.
/// </summary>
public partial class SettingsWindow : Window
{
    private const string DarkTint = "#141419";
    private const string NeutralTint = "#3A3A44";
    private const string LightTint = "#C8C8D2";

    private readonly Settings _settings;
    private readonly Action _changed;
    private readonly Func<string> _blurStatus;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;

    private bool _loading = true;

    internal SettingsWindow(Settings settings, Action changed, Func<string> blurStatus)
    {
        InitializeComponent();

        _settings = settings;
        _changed = changed;
        _blurStatus = blurStatus;

        _statusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(700),
        };
        _statusTimer.Tick += (_, _) => BlurStatusText.Text = "Wallpaper capture: " + _blurStatus();
        _statusTimer.Start();
        BlurStatusText.Text = "Wallpaper capture: " + _blurStatus();
        Closed += (_, _) => _statusTimer.Stop();

        Load();
        _loading = false;

        PeekBox.Checked += (_, _) => Apply();
        PeekBox.Unchecked += (_, _) => Apply();
        BlurBox.Checked += (_, _) => Apply();
        BlurBox.Unchecked += (_, _) => Apply();
        SoftnessSlider.ValueChanged += (_, _) => Apply();
        BrightnessSlider.ValueChanged += (_, _) => Apply();
        OpacitySlider.ValueChanged += (_, _) => Apply();
        TintSlider.ValueChanged += (_, _) => Apply();
        IconSlider.ValueChanged += (_, _) => Apply();
        TintDark.Checked += (_, _) => Apply();
        TintNeutral.Checked += (_, _) => Apply();
        TintLight.Checked += (_, _) => Apply();

        AutostartBox.Checked += (_, _) => Autostart.Enabled = true;
        AutostartBox.Unchecked += (_, _) => Autostart.Enabled = false;

        ResetButton.Click += (_, _) => Reset();
        CloseButton.Click += (_, _) => Close();
    }

    private void Load()
    {
        BlurBox.IsChecked = _settings.BlurWallpaper;
        PeekBox.IsChecked = _settings.PeekOnHover;
        SoftnessSlider.Value = _settings.BlurSoftness;
        BrightnessSlider.Value = _settings.BlurBrightness;
        OpacitySlider.Value = _settings.BackgroundOpacity;
        TintSlider.Value = _settings.TintStrength;
        IconSlider.Value = _settings.IconSize;
        AutostartBox.IsChecked = Autostart.Enabled;

        switch (_settings.TintColor)
        {
            case NeutralTint: TintNeutral.IsChecked = true; break;
            case LightTint: TintLight.IsChecked = true; break;
            default: TintDark.IsChecked = true; break;
        }
    }

    private void Apply()
    {
        if (_loading) return;

        _settings.BlurWallpaper = BlurBox.IsChecked == true;
        _settings.PeekOnHover = PeekBox.IsChecked == true;
        _settings.BlurSoftness = (int)SoftnessSlider.Value;
        _settings.BlurBrightness = (int)BrightnessSlider.Value;
        _settings.BackgroundOpacity = (int)OpacitySlider.Value;
        _settings.TintStrength = (int)TintSlider.Value;
        _settings.IconSize = (int)IconSlider.Value;
        _settings.TintColor = TintNeutral.IsChecked == true ? NeutralTint
                            : TintLight.IsChecked == true ? LightTint
                            : DarkTint;

        _changed();
    }

    private void Reset()
    {
        var defaults = new Settings();

        _loading = true;
        BlurBox.IsChecked = defaults.BlurWallpaper;
        PeekBox.IsChecked = defaults.PeekOnHover;
        SoftnessSlider.Value = defaults.BlurSoftness;
        BrightnessSlider.Value = defaults.BlurBrightness;
        OpacitySlider.Value = defaults.BackgroundOpacity;
        TintSlider.Value = defaults.TintStrength;
        IconSlider.Value = defaults.IconSize;
        TintDark.IsChecked = true;
        _loading = false;

        Apply();
    }
}
