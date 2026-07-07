// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Extended from the Identity scaffold to support two standalone MFA methods: the authenticator app
// (TOTP) and email one-time codes. The active method is chosen via the `method` route value; the user
// can switch between whichever methods they have enrolled. For email, a code is sent automatically when
// that method is selected. Verification goes through the generic SignInManager.TwoFactorSignInAsync so a
// single code box serves both providers.
#nullable disable

using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace KSol.ZeroVDI.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class LoginWith2faModel : PageModel
    {
        public const string AuthenticatorMethod = "authenticator";
        public const string EmailMethod = "email";

        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IEmailSender _emailSender;
        private readonly KSol.ZeroVDI.RDP.IAuditLogger _audit;
        private readonly ILogger<LoginWith2faModel> _logger;

        public LoginWith2faModel(
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            IEmailSender emailSender,
            KSol.ZeroVDI.RDP.IAuditLogger audit,
            ILogger<LoginWith2faModel> logger)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _emailSender = emailSender;
            _audit = audit;
            _logger = logger;
        }

        [BindProperty]
        public InputModel Input { get; set; }

        public bool RememberMe { get; set; }

        public string ReturnUrl { get; set; }

        /// <summary>Active method: "authenticator" or "email".</summary>
        public string Method { get; set; }

        /// <summary>Whether the user has the authenticator enrolled (controls the method switcher).</summary>
        public bool HasAuthenticator { get; set; }

        /// <summary>Whether the user has email MFA enrolled (controls the method switcher).</summary>
        public bool HasEmail { get; set; }

        /// <summary>Masked destination shown when the email method is active.</summary>
        public string MaskedEmail { get; set; }

        public class InputModel
        {
            [Required]
            [StringLength(8, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.", MinimumLength = 6)]
            [DataType(DataType.Text)]
            [Display(Name = "Verification code")]
            public string TwoFactorCode { get; set; }

            [Display(Name = "Remember this machine")]
            public bool RememberMachine { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(bool rememberMe, string returnUrl = null, string method = null)
        {
            // Ensure the user has gone through the username & password screen first.
            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync()
                ?? throw new InvalidOperationException("Unable to load two-factor authentication user.");

            await PopulateMethodsAsync(user);
            Method = ResolveMethod(method);
            ReturnUrl = returnUrl;
            RememberMe = rememberMe;

            // For the email method, issue a code as soon as the page is shown.
            if (Method == EmailMethod)
            {
                await SendEmailCodeAsync(user);
            }

            return Page();
        }

        public async Task<IActionResult> OnPostAsync(bool rememberMe, string returnUrl = null, string method = null)
        {
            returnUrl ??= Url.Content("~/");

            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync()
                ?? throw new InvalidOperationException("Unable to load two-factor authentication user.");

            await PopulateMethodsAsync(user);
            Method = ResolveMethod(method);
            ReturnUrl = returnUrl;
            RememberMe = rememberMe;

            if (!ModelState.IsValid)
                return Page();

            var code = Input.TwoFactorCode.Replace(" ", string.Empty).Replace("-", string.Empty);
            var userId = await _userManager.GetUserIdAsync(user);

            Microsoft.AspNetCore.Identity.SignInResult result;
            if (Method == EmailMethod)
            {
                result = await _signInManager.TwoFactorSignInAsync(
                    TokenOptions.DefaultEmailProvider, code, rememberMe, Input.RememberMachine);
            }
            else
            {
                result = await _signInManager.TwoFactorAuthenticatorSignInAsync(
                    code, rememberMe, Input.RememberMachine);
            }

            if (result.Succeeded)
            {
                _logger.LogInformation("User with ID '{UserId}' logged in with 2fa ({Method}).", user.Id, Method);
                await _audit.LogAsync(Models.AuditCategory.Authentication, "LoginTwoFactorSucceeded",
                    actorUserId: userId, actorName: user.UserName, detail: new { method = Method });
                return LocalRedirect(returnUrl);
            }
            if (result.IsLockedOut)
            {
                _logger.LogWarning("User with ID '{UserId}' account locked out.", user.Id);
                await _audit.LogAsync(Models.AuditCategory.Authentication, "LoginLockedOut",
                    success: false, actorUserId: userId, actorName: user.UserName);
                return RedirectToPage("./Lockout");
            }

            _logger.LogWarning("Invalid 2fa code entered for user with ID '{UserId}' ({Method}).", user.Id, Method);
            await _audit.LogAsync(Models.AuditCategory.Authentication, "LoginTwoFactorFailed",
                success: false, actorUserId: userId, actorName: user.UserName, detail: new { method = Method });
            ModelState.AddModelError(string.Empty, "Invalid verification code.");
            return Page();
        }

        /// <summary>Resend handler for the email method.</summary>
        public async Task<IActionResult> OnPostResendAsync(bool rememberMe, string returnUrl = null)
        {
            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync()
                ?? throw new InvalidOperationException("Unable to load two-factor authentication user.");

            await PopulateMethodsAsync(user);
            Method = EmailMethod;
            ReturnUrl = returnUrl;
            RememberMe = rememberMe;
            ModelState.Clear();
            await SendEmailCodeAsync(user);
            return Page();
        }

        private async Task PopulateMethodsAsync(ApplicationUser user)
        {
            HasAuthenticator = await _userManager.GetAuthenticatorKeyAsync(user) != null;
            HasEmail = user.EmailTwoFactorEnabled;
            MaskedEmail = Mask(await _userManager.GetEmailAsync(user));
        }

        /// <summary>Pick the requested method if the user has it; otherwise fall back to whatever they have.</summary>
        private string ResolveMethod(string requested)
        {
            if (requested == EmailMethod && HasEmail) return EmailMethod;
            if (requested == AuthenticatorMethod && HasAuthenticator) return AuthenticatorMethod;
            // Default: prefer the authenticator, else email.
            return HasAuthenticator ? AuthenticatorMethod : EmailMethod;
        }

        private async Task SendEmailCodeAsync(ApplicationUser user)
        {
            var token = await _userManager.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider);
            var email = await _userManager.GetEmailAsync(user);
            var body =
                $"<p>Your sign-in verification code is:</p>" +
                $"<p style=\"font-size:24px;font-weight:bold;letter-spacing:3px\">{HtmlEncoder.Default.Encode(token)}</p>" +
                $"<p>This code expires in a few minutes. If you did not try to sign in, change your password immediately.</p>";
            await _emailSender.SendEmailAsync(email, "Your ZeroVDI sign-in code", body);
            _logger.LogInformation("Sent email-2FA sign-in code to user with ID '{UserId}'.", user.Id);
        }

        private static string Mask(string email)
        {
            if (string.IsNullOrEmpty(email)) return email;
            var at = email.IndexOf('@');
            if (at <= 1) return email;
            return email[0] + new string('•', Math.Min(at - 1, 6)) + email[at..];
        }
    }
}
