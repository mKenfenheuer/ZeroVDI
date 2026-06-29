namespace KSol.RDPGateway.Models;

/// <summary>How the forced color theme is specified when <see cref="AppearanceSettings.ForceTheme"/> is on.</summary>
public enum ThemeSource
{
    /// <summary>Use one of the built-in named themes (<see cref="AppearanceSettings.PresetTheme"/>).</summary>
    Preset = 0,

    /// <summary>Use a custom primary/accent color (<see cref="AppearanceSettings.CustomColorHex"/>).</summary>
    Custom = 1,
}

/// <summary>Forced light/dark mode when <see cref="AppearanceSettings.ForceMode"/> is on.</summary>
public enum ThemeModePreference
{
    Light = 0,
    Dark = 1,
}

/// <summary>
/// Tenant-wide appearance policy. By default users pick their own color theme + light/dark mode
/// (persisted client-side in localStorage). When an admin turns on <see cref="ForceTheme"/> and/or
/// <see cref="ForceMode"/>, the chosen values are applied server-side before first paint and the
/// in-app theme switcher is hidden, so users can no longer change them.
///
/// Single settings row (<see cref="Id"/> fixed to <see cref="SingletonId"/>); <c>AppearanceService</c>
/// loads/caches it. Mirrors the <see cref="DevicePolicy"/> singleton pattern.
/// </summary>
public class AppearanceSettings
{
    /// <summary>The only valid primary key — this table holds exactly one row.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>The built-in theme keys offered to users and admins (must match the CSS themes).</summary>
    public static readonly string[] PresetThemes =
        { "neutral", "blue", "green", "orange", "rose", "slate", "sky", "azure" };

    /// <summary>When true, the color theme is admin-controlled and the user switcher is hidden.</summary>
    public bool ForceTheme { get; set; }

    /// <summary>Whether the forced theme is a preset or a custom color.</summary>
    public ThemeSource ThemeSource { get; set; } = ThemeSource.Preset;

    /// <summary>The forced preset theme key (when <see cref="ThemeSource"/> is Preset).</summary>
    public string PresetTheme { get; set; } = "blue";

    /// <summary>The forced custom primary color as a <c>#rrggbb</c> hex string (when Custom).</summary>
    public string CustomColorHex { get; set; } = "#2563eb";

    /// <summary>When true, light/dark mode is admin-controlled and the user toggle is hidden.</summary>
    public bool ForceMode { get; set; }

    /// <summary>The forced light/dark mode (when <see cref="ForceMode"/> is on).</summary>
    public ThemeModePreference Mode { get; set; } = ThemeModePreference.Light;

    /// <summary>
    /// Path (relative to web root, e.g. <c>/uploads/branding/logo.svg</c>) of a custom company logo
    /// that replaces the built-in 0VDI wordmark. Used in light mode, and as the fallback in dark mode
    /// when no dedicated dark logo is set. Null/empty = use the default logo.
    /// </summary>
    public string? LogoPath { get; set; }

    /// <summary>
    /// Optional path (relative to web root) of a custom logo shown in dark mode. When null/empty, the
    /// light <see cref="LogoPath"/> is used in dark mode too. Only meaningful when a light logo is set.
    /// </summary>
    public string? LogoPathDark { get; set; }

    /// <summary>Whether a custom (light/default) logo is configured.</summary>
    public bool HasCustomLogo => !string.IsNullOrWhiteSpace(LogoPath);

    /// <summary>Whether a dedicated dark-mode logo is configured.</summary>
    public bool HasCustomLogoDark => !string.IsNullOrWhiteSpace(LogoPathDark);

    /// <summary>The dark-mode logo to display: the dedicated dark logo if set, else the light one.</summary>
    public string? EffectiveDarkLogoPath => HasCustomLogoDark ? LogoPathDark : LogoPath;

    /// <summary>True if either the theme or the mode is admin-forced (used to hide the switcher).</summary>
    public bool IsAnythingForced => ForceTheme || ForceMode;
}
