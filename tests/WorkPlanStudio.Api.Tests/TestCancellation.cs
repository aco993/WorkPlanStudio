namespace WorkPlanStudio.Api.Tests;

/// <summary>
/// The running test's cancellation token, imported globally as <c>Ct</c>.
/// <para>
/// Every asynchronous call in these tests is given it, so a cancelled or
/// timed-out run stops in flight rather than waiting for an in-process HTTP
/// round trip to finish. The other suites in this repository spell the same
/// thing as a per-class <c>Ct</c> property; one global definition avoids
/// repeating it in every fixture.
/// </para>
/// </summary>
internal static class TestCancellation
{
    /// <summary>The token xUnit associates with the test currently executing.</summary>
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}
