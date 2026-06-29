using System.Globalization;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using Microsoft.EntityFrameworkCore;

namespace KSol.RDPGateway.RDP;

/// <summary>
/// Loads, caches and applies the tenant-wide <see cref="AppearanceSettings"/>. Read on every page
/// render (via <c>_ThemeHead</c>), so it is cached in memory and only re-read after a save (via
/// <see cref="Invalidate"/>). Singleton; opens its own short-lived scope to read the scoped DbContext.
/// Mirrors <see cref="DevicePolicyService"/>.
/// </summary>
public sealed class AppearanceService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _gate = new();
    private AppearanceSettings? _cached;

    public AppearanceService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>The current settings, loading the singleton row (or defaults) on first use.</summary>
    public AppearanceSettings Get()
    {
        var cached = _cached;
        if (cached != null) return cached;

        lock (_gate)
        {
            if (_cached != null) return _cached;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = db.AppearanceSettings.AsNoTracking()
                .FirstOrDefault(a => a.Id == AppearanceSettings.SingletonId);
            _cached = settings ?? new AppearanceSettings();
            return _cached;
        }
    }

    /// <summary>Drop the cache so the next <see cref="Get"/> re-reads from the DB.</summary>
    public void Invalidate()
    {
        lock (_gate) _cached = null;
    }

    /// <summary>
    /// Builds inline CSS that re-derives a full color theme from a single custom primary color, for
    /// both light and dark mode, mirroring the structure of the built-in presets (see the "rose"
    /// theme in <c>theme.css</c>). The custom hex is converted to HSL and its <em>hue</em> drives an
    /// entire family of related surfaces — primary, secondary/muted/accent tints, their foregrounds,
    /// borders/inputs, the focus ring, and the matching sidebar variables — so the whole UI shifts
    /// cohesively rather than just the primary button. Returns an empty string when the color cannot
    /// be parsed. The text-on-primary foreground flips between near-black and near-white based on the
    /// primary's perceived luminance for legibility.
    /// </summary>
    public static string BuildCustomColorCss(string hex)
    {
        if (!TryParseHex(hex, out var r, out var g, out var b))
        {
            return string.Empty;
        }

        RgbToHsl(r, g, b, out var h, out var s, out _);
        // The primary's own lightness is fixed for a consistent brand weight regardless of the input
        // color's lightness; only the hue (and, lightly clamped, the saturation) carry over.
        s = Math.Clamp(s, 35, 95);

        var hue = F(h);
        var sat = F(s) + "%";

        // Relative luminance of the primary at its rendered lightness → readable on-primary text.
        // Recompute from the canonical primary so foreground tracks what is actually displayed.
        HslToRgb(h, s, 49.8, out var pr, out var pg, out var pb);
        var luminance = (0.2126 * pr + 0.7152 * pg + 0.0722 * pb) / 255.0;
        var onPrimaryLight = luminance > 0.6 ? "hsl(0 0% 10%)" : "hsl(0 0% 98%)";

        // Light mode: vivid primary, faint hue-tinted neutrals, hue-tinted borders. Mirrors the
        // structure of the preset themes (e.g. "rose") so every surface is on-brand.
        var light = $$"""
            :root[data-theme] {
                --background: hsl(0 0% 100%);
                --foreground: hsl({{hue}} 5% 4%);
                --card: hsl(0 0% 100%);
                --card-foreground: hsl({{hue}} 5% 4%);
                --popover: hsl(0 0% 100%);
                --popover-foreground: hsl({{hue}} 5% 4%);
                --primary: hsl({{hue}} {{sat}} 49.8%);
                --primary-foreground: {{onPrimaryLight}};
                --secondary: hsl({{hue}} 40% 96%);
                --secondary-foreground: hsl({{hue}} {{sat}} 30%);
                --muted: hsl({{hue}} 40% 96%);
                --muted-foreground: hsl({{hue}} 5% 45%);
                --accent: hsl({{hue}} 40% 96%);
                --accent-foreground: hsl({{hue}} {{sat}} 30%);
                --border: hsl({{hue}} 20% 90%);
                --input: hsl({{hue}} 20% 90%);
                --ring: hsl({{hue}} {{sat}} 49.8%);
                --sidebar: hsl(0 0% 100%);
                --sidebar-foreground: hsl({{hue}} 5% 4%);
                --sidebar-primary: hsl({{hue}} {{sat}} 49.8%);
                --sidebar-primary-foreground: {{onPrimaryLight}};
                --sidebar-accent: hsl({{hue}} 40% 96%);
                --sidebar-accent-foreground: hsl({{hue}} {{sat}} 30%);
                --sidebar-border: hsl({{hue}} 20% 90%);
                --sidebar-ring: hsl({{hue}} {{sat}} 49.8%);
            }
            """;

        // Dark mode: slightly lighter/brighter primary, very dark hue-tinted neutrals.
        var dark = $$"""
            :root[data-theme].dark, .dark:root[data-theme] {
                --background: hsl({{hue}} 5% 6%);
                --foreground: hsl(0 0% 95%);
                --card: hsl({{hue}} 5% 10%);
                --card-foreground: hsl(0 0% 95%);
                --popover: hsl({{hue}} 5% 10%);
                --popover-foreground: hsl(0 0% 95%);
                --primary: hsl({{hue}} {{sat}} 55%);
                --primary-foreground: hsl({{hue}} 5% 6%);
                --secondary: hsl({{hue}} 10% 15%);
                --secondary-foreground: hsl(0 0% 95%);
                --muted: hsl({{hue}} 10% 15%);
                --muted-foreground: hsl({{hue}} 5% 60%);
                --accent: hsl({{hue}} 10% 15%);
                --accent-foreground: hsl(0 0% 95%);
                --border: hsl({{hue}} 10% 17%);
                --input: hsl({{hue}} 10% 17%);
                --ring: hsl({{hue}} {{sat}} 55%);
                --sidebar: hsl({{hue}} 5% 8%);
                --sidebar-foreground: hsl(0 0% 95%);
                --sidebar-primary: hsl({{hue}} {{sat}} 55%);
                --sidebar-primary-foreground: hsl(0 0% 95%);
                --sidebar-accent: hsl({{hue}} 10% 15%);
                --sidebar-accent-foreground: hsl(0 0% 95%);
                --sidebar-border: hsl({{hue}} 10% 17%);
                --sidebar-ring: hsl({{hue}} {{sat}} 55%);
            }
            """;

        return light + "\n" + dark;
    }

    /// <summary>Formats a number for CSS with an invariant decimal point (e.g. <c>"346.8"</c>).</summary>
    private static string F(double v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Converts 8-bit sRGB to HSL with hue in [0,360) and S/L in [0,100].</summary>
    private static void RgbToHsl(int r8, int g8, int b8, out double h, out double s, out double l)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        l = (max + min) / 2.0;

        if (d == 0)
        {
            h = 0;
            s = 0;
        }
        else
        {
            s = d / (1 - Math.Abs(2 * l - 1));
            if (max == r) h = ((g - b) / d % 6 + 6) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }

        s *= 100;
        l *= 100;
    }

    /// <summary>Converts HSL (hue [0,360), S/L [0,100]) back to 8-bit sRGB.</summary>
    private static void HslToRgb(double h, double s, double l, out int r8, out int g8, out int b8)
    {
        s /= 100; l /= 100;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        r8 = (int)Math.Round((r + m) * 255);
        g8 = (int)Math.Round((g + m) * 255);
        b8 = (int)Math.Round((b + m) * 255);
    }

    private static bool TryParseHex(string? hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3)
        {
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        }
        if (s.Length != 6) return false;
        return int.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && int.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && int.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }
}
