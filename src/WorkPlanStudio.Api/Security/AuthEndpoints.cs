using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Persistence;

namespace WorkPlanStudio.Api.Security;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapWorkPlanIdentity(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        var group = endpoints.MapGroup("/api/auth");

        group.MapGet("/antiforgery", (IAntiforgery antiforgery, HttpContext context) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new AntiforgeryToken(tokens.RequestToken!));
        }).AllowAnonymous();

        group.MapPost("/register", async (
            AuthRequest request,
            UserManager<ApplicationUser> users,
            IAntiforgery antiforgery,
            HttpContext context) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            if (!configuration.GetValue("Identity:AllowRegistration", false))
                return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest(new ApiError("invalid_registration", "Email and password are required."));

            var user = new ApplicationUser
            {
                UserName = request.Email.Trim(),
                Email = request.Email.Trim(),
                EmailConfirmed = true,
                CreatedUtc = DateTime.UtcNow
            };
            var result = await users.CreateAsync(user, request.Password);
            return result.Succeeded
                ? Results.Created("/api/auth/me", new UserInfo(user.Id, user.Email!, []))
                : Results.BadRequest(new ApiError(
                    "registration_failed",
                    "Registration failed.",
                    result.Errors.GroupBy(error => error.Code)
                        .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray())));
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/login", async (
            AuthRequest request,
            SignInManager<ApplicationUser> signIn,
            UserManager<ApplicationUser> users,
            IAntiforgery antiforgery,
            HttpContext context) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var user = await users.FindByEmailAsync(request.Email.Trim());
            if (user is null || !user.IsActive)
                return Results.Json(new ApiError("invalid_credentials", "Invalid credentials."), statusCode: StatusCodes.Status401Unauthorized);
            var passwordResult = await signIn.CheckPasswordSignInAsync(user, request.Password, true);
            if (!passwordResult.Succeeded)
                return Results.Json(new ApiError("invalid_credentials", "Invalid credentials."), statusCode: StatusCodes.Status401Unauthorized);
            if (user.TwoFactorEnabled)
            {
                var secondFactorValid = false;
                if (!string.IsNullOrWhiteSpace(request.RecoveryCode))
                    secondFactorValid = (await users.RedeemTwoFactorRecoveryCodeAsync(
                        user, request.RecoveryCode.Replace(" ", "", StringComparison.Ordinal))).Succeeded;
                else if (!string.IsNullOrWhiteSpace(request.TwoFactorCode))
                    secondFactorValid = await users.VerifyTwoFactorTokenAsync(
                        user, TokenOptions.DefaultAuthenticatorProvider,
                        request.TwoFactorCode.Replace(" ", "", StringComparison.Ordinal));
                else
                    return Results.Json(new ApiError("two_factor_required", "An authenticator or recovery code is required."), statusCode: StatusCodes.Status401Unauthorized);
                if (!secondFactorValid)
                    return Results.Json(new ApiError("invalid_two_factor_code", "The authenticator or recovery code is invalid."), statusCode: StatusCodes.Status401Unauthorized);
            }
            await signIn.SignInAsync(user, false);
            var roles = await users.GetRolesAsync(user);
            return Results.Ok(new UserInfo(user.Id, user.Email!, roles.ToArray()));
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/password-reset/request", async (
            PasswordResetRequest request,
            UserManager<ApplicationUser> users,
            IEmailDelivery emailDelivery,
            IOptions<EmailDeliveryOptions> emailOptions,
            ILoggerFactory loggerFactory,
            IAntiforgery antiforgery,
            HttpContext context) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var logger = loggerFactory.CreateLogger("PasswordReset");
            var email = request.Email?.Trim() ?? "";
            var user = string.IsNullOrWhiteSpace(email) ? null : await users.FindByEmailAsync(email);
            if (user is not null && user.IsActive && emailDelivery.IsConfigured)
            {
                var token = await users.GeneratePasswordResetTokenAsync(user);
                var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
                var baseUrl = emailOptions.Value.PublicBaseUrl.TrimEnd('/');
                var resetUrl = $"{baseUrl}/?resetEmail={Uri.EscapeDataString(email)}&resetToken={Uri.EscapeDataString(encodedToken)}";
                try
                {
                    await emailDelivery.SendPasswordResetAsync(email, resetUrl, context.RequestAborted);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Password reset email delivery failed");
                }
            }
            else if (user is not null && user.IsActive)
            {
                logger.LogError("Password reset email was requested, but SMTP delivery is not configured");
            }

            // Always return the same response to prevent account enumeration.
            return Results.Accepted();
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/password-reset/confirm", async (
            PasswordResetConfirmRequest request,
            UserManager<ApplicationUser> users,
            IAntiforgery antiforgery,
            HttpContext context) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var user = await users.FindByEmailAsync(request.Email.Trim());
            if (user is null || !user.IsActive)
                return Results.BadRequest(new ApiError("password_reset_failed", "The password reset link is invalid or expired."));
            string token;
            try
            {
                token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token));
            }
            catch (FormatException)
            {
                return Results.BadRequest(new ApiError("password_reset_failed", "The password reset link is invalid or expired."));
            }
            var result = await users.ResetPasswordAsync(user, token, request.NewPassword);
            return result.Succeeded
                ? Results.NoContent()
                : Results.BadRequest(new ApiError(
                    "password_reset_failed",
                    "The password reset link is invalid, expired, or the new password does not meet policy.",
                    result.Errors.GroupBy(error => error.Code)
                        .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray())));
        }).AllowAnonymous().RequireRateLimiting("auth");

        group.MapPost("/logout", async (SignInManager<ApplicationUser> signIn, IAntiforgery antiforgery, HttpContext context) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await signIn.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("api");

        group.MapGet("/me", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive)
                return Results.Unauthorized();
            var roles = await users.GetRolesAsync(user);
            return Results.Ok(new UserInfo(user.Id, user.Email ?? "", roles.ToArray()));
        }).RequireAuthorization().RequireRateLimiting("api");

        group.MapGet("/mfa/status", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();
            var key = await users.GetAuthenticatorKeyAsync(user);
            var codes = user.TwoFactorEnabled ? await users.CountRecoveryCodesAsync(user) : 0;
            return Results.Ok(new MfaStatusDto(user.TwoFactorEnabled, !string.IsNullOrWhiteSpace(key), codes));
        }).RequireAuthorization().RequireRateLimiting("api");

        group.MapPost("/mfa/setup", async (
            MfaPasswordRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> users,
            IAntiforgery antiforgery,
            HttpContext context) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();
            if (!await users.CheckPasswordAsync(user, request.CurrentPassword))
                return Results.Json(new ApiError("reauthentication_failed", "The current password is invalid."), statusCode: StatusCodes.Status403Forbidden);
            var key = await users.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrWhiteSpace(key))
            {
                await users.ResetAuthenticatorKeyAsync(user);
                key = await users.GetAuthenticatorKeyAsync(user);
            }
            var account = user.Email ?? user.UserName ?? user.Id;
            var uri = $"otpauth://totp/{UrlEncoder.Default.Encode("WorkPlan Studio")}:{UrlEncoder.Default.Encode(account)}?secret={key}&issuer={UrlEncoder.Default.Encode("WorkPlan Studio")}&digits=6";
            return Results.Ok(new MfaSetupDto(key!, uri));
        }).RequireAuthorization().RequireRateLimiting("api");

        group.MapPost("/mfa/enable", async (
            MfaEnableRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> users,
            IAntiforgery antiforgery,
            HttpContext context) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();
            if (!await users.CheckPasswordAsync(user, request.CurrentPassword))
                return Results.Json(new ApiError("reauthentication_failed", "The current password is invalid."), statusCode: StatusCodes.Status403Forbidden);
            var valid = await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, request.Code.Replace(" ", "", StringComparison.Ordinal));
            if (!valid)
                return Results.BadRequest(new ApiError("invalid_authenticator_code", "The authenticator code is invalid."));
            await users.SetTwoFactorEnabledAsync(user, true);
            var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToArray() ?? [];
            return Results.Ok(new MfaRecoveryCodesDto(codes));
        }).RequireAuthorization().RequireRateLimiting("api");

        group.MapPost("/mfa/recovery-codes", async (
            MfaPasswordRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> users,
            IAntiforgery antiforgery,
            HttpContext context) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();
            if (!user.TwoFactorEnabled)
                return Results.BadRequest(new ApiError("mfa_not_enabled", "Multi-factor authentication is not enabled."));
            if (!await users.CheckPasswordAsync(user, request.CurrentPassword))
                return Results.Json(new ApiError("reauthentication_failed", "The current password is invalid."), statusCode: StatusCodes.Status403Forbidden);
            var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToArray() ?? [];
            return Results.Ok(new MfaRecoveryCodesDto(codes));
        }).RequireAuthorization().RequireRateLimiting("api");

        group.MapPost("/mfa/disable", async (
            MfaEnableRequest request,
            ClaimsPrincipal principal,
            UserManager<ApplicationUser> users,
            IAntiforgery antiforgery,
            HttpContext context) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var user = await users.GetUserAsync(principal);
            if (user is null)
                return Results.Unauthorized();
            if (!await users.CheckPasswordAsync(user, request.CurrentPassword) ||
                !await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, request.Code.Replace(" ", "", StringComparison.Ordinal)))
                return Results.Json(new ApiError("reauthentication_failed", "Password or authenticator code is invalid."), statusCode: StatusCodes.Status403Forbidden);
            await users.SetTwoFactorEnabledAsync(user, false);
            await users.ResetAuthenticatorKeyAsync(user);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("api");

        return endpoints;
    }

    public static string RequiredUserId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated user has no subject identifier.");
}
