using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using WorkPlanStudio.Api.Auth;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Api.Http;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Api.Endpoints;

/// <summary>Sign-in, token rotation, sign-out and account creation.</summary>
public static class AuthEndpoints
{
    /// <summary>Rate-limiting policy applied to the routes a stranger can reach.</summary>
    public const string RateLimitPolicy = "auth";

    /// <summary>
    /// Maps the auth routes.
    /// <para>
    /// Login and refresh answer identically whether the account does not exist,
    /// the password is wrong or the token is stale: one 401, one wording. The
    /// difference is only interesting to somebody who does not already know the
    /// answer.
    /// </para>
    /// </summary>
    /// <param name="app">The route builder to add to.</param>
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/auth").WithTags("Authentication");

        group.MapPost("/login", async (
            LoginRequest request,
            UserManager<ApiUser> users,
            SignInManager<ApiUser> signIn,
            TokenService tokens,
            ILoggerFactory loggers,
            CancellationToken cancellationToken) =>
        {
            var log = loggers.CreateLogger("WorkPlanStudio.Api.Auth");

            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
                return Unauthorized();

            var user = await users.FindByNameAsync(request.UserName);
            if (user is null)
            {
                // No user record to count a failure against, so the lockout
                // counter cannot help here; the rate limiter on this route is
                // what makes guessing user names expensive.
                log.LogInformation("Sign-in rejected for an unknown account");
                return Unauthorized();
            }

            var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                log.LogInformation(
                    "Sign-in rejected for {UserId} (lockedOut: {LockedOut})", user.Id, result.IsLockedOut);
                return Unauthorized();
            }

            var roles = await users.GetRolesAsync(user);
            var issued = await tokens.IssueAsync(user, [.. roles], cancellationToken);
            log.LogInformation("Signed in {UserId}", user.Id);
            return Results.Ok(issued);
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicy)
        .WithSummary("Exchanges a user name and password for an access/refresh pair.");

        group.MapPost("/refresh", async (
            RefreshRequest request,
            UserManager<ApiUser> users,
            TokenService tokens,
            CancellationToken cancellationToken) =>
        {
            var result = await tokens.RefreshAsync(
                request.RefreshToken,
                async (user, token) => [.. await users.GetRolesAsync(user)],
                cancellationToken);

            return result is { Outcome: RefreshOutcome.Rotated, Tokens: not null }
                ? Results.Ok(result.Tokens)
                : Unauthorized();
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicy)
        .WithSummary("Rotates a refresh token. The token presented is dead afterwards, used or not.");

        group.MapPost("/logout", async (
            LogoutRequest request,
            ClaimsPrincipal principal,
            TokenService tokens,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(userId))
                await tokens.RevokeAsync(request.RefreshToken, userId, cancellationToken);

            // The access token cannot be withdrawn — that is the price of a
            // self-contained token — so it stays valid until it expires. Its
            // lifetime is the security boundary here, not this call.
            return Results.NoContent();
        })
        .RequireAuthorization()
        .WithSummary("Revokes one refresh token. The access token remains valid until it expires.");

        group.MapGet("/me", (ClaimsPrincipal principal) =>
        {
            var name = principal.FindFirstValue(ClaimTypes.Name) ?? "";
            var roles = principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray();
            return TypedResults.Ok(new UserInfo(name, roles));
        })
        .RequireAuthorization()
        .WithSummary("The signed-in identity, read from the presented token.");

        group.MapPost("/register", async (
            RegisterRequest request,
            UserManager<ApiUser> users,
            IOptions<AuthOptions> options) =>
        {
            if (!options.Value.AllowRegistration)
                return Results.Problem(
                    detail: "Account creation is disabled on this deployment.",
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Not found");

            if (!WorkspaceRoles.IsKnown(request.Role))
                return Problems.Validation(nameof(RegisterRequest.Role), "Val_Required");

            if (string.IsNullOrWhiteSpace(request.UserName))
                return Problems.Validation(nameof(RegisterRequest.UserName), "Val_Required");

            var user = new ApiUser { UserName = request.UserName.Trim(), DisplayName = request.UserName.Trim() };
            var created = await users.CreateAsync(user, request.Password);
            if (!created.Succeeded)
                // Identity's own password and user-name rules decide this, and its
                // codes are stable enough to hand to a caller. The description text
                // is not echoed: it is English-only and occasionally quotes input.
                return Problems.Validation(
                    [.. created.Errors.Select(error =>
                        new ValidationIssue(nameof(RegisterRequest.UserName), $"Identity_{error.Code}"))]);

            await users.AddToRoleAsync(user, request.Role);
            return Results.Created($"/api/auth/users/{user.Id}", new UserInfo(user.UserName ?? "", [request.Role]));
        })
        .RequireAuthorization(PolicyNames.ManageMasterData)
        .RequireRateLimiting(RateLimitPolicy)
        .WithSummary("Creates an account. Needs the master-data policy and must be enabled in configuration.");
    }

    private static IResult Unauthorized() =>
        Results.Problem(
            detail: "The credentials or the refresh token were not accepted.",
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Unauthorized");
}
