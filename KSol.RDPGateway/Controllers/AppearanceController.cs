using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KSol.RDPGateway.Data;
using KSol.RDPGateway.Models;
using KSol.RDPGateway.RDP;

namespace KSol.RDPGateway.Controllers;

/// <summary>
/// Admin editor for tenant-wide appearance: a forced color theme (preset or custom hex), forced
/// light/dark mode, and a custom company logo. When the theme/mode is forced, the per-user switcher
/// is hidden (see _NavActions) and the forced value is applied server-side before paint (_ThemeHead).
/// Admin-only.
/// </summary>
[Authorize(Roles = "Admin")]
[Route("admin/appearance")]
public class AppearanceController : Controller
{
    private static readonly string[] AllowedLogoExtensions = { ".svg", ".png", ".jpg", ".jpeg", ".webp", ".gif" };
    private const long MaxLogoBytes = 2 * 1024 * 1024; // 2 MB

    private readonly ApplicationDbContext _context;
    private readonly AppearanceService _appearance;
    private readonly IWebHostEnvironment _env;
    private readonly IAuditLogger _audit;

    public AppearanceController(ApplicationDbContext context, AppearanceService appearance,
        IWebHostEnvironment env, IAuditLogger audit)
    {
        _context = context;
        _appearance = appearance;
        _env = env;
        _audit = audit;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var settings = await _context.AppearanceSettings
            .FirstOrDefaultAsync(a => a.Id == AppearanceSettings.SingletonId) ?? new AppearanceSettings();
        return View(settings);
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(AppearanceSettings input)
    {
        var (settings, _) = await GetOrCreateAsync();

        settings.ForceTheme = input.ForceTheme;
        settings.ThemeSource = input.ThemeSource;
        settings.PresetTheme = AppearanceSettings.PresetThemes.Contains(input.PresetTheme)
            ? input.PresetTheme : "blue";
        settings.CustomColorHex = NormalizeHex(input.CustomColorHex) ?? settings.CustomColorHex;
        settings.ForceMode = input.ForceMode;
        settings.Mode = input.Mode;
        // LogoPath is managed by the upload/remove actions, not this form.

        await _context.SaveChangesAsync();
        _appearance.Invalidate();

        await _audit.LogAsync(AuditCategory.Admin, "AppearanceUpdated", detail: new
        {
            settings.ForceTheme,
            settings.ThemeSource,
            settings.PresetTheme,
            settings.CustomColorHex,
            settings.ForceMode,
            settings.Mode,
        });

        TempData["Status"] = "Appearance settings saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("logo")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadLogo(IFormFile? logo, string? variant)
    {
        var dark = string.Equals(variant, "dark", StringComparison.OrdinalIgnoreCase);

        if (logo == null || logo.Length == 0)
        {
            TempData["Error"] = "Please choose a file to upload.";
            return RedirectToAction(nameof(Index));
        }
        if (logo.Length > MaxLogoBytes)
        {
            TempData["Error"] = "Logo must be 2 MB or smaller.";
            return RedirectToAction(nameof(Index));
        }

        var ext = Path.GetExtension(logo.FileName).ToLowerInvariant();
        if (!AllowedLogoExtensions.Contains(ext))
        {
            TempData["Error"] = "Unsupported file type. Use SVG, PNG, JPG, WEBP, or GIF.";
            return RedirectToAction(nameof(Index));
        }

        var dir = Path.Combine(_env.WebRootPath, "uploads", "branding");
        Directory.CreateDirectory(dir);

        // Stable, unguessable-ish file name with a cache-busting token so a replaced logo refreshes.
        var fileName = $"logo-{(dark ? "dark-" : "")}{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
        var fullPath = Path.Combine(dir, fileName);

        var (settings, _) = await GetOrCreateAsync();
        DeleteExistingLogo(settings, dark);

        await using (var stream = System.IO.File.Create(fullPath))
        {
            await logo.CopyToAsync(stream);
        }

        var path = $"/uploads/branding/{fileName}";
        if (dark) settings.LogoPathDark = path;
        else settings.LogoPath = path;
        await _context.SaveChangesAsync();
        _appearance.Invalidate();

        await _audit.LogAsync(AuditCategory.Admin, "AppearanceLogoUpdated", detail: new { variant = dark ? "dark" : "light", path });

        TempData["Status"] = dark ? "Custom dark-mode logo uploaded." : "Custom logo uploaded.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("logo/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveLogo(string? variant)
    {
        var dark = string.Equals(variant, "dark", StringComparison.OrdinalIgnoreCase);
        var (settings, created) = await GetOrCreateAsync();
        var hasLogo = dark ? settings.HasCustomLogoDark : settings.HasCustomLogo;
        if (!created && hasLogo)
        {
            DeleteExistingLogo(settings, dark);
            if (dark)
            {
                settings.LogoPathDark = null;
            }
            else
            {
                // Removing the light logo also clears the dark one — a dark-only logo makes no sense.
                DeleteExistingLogo(settings, true);
                settings.LogoPath = null;
                settings.LogoPathDark = null;
            }
            await _context.SaveChangesAsync();
            _appearance.Invalidate();
            await _audit.LogAsync(AuditCategory.Admin, "AppearanceLogoRemoved", detail: new { variant = dark ? "dark" : "light" });
            TempData["Status"] = dark
                ? "Dark-mode logo removed. The light logo is used in both modes."
                : "Custom logo removed. The default logo is back.";
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<(AppearanceSettings settings, bool created)> GetOrCreateAsync()
    {
        var settings = await _context.AppearanceSettings
            .FirstOrDefaultAsync(a => a.Id == AppearanceSettings.SingletonId);
        if (settings != null)
        {
            return (settings, false);
        }
        settings = new AppearanceSettings { Id = AppearanceSettings.SingletonId };
        _context.AppearanceSettings.Add(settings);
        return (settings, true);
    }

    private void DeleteExistingLogo(AppearanceSettings settings, bool dark)
    {
        var current = dark ? settings.LogoPathDark : settings.LogoPath;
        if (string.IsNullOrWhiteSpace(current))
        {
            return;
        }
        try
        {
            var existing = Path.Combine(_env.WebRootPath, current.TrimStart('/')
                .Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(existing))
            {
                System.IO.File.Delete(existing);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover file is harmless.
        }
    }

    private static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }
        var s = hex.Trim();
        if (!s.StartsWith('#'))
        {
            s = "#" + s;
        }
        // #rgb or #rrggbb, hex digits only.
        return System.Text.RegularExpressions.Regex.IsMatch(s, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")
            ? s.ToLowerInvariant() : null;
    }
}
