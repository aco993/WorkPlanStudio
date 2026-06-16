using System.Reflection;

namespace WorkPlanStudio.Scheduling.Tests;

/// <summary>
/// Guards the central design decision: the scheduling engine is a pure library.
/// If anyone ever pulls Blazor, EF Core or JS interop into it, these tests fail —
/// which is what keeps the engine testable on a plain runner and reusable outside
/// the browser app.
/// </summary>
public class ArchitectureTests
{
    private static readonly AssemblyName[] References =
        typeof(SchedulingEngine).Assembly.GetReferencedAssemblies();

    [Theory]
    [InlineData("Microsoft.AspNetCore")]   // Blazor / web
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.JSInterop")]
    [InlineData("Microsoft.Extensions")]   // DI, localization, …
    [InlineData("SQLitePCLRaw")]
    public void Engine_does_not_reference(string forbiddenPrefix)
    {
        Assert.DoesNotContain(References, a =>
            (a.Name ?? "").StartsWith(forbiddenPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Engine_only_depends_on_the_base_class_library()
    {
        string[] allowed = ["System", "netstandard", "mscorlib"];
        foreach (var reference in References)
        {
            var name = reference.Name ?? "";
            Assert.True(
                allowed.Any(p => name.StartsWith(p, StringComparison.Ordinal)),
                $"Unexpected dependency in the scheduling engine: {name}");
        }
    }
}
