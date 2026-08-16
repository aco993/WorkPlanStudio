using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Components;
using WorkPlanStudio.Resources;

namespace WorkPlanStudio.Web.Tests;

public sealed class AccessibilityTests : BunitContext
{
    [Fact]
    public void Modal_has_dialog_semantics_localized_close_name_and_escape_behavior()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        var closed = false;

        var cut = Render<Modal>(parameters => parameters
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
}
