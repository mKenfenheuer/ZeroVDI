// Removes the email MFA method. If the user has no other enrolled method (no authenticator) afterwards,
// the Identity master TwoFactorEnabled switch is cleared too so they aren't left with 2FA "on" but no way
// to satisfy a challenge.
#nullable disable

using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace KSol.ZeroVDI.Areas.Identity.Pages.Account.Manage
{
    public class DisableEmailAuthenticatorModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<DisableEmailAuthenticatorModel> _logger;
        private readonly KSol.ZeroVDI.RDP.IAuditLogger _audit;

        public DisableEmailAuthenticatorModel(
            UserManager<ApplicationUser> userManager,
            ILogger<DisableEmailAuthenticatorModel> logger,
            KSol.ZeroVDI.RDP.IAuditLogger audit)
        {
            _userManager = userManager;
            _logger = logger;
            _audit = audit;
        }

        [TempData]
        public string StatusMessage { get; set; }

        public async Task<IActionResult> OnGet()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");
            if (!user.EmailTwoFactorEnabled)
                return RedirectToPage("./TwoFactorAuthentication");
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");

            user.EmailTwoFactorEnabled = false;
            await _userManager.UpdateAsync(user);

            // If the authenticator app is also gone, no MFA method remains → turn the master switch off.
            var hasAuthenticator = await _userManager.GetAuthenticatorKeyAsync(user) != null;
            if (!hasAuthenticator)
                await _userManager.SetTwoFactorEnabledAsync(user, false);

            _logger.LogInformation("User with ID '{UserId}' disabled email-based 2FA.", user.Id);
            await _audit.LogAsync(Models.AuditCategory.Authentication, "MfaEmailDisabled",
                actorUserId: user.Id, actorName: user.UserName);

            StatusMessage = "Email authentication has been disabled.";
            return RedirectToPage("./TwoFactorAuthentication");
        }
    }
}
