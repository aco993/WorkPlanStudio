using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace WorkPlanStudio.Services.Auth;

/// <summary>
/// The service layer's question: may the current persona perform this policy?
/// Hiding a button is a courtesy to the user; this is what actually stops the
/// write. Every mutating service asks before it touches the database.
/// </summary>
public interface IPermissionGuard
{
    ValueTask<bool> CanAsync(string policy, CancellationToken cancellationToken = default);
}

/// <summary>Evaluates policies through the standard <see cref="IAuthorizationService"/> against the current identity.</summary>
public sealed class PermissionGuard : IPermissionGuard
{
    private readonly IAuthorizationService _authorization;
    private readonly AuthenticationStateProvider _authentication;

    public PermissionGuard(IAuthorizationService authorization, AuthenticationStateProvider authentication)
    {
        _authorization = authorization;
        _authentication = authentication;
    }

    public async ValueTask<bool> CanAsync(string policy, CancellationToken cancellationToken = default)
    {
        var state = await _authentication.GetAuthenticationStateAsync();
        var result = await _authorization.AuthorizeAsync(state.User, resource: null, policy);
        return result.Succeeded;
    }
}

/// <summary>A guard that always says yes — for tests that exercise a service, not its authorization.</summary>
public sealed class AllowAllGuard : IPermissionGuard
{
    public static AllowAllGuard Instance { get; } = new();

    public ValueTask<bool> CanAsync(string policy, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
}

/// <summary>A guard for a fixed persona — the same table the pipeline uses, without DI.</summary>
public sealed class RoleGuard : IPermissionGuard
{
    private readonly WorkspaceRole _role;

    public RoleGuard(WorkspaceRole role) => _role = role;

    public ValueTask<bool> CanAsync(string policy, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Permissions.Grants(_role, policy));
}
