using Bunit;
using Microsoft.Extensions.DependencyInjection;
using WorkPlanStudio.Services;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The base every component test uses. It exists for one setting: bUnit's
/// default <see cref="BunitContext.DefaultWaitTimeout"/> is one second, which is
/// a measurement of the machine rather than of the component. A shared CI runner
/// compiling three other projects can miss it while the component is perfectly
/// correct.
/// <para>
/// Raising the ceiling costs nothing on the passing path — a satisfied assertion
/// returns as soon as it is satisfied — and only changes how long a genuinely
/// failing test takes to admit it. It is not, and was not, a fix for a flaky
/// test: the one intermittent failure this suite had was a stale element, not a
/// slow one, and is fixed at its cause in
/// <see cref="GanttAccessibilityTests"/>.
/// </para>
/// </summary>
public abstract class AppBunitContext : BunitContext
{
    protected AppBunitContext()
    {
        DefaultWaitTimeout = TimeSpan.FromSeconds(15);

        // Registered here rather than in each test's Arrange, because every page
        // that changes something announces it: leaving it out would make a test
        // fail on wiring instead of on behaviour.
        var announcer = new UiAnnouncer();
        announcer.Announced += Announcements.Add;
        Services.AddSingleton(announcer);
    }

    /// <summary>The sentences the page announced, in order.</summary>
    protected List<string> Announcements { get; } = [];
}
