using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Components;
using WorkPlanStudio.Resources;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// Where focus lands when a dialog closes. The existing dialog test exercises the
/// Escape key, which was the one path that already worked: ✕, Escape and the
/// backdrop all went through the component's own close method, while every page
/// closes its dialogs by assigning the bound field — which Cancel does, and so does
/// a successful Save. Those left the focus trap attached to a detached node and the
/// keyboard user on <c>&lt;body&gt;</c>, at the top of the document.
/// </summary>
public sealed class FocusManagementTests : AppBunitContext
{
    private IRenderedComponent<Modal> RenderModal(Action<bool> onVisibleChanged, bool visible = true)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());

        return Render<Modal>(parameters => parameters
            .Add(component => component.Visible, visible)
            .Add(component => component.Title, "Edit work center")
            .Add(component => component.VisibleChanged,
                EventCallback.Factory.Create<bool>(this, onVisibleChanged)));
    }

    private int CloseCalls =>
        JSInterop.Invocations.Count(invocation => invocation.Identifier == "workplanModal.close");

    [Fact]
    public void Opening_the_dialog_records_where_focus_came_from()
    {
        var cut = RenderModal(_ => { });

        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "workplanModal.open");
        Assert.Equal(0, CloseCalls);
    }

    /// <summary>
    /// The case the old implementation missed: the page — not the dialog — decides to
    /// close, which is what Cancel and a successful Save both do.
    /// </summary>
    [Fact]
    public void Focus_is_returned_when_the_page_closes_the_dialog_rather_than_the_dialog_itself()
    {
        var cut = RenderModal(_ => { });
        Assert.Equal(0, CloseCalls);

        cut.Render(parameters => parameters.Add(component => component.Visible, false));

        Assert.Equal(1, CloseCalls);
        Assert.Empty(cut.FindAll("[role=dialog]"));
    }

    [Fact]
    public void Focus_is_returned_exactly_once_when_the_close_button_is_used()
    {
        var visible = true;
        var cut = RenderModal(value => visible = value);

        cut.Find(".modal-head button").Click();
        cut.Render(parameters => parameters.Add(component => component.Visible, visible));

        Assert.False(visible);
        Assert.Equal(1, CloseCalls);
    }

    [Fact]
    public async Task Focus_is_returned_when_the_dialog_is_unmounted_while_still_open()
    {
        var cut = RenderModal(_ => { });
        Assert.Equal(0, CloseCalls);

        await cut.Instance.DisposeAsync();

        Assert.Equal(1, CloseCalls);
    }

    /// <summary>
    /// Reopening has to record the new return target. A stack rather than a single
    /// slot is why nested or successive dialogs cannot strand each other.
    /// </summary>
    [Fact]
    public void Reopening_the_dialog_arms_the_return_again()
    {
        var cut = RenderModal(_ => { });
        cut.Render(parameters => parameters.Add(component => component.Visible, false));
        Assert.Equal(1, CloseCalls);

        cut.Render(parameters => parameters.Add(component => component.Visible, true));
        cut.Render(parameters => parameters.Add(component => component.Visible, false));

        Assert.Equal(2, CloseCalls);
        Assert.Equal(2, JSInterop.Invocations.Count(i => i.Identifier == "workplanModal.open"));
    }

    [Fact]
    public async Task A_dialog_that_was_never_open_does_not_try_to_return_focus()
    {
        var cut = RenderModal(_ => { }, visible: false);

        await cut.Instance.DisposeAsync();

        Assert.Equal(0, CloseCalls);
    }
}
