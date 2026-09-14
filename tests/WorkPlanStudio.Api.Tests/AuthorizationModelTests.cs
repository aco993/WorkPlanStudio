using WorkPlanStudio.Contracts;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Api.Tests;

/// <summary>
/// Guards the seam where a string had to be written down twice: the wire's role
/// and policy names against the application's own.
/// <para>
/// The duplication is deliberate — <c>WorkPlanStudio.Contracts</c> is the wire
/// format and takes no dependency on anything — and what is not acceptable is a
/// duplicate that drifts silently, so these fail the build the moment the two
/// copies stop agreeing. The plant-settings bounds used to be guarded here too,
/// by reading the application's source text; they are not duplicated any more,
/// because the endpoint calls <c>PlantSettingsValidator</c> itself.
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
}
