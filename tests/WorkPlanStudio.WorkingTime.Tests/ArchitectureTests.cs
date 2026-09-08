using System.Reflection;

namespace WorkPlanStudio.WorkingTime.Tests;

/// <summary>
/// The working-time model is pure: it may know the scheduling engine (so it can
/// hand over a calendar) and the base class library, nothing else. No clock, no
/// time zones, no UI, no persistence — which is what keeps every test above
/// deterministic.
/// </summary>
public class ArchitectureTests
{
    private static readonly AssemblyName[] References =
        typeof(WorkingTimelineBuilder).Assembly.GetReferencedAssemblies();

    [Fact]
    public void The_model_depends_only_on_the_engine_and_the_base_class_library()
    {
        string[] allowed = ["System", "netstandard", "mscorlib", "WorkPlanStudio.Scheduling"];
        foreach (var reference in References)
        {
            var name = reference.Name ?? "";
            Assert.True(
                allowed.Any(p => name.StartsWith(p, StringComparison.Ordinal)),
                $"Unexpected dependency in the working-time model: {name}");
        }
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.JSInterop")]
    [InlineData("Microsoft.Extensions")]
    public void The_model_does_not_reference(string forbiddenPrefix) =>
        Assert.DoesNotContain(References, a => (a.Name ?? "").StartsWith(forbiddenPrefix, StringComparison.Ordinal));
}
