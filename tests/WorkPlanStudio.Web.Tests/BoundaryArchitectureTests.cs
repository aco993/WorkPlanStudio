using System.Reflection;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The three boundaries docs/ARCHITECTURE.md draws that the engine and
/// working-time tests do not reach: the wire contracts are records over the base
/// class library, the shared domain knows authorization and the two pure
/// libraries and nothing else, and the browser app compiles against the
/// contracts but never against the backend. Each was true before these tests;
/// now each one breaks the build on the day it stops being true.
/// </summary>
public class BoundaryArchitectureTests
{
    private static string[] ReferencesOf(Type anchor) =>
        [.. anchor.Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "")];

    private static void AssertOnly(string[] references, string[] allowed, string what)
    {
        foreach (var name in references)
            Assert.True(
                allowed.Any(p => name.StartsWith(p, StringComparison.Ordinal)),
                $"Unexpected dependency in {what}: {name}");
    }

    [Fact]
    public void The_contracts_depend_only_on_the_base_class_library() =>
        AssertOnly(ReferencesOf(typeof(LoginRequest)), ["System", "netstandard", "mscorlib"], "the wire contracts");

    [Fact]
    public void The_domain_knows_authorization_and_the_pure_libraries_and_nothing_else() =>
        AssertOnly(
            ReferencesOf(typeof(Permissions)),
            ["System", "netstandard", "mscorlib",
             "Microsoft.AspNetCore.Authorization",
             "WorkPlanStudio.Scheduling", "WorkPlanStudio.WorkingTime"],
            "the shared domain");

    [Theory]
    [InlineData("Microsoft.AspNetCore.Components")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.JSInterop")]
    [InlineData("SQLitePCLRaw")]
    public void The_domain_has_no_ui_and_no_persistence(string forbiddenPrefix) =>
        Assert.DoesNotContain(ReferencesOf(typeof(Permissions)), n => n.StartsWith(forbiddenPrefix, StringComparison.Ordinal));

    [Theory]
    [InlineData("WorkPlanStudio.Api")]                         // the backend itself
    [InlineData("Microsoft.AspNetCore.Identity")]              // its user store
    [InlineData("Microsoft.AspNetCore.Authentication.JwtBearer")] // its token validation
    public void The_browser_app_never_compiles_against_the_backend(string forbidden) =>
        Assert.DoesNotContain(ReferencesOf(typeof(App)), n => n.StartsWith(forbidden, StringComparison.Ordinal));

    [Fact]
    public void The_browser_app_talks_to_the_backend_through_the_contracts() =>
        Assert.Contains("WorkPlanStudio.Contracts", ReferencesOf(typeof(App)));
}
