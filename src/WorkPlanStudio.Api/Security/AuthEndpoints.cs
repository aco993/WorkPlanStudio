using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Persistence;

namespace WorkPlanStudio.Api.Security;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapWorkPlanIdentity(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        var group = endpoints.MapGroup("/api/auth").RequireRateLimiting("auth");

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
        }).AllowAnonymous();

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
            var result = await signIn.PasswordSignInAsync(user, request.Password, false, true);
            if (!result.Succeeded)
                return Results.Json(new ApiError("invalid_credentials", "Invalid credentials."), statusCode: StatusCodes.Status401Unauthorized);
            var roles = await users.GetRolesAsync(user);
            return Results.Ok(new UserInfo(user.Id, user.Email!, roles.ToArray()));
        }).AllowAnonymous();

        group.MapPost("/logout", async (SignInManager<ApplicationUser> signIn, IAntiforgery antiforgery, HttpContext context) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await signIn.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        group.MapGet("/me", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive)
                return Results.Unauthorized();
            var roles = await users.GetRolesAsync(user);
            return Results.Ok(new UserInfo(user.Id, user.Email ?? "", roles.ToArray()));
        }).RequireAuthorization();

        return endpoints;
    }

    public static string RequiredUserId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated user has no subject identifier.");
}
