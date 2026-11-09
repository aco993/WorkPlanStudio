using System.Reflection;

namespace WorkPlanStudio.Export.Tests;

/// <summary>
/// The README says the writers carry "no package reference and no native
/// dependency". Until this test, the only thing holding that up was an empty
/// <c>.csproj</c> — true, and one <c>PackageReference</c> away from being false
/// without anything going red. The same claim, made by a test, defends itself.
/// </summary>
public class ArchitectureTests
{
    private static readonly AssemblyName[] References =
        typeof(ExportTable).Assembly.GetReferencedAssemblies();

    [Fact]
    public void The_writers_depend_only_on_the_base_class_library()
    {
        string[] allowed = ["System", "netstandard", "mscorlib"];
        foreach (var reference in References)
        {
            var name = reference.Name ?? "";
            Assert.True(
                allowed.Any(p => name.StartsWith(p, StringComparison.Ordinal)),
                $"Unexpected dependency in the export writers: {name}");
        }
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.JSInterop")]
    [InlineData("Microsoft.Extensions")]
    [InlineData("WorkPlanStudio")]   // not even the engine: the writers take tables, not schedules
    public void The_writers_do_not_reference(string forbiddenPrefix) =>
        Assert.DoesNotContain(References, a => (a.Name ?? "").StartsWith(forbiddenPrefix, StringComparison.Ordinal));
}
