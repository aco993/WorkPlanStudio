using System.Globalization;
using System.Text.RegularExpressions;
using WorkPlanStudio.Api.Validation;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Api.Tests;

/// <summary>
/// Guards the seams where a string or a bound had to be written down twice.
/// <para>
/// Every duplicate here is deliberate and explained where it lives; what is not
/// acceptable is a duplicate that drifts silently. These tests fail the build
/// the moment the two copies stop agreeing, which is the only thing that makes
/// the duplication safe to have.
/// </para>
/// </summary>
public class AuthorizationModelTests
{
    [Fact]
    public void The_wire_role_names_are_the_application_persona_names()
    {
        var personas = Enum.GetNames<WorkspaceRole>().Order().ToArray();

        Assert.Equal(personas, WorkspaceRoles.All.Order().ToArray());
    }

    [Fact]
    public void The_wire_policy_names_are_the_application_policy_names()
    {
        // The browser hides a button with AuthorizeView Policy="ManageOrders";
        // this API refuses the write under the same string. One authorization
        // model, two enforcement points.
        Assert.Equal(Permissions.All.Order().ToArray(), PolicyNames.All.Order().ToArray());
    }

    [Theory]
    [InlineData(PolicyNames.ManageMasterData, WorkspaceRole.Planner, true)]
    [InlineData(PolicyNames.ManageMasterData, WorkspaceRole.Supervisor, false)]
    [InlineData(PolicyNames.ManageMasterData, WorkspaceRole.Guest, false)]
    [InlineData(PolicyNames.ManageOrders, WorkspaceRole.Supervisor, true)]
    [InlineData(PolicyNames.ManageOrders, WorkspaceRole.Guest, false)]
    [InlineData(PolicyNames.ManagePlantRules, WorkspaceRole.Supervisor, false)]
    [InlineData(PolicyNames.ManageAbsences, WorkspaceRole.Supervisor, true)]
    public void The_policy_table_the_api_registers_is_the_application_table(
        string policy, WorkspaceRole role, bool expected) =>
        Assert.Equal(expected, Permissions.Grants(role, policy));

    [Fact]
    public void The_duplicated_plant_settings_bounds_match_the_application_source()
    {
        // PlantSettingsValidator could not be linked into this assembly: it shares
        // a file with the service that writes to browser storage. So it is copied,
        // and the copy is checked against the original's source text here.
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "WorkPlanStudio", "Services", "PlantSettingsService.cs"));

        var sundayShift = RangeIn(source, "SundayBoundaryShiftHours");
        var rest = RangeIn(source, "MinimumRestHours");

        Assert.Equal(
            (PlantSettingsRules.MinSundayBoundaryShiftHours, PlantSettingsRules.MaxSundayBoundaryShiftHours),
            sundayShift);
        Assert.Equal((PlantSettingsRules.MinRestHours, PlantSettingsRules.MaxRestHours), rest);
    }

    /// <summary>Reads a <c>settings.X is &lt; a or &gt; b</c> pattern out of the application's validator.</summary>
    private static (int Minimum, int Maximum) RangeIn(string source, string property)
    {
        var match = Regex.Match(
            source,
            $@"settings\.{Regex.Escape(property)} is < (?<min>-?\d+) or > (?<max>-?\d+)",
            RegexOptions.None,
            TimeSpan.FromSeconds(2));

        Assert.True(match.Success, $"could not find the range check for {property}; has the validator been rewritten?");
        return (
            int.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["max"].Value, CultureInfo.InvariantCulture));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
