namespace Frostpane.Model;

/// <summary>
/// How panes look. Shared by every pane; there are no per-pane appearance options.
///
/// The defaults aim for frosted glass, but a blurred sample of a nearly black wallpaper is itself
/// nearly black, which reads as a solid box rather than glass. <see cref="BackgroundOpacity"/> is
/// the way out: below 100 the live wallpaper shows through, so the pane stays visibly a pane.
/// </summary>
internal sealed class Settings
{
    /// <summary>Blur the wallpaper behind a pane. Off leaves plain tinted translucency.</summary>
    public bool BlurWallpaper { get; set; } = true;

    /// <summary>Opacity of a pane's whole background, 20–100. Contents stay fully opaque.</summary>
    public int BackgroundOpacity { get; set; } = 100;

    /// <summary>
    /// How strongly <see cref="TintColor"/> covers the backdrop, 0–60. Capped below opaque on
    /// purpose: a tint that hides the blurred sample turns the pane back into a flat box.
    /// </summary>
    public int TintStrength { get; set; } = 30;

    /// <summary>Tint applied over the backdrop, as #RRGGBB.</summary>
    public string TintColor { get; set; } = "#141419";

    /// <summary>How far the wallpaper sample is smeared, 1–10.</summary>
    public int BlurSoftness { get; set; } = 4;

    /// <summary>
    /// Lifts the blurred sample towards white, 0–80. A dark wallpaper blurs to near black, and a
    /// black backdrop reads as a solid box rather than glass; this is what pulls it back.
    /// </summary>
    public int BlurBrightness { get; set; } = 24;

    /// <summary>
    /// Named look: <c>glass</c>, <c>acrylic</c> or <c>frosted</c>. The preset multiplies
    /// <see cref="BlurSoftness"/> rather than replacing it, so that slider keeps meaning what it says.
    /// </summary>
    public string BlurPreset { get; set; } = "acrylic";

    /// <summary>Colour saturation of the sample, 0–200%. The lift that makes a blur read as acrylic.</summary>
    public int BlurSaturation { get; set; } = 200;

    /// <summary>Film grain over the sample, 0–8%. Real acrylic grain sits at 2–4%; more reads as dirt.</summary>
    public int BlurGrain { get; set; } = 4;

    /// <summary>Softness multiplier the current preset applies.</summary>
    public double PresetSoftness => BlurPreset switch
    {
        "glass" => 1.0,
        "frosted" => 3.0,
        _ => 2.1,
    };

    /// <summary>Unroll a rolled-up pane while the pointer rests on it, then roll it back.</summary>
    public bool PeekOnHover { get; set; } = true;

    /// <summary>Icon edge in device-independent pixels, 24–64.</summary>
    public int IconSize { get; set; } = 36;

    // ---- title bar ----

    /// <summary>Title bar height in device-independent pixels, 16–40.</summary>
    public int TitleBarHeight { get; set; } = 24;

    public string TitleBarColor { get; set; } = "#FFFFFF";

    /// <summary>How strongly the title bar colour shows, 0–100.</summary>
    public int TitleBarStrength { get; set; } = 13;

    // ---- border ----

    public string BorderColor { get; set; } = "#FFFFFF";

    /// <summary>Border opacity, 0–100.</summary>
    public int BorderStrength { get; set; } = 18;

    /// <summary>Border thickness in device-independent pixels, 0–4.</summary>
    public int BorderThickness { get; set; } = 1;

    /// <summary>Corner radius in device-independent pixels, 0–20.</summary>
    public int CornerRadius { get; set; } = 10;

    public Settings Clone() => (Settings)MemberwiseClone();
}
