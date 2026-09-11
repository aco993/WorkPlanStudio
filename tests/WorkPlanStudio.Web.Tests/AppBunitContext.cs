using Bunit;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The base every component test uses, for one reason: bUnit's default
/// <see cref="BunitContext.DefaultWaitTimeout"/> is one second, and one second is
/// a measurement of the machine, not of the component.
/// <para>
/// Two of these tests failed roughly once in four runs on a developer machine
/// that was busy compiling — never in isolation, and never with a wrong value,
/// only with "the assertion did not become true in time". A shared CI runner is
/// busier than that. Raising the ceiling costs nothing on the passing path,
/// because a satisfied assertion returns as soon as it is satisfied; it only
/// changes how long a genuinely failing test takes to admit it.
/// </para>
/// </summary>
public abstract class AppBunitContext : BunitContext
{
    protected AppBunitContext() => DefaultWaitTimeout = TimeSpan.FromSeconds(15);
}
