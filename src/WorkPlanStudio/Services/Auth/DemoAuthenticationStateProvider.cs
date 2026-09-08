using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace WorkPlanStudio.Services.Auth;

/// <summary>Where the chosen persona is remembered. Abstracted so tests need no browser.</summary>
public interface IPersonaStore
{
    ValueTask<WorkspaceRole?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(WorkspaceRole role, CancellationToken cancellationToken = default);
}

/// <summary>Persona in the browser's <c>localStorage</c>, next to the other app settings.</summary>
public sealed class JsPersonaStore : IPersonaStore
{
    private const string Key = "persona";
    private readonly IJSRuntime _js;

    public JsPersonaStore(IJSRuntime js) => _js = js;

    public async ValueTask<WorkspaceRole?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _js.InvokeAsync<string?>("workplanSettings.get", cancellationToken, Key);
        return Enum.TryParse<WorkspaceRole>(stored, out var role) && Enum.IsDefined(role) ? role : null;
    }

    public ValueTask SaveAsync(WorkspaceRole role, CancellationToken cancellationToken = default) =>
        _js.InvokeVoidAsync("workplanSettings.set", cancellationToken, Key, role.ToString());
}

/// <summary>
/// The identity behind the standard Blazor authorization pipeline — a persona
/// picked in the UI rather than a token from a server. Everything downstream
/// (<c>AuthorizeView</c>, policies, the service guard) is the real thing; only
/// this one class would change if an identity provider were plugged in.
/// </summary>
public sealed class DemoAuthenticationStateProvider : AuthenticationStateProvider
{
    /// <summary>The authentication type stamped on the identity, so nothing mistakes it for a real login.</summary>
    public const string AuthenticationType = "demo-persona";

    /// <summary>Who a first-time visitor is: the persona with every permission, so the demo is fully usable.</summary>
    public const WorkspaceRole DefaultRole = WorkspaceRole.Planner;

    private readonly IPersonaStore _store;
    private WorkspaceRole? _current;

    public DemoAuthenticationStateProvider(IPersonaStore store) => _store = store;

    /// <summary>The persona in effect (the default until the store has been read).</summary>
    public WorkspaceRole CurrentRole => _current ?? DefaultRole;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        _current ??= await _store.LoadAsync() ?? DefaultRole;
        return new AuthenticationState(PrincipalFor(_current.Value));
    }

    /// <summary>Switches persona, remembers it, and tells every <c>AuthorizeView</c> to re-render.</summary>
    public async Task SwitchAsync(WorkspaceRole role, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));

        _current = role;
        await _store.SaveAsync(role, cancellationToken);
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(PrincipalFor(role))));
    }

    /// <summary>The principal for a persona: a name, and the role claim the policies test.</summary>
    public static ClaimsPrincipal PrincipalFor(WorkspaceRole role) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, $"demo-{role.ToString().ToLowerInvariant()}"),
                new Claim(ClaimTypes.Role, role.ToString())
            ],
            AuthenticationType));
}
