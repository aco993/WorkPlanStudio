using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkPlanStudio.Components;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// On a cold start the persona is read from storage before anyone is
/// authorized, and AuthorizeView renders nothing for that moment by default.
/// The working-time form therefore arrived a frame after the rest of the page
/// and pushed every card below it down: the app's only layout shift (0.05,
/// Lighthouse), traced in a browser to this component. The controls are now
/// there from the first frame, disabled until the persona is known.
/// </summary>
public sealed class EditableAreaWhileAuthorizingTests : AppBunitContext
{
    /// <summary>A persona store that answers only when the test says so.</summary>
    private sealed class SlowPersonaStore : IPersonaStore
    {
        public TaskCompletionSource<WorkspaceRole?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<WorkspaceRole?> LoadAsync(CancellationToken cancellationToken = default) => new(Answer.Task);

        public ValueTask SaveAsync(WorkspaceRole role, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private SlowPersonaStore Arrange()
    {
        var store = new SlowPersonaStore();
        var provider = new DemoAuthenticationStateProvider(store);
        Services.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
        Services.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();
        Services.RemoveAll<AuthenticationStateProvider>();
        Services.AddAuthorizationCore(options => options.AddWorkspacePolicies());
        Services.AddCascadingAuthenticationState();
        Services.AddSingleton<AuthenticationStateProvider>(provider);
        return store;
    }

    private IRenderedComponent<EditableArea> Rendered() =>
        Render<EditableArea>(parameters => parameters
            .Add(p => p.Policy, Permissions.ManagePlantRules)
            .Add(p => p.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenElement(0, "input");
                builder.AddAttribute(1, "class", "probe");
                builder.CloseElement();
            })));

    [Fact]
    public void While_the_persona_is_read_the_controls_are_already_there_and_disabled()
    {
        Arrange();
        var cut = Rendered();

        var fieldset = cut.Find("fieldset.form-fieldset");
        Assert.True(fieldset.HasAttribute("disabled"));
        Assert.NotNull(cut.Find("fieldset .probe"));
    }

    [Fact]
    public void Once_the_persona_is_known_the_same_controls_become_editable()
    {
        var store = Arrange();
        var cut = Rendered();

        store.Answer.SetResult(WorkspaceRole.Planner);

        cut.WaitForAssertion(() => Assert.False(cut.Find("fieldset.form-fieldset").HasAttribute("disabled")));
        Assert.Single(cut.FindAll(".probe"));
    }
}
