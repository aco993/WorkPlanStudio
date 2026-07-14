using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Components;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services;

namespace WorkPlanStudio.Web.Tests;

public sealed class AccessibilityTests : Bunit.TestContext
{
    [Fact]
    public void Modal_has_dialog_semantics_localized_close_name_and_escape_behavior()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        var closed = false;

        var cut = RenderComponent<Modal>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.Title, "Accessible title")
            .Add(component => component.VisibleChanged,
                EventCallback.Factory.Create<bool>(this, visible => closed = !visible)));

        var dialog = cut.Find("[role=dialog]");
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        var titleId = dialog.GetAttribute("aria-labelledby");
        Assert.Equal("Accessible title", cut.Find($"#{titleId}").TextContent);
        Assert.Equal("Common_Close", cut.Find(".modal-head button").GetAttribute("aria-label"));

        cut.Find(".modal-backdrop-custom").KeyDown("Escape");

        Assert.True(closed);
        Assert.Empty(cut.FindAll("[role=dialog]"));
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "workplanModal.open");
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "workplanModal.close");
    }

    [Fact]
    public void Password_reset_link_renders_a_named_single_main_form_with_new_password_autocomplete()
    {
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddSingleton(new ServerSession(new HttpClient { BaseAddress = new Uri("http://localhost/") }));
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/?resetEmail=user%40example.com&resetToken=one-time-token");

        var cut = RenderComponent<LoginPanel>();

        Assert.Single(cut.FindAll("main"));
        Assert.Equal("Auth_ResetConfirmTitle", cut.Find("h1").TextContent);
        Assert.Equal("user@example.com", cut.Find("input[type=email]").GetAttribute("value"));
        Assert.Equal("new-password", cut.Find("input[type=password]").GetAttribute("autocomplete"));
        Assert.Contains("Auth_ResetPassword", cut.Find("button.btn-primary").TextContent, StringComparison.Ordinal);
        Assert.Contains("Auth_BackToLogin", cut.Find("button.btn-ghost").TextContent, StringComparison.Ordinal);
    }
}
