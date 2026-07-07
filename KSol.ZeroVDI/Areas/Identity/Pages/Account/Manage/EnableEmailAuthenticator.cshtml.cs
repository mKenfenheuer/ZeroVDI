// Email-based MFA enrollment. Mirrors EnableAuthenticator (TOTP) but uses the built-in "Email" token
// provider: we mail a one-time code to the user's account email, they enter it, and on success we flip
// the standalone EmailTwoFactorEnabled flag (and the Identity master TwoFactorEnabled switch).
#nullable disable

using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace KSol.ZeroVDI.Areas.Identity.Pages.Account.Manage
{
    public class EnableEmailAuthenticatorModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IEmailSender _emailSender;
        private readonly ILogger<EnableEmailAuthenticatorModel> _logger;
        private readonly KSol.ZeroVDI.RDP.IAuditLogger _audit;

        public EnableEmailAuthenticatorModel(
            UserManager<ApplicationUser> userManager,
            IEmailSender emailSender,
            ILogger<EnableEmailAuthenticatorModel> logger,
            KSol.ZeroVDI.RDP.IAuditLogger audit)
        {
            _userManager = userManager;
            _emailSender = emailSender;
            _logger = logger;
            _audit = audit;
        }

        /// <summary>Masked destination address shown on the form, e.g. "j••••@example.com".</summary>
        public string MaskedEmail { get; set; }

        /// <summary>True once a code has been sent and we are awaiting verification.</summary>
        public bool CodeSent { get; set; }

        [TempData]
        public string StatusMessage { get; set; }

        [BindProperty]
        public InputModel Input { get; set; }

        public class InputModel
        {
            [Required]
            [StringLength(7, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.", MinimumLength = 6)]
            [DataType(DataType.Text)]
            [Display(Name = "Verification code")]
            public string Code { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");

            MaskedEmail = Mask(await _userManager.GetEmailAsync(user));
            return Page();
        }

        /// <summary>Send (or resend) the enrollment verification code to the user's account email.</summary>
        public async Task<IActionResult> OnPostSendCodeAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");

            var email = await _userManager.GetEmailAsync(user);
            MaskedEmail = Mask(email);
            await SendCodeAsync(user, email);
            CodeSent = true;
            ModelState.Clear();
            return Page();
        }

        public async Task<IActionResult> OnPostVerifyAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return NotFound($"Unable to load user with ID '{_userManager.GetUserId(User)}'.");

            MaskedEmail = Mask(await _userManager.GetEmailAsync(user));
            CodeSent = true;

            if (!ModelState.IsValid)
                return Page();

            var code = Input.Code.Replace(" ", string.Empty).Replace("-", string.Empty);
            var valid = await _userManager.VerifyTwoFactorTokenAsync(
                user, TokenOptions.DefaultEmailProvider, code);

            if (!valid)
            {
                ModelState.AddModelError("Input.Code", "Verification code is invalid or has expired.");
                return Page();
            }

            user.EmailTwoFactorEnabled = true;
            await _userManager.UpdateAsync(user);
            // Master switch: Identity will only run the 2FA challenge when this is on.
            await _userManager.SetTwoFactorEnabledAsync(user, true);

            _logger.LogInformation("User with ID '{UserId}' enabled email-based 2FA.", user.Id);
            await _audit.LogAsync(Models.AuditCategory.Authentication, "MfaEmailEnabled",
                actorUserId: user.Id, actorName: user.UserName);

            StatusMessage = "Email authentication has been enabled.";

            // Mirror the authenticator flow: ensure the user has recovery codes.
            if (await _userManager.CountRecoveryCodesAsync(user) == 0)
            {
                await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            }
            return RedirectToPage("./TwoFactorAuthentication");
        }

        private async Task SendCodeAsync(ApplicationUser user, string email)
        {
            var token = await _userManager.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider);
            var subject = "Your ZeroVDI verification code";
            var body =
                $"<p>Your verification code is:</p>" +
                $"<p style=\"font-size:24px;font-weight:bold;letter-spacing:3px\">{HtmlEncoder.Default.Encode(token)}</p>" +
                $"<p>This code expires in a few minutes. If you did not request it, you can ignore this email.</p>";
            await _emailSender.SendEmailAsync(email, subject, body);
            _logger.LogInformation("Sent email-2FA enrollment code to user with ID '{UserId}'.", user.Id);
        }

        /// <summary>Mask an email for display: keep first char of local part + domain.</summary>
        private static string Mask(string email)
        {
            if (string.IsNullOrEmpty(email)) return email;
            var at = email.IndexOf('@');
            if (at <= 1) return email;
            return email[0] + new string('•', Math.Min(at - 1, 6)) + email[at..];
        }
    }
}
