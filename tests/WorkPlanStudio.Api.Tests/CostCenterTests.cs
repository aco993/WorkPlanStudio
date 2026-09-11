using System.Net;
using System.Net.Http.Json;
using WorkPlanStudio.Contracts;

namespace WorkPlanStudio.Api.Tests;

/// <summary>Its own database: these tests write.</summary>
public sealed class CostCenterFixture() : ApiFactory("costcenters");

/// <summary>
/// Cost centres as a resource, and the relationship that replaced a free-text
/// column.
/// <para>
/// The interesting cases are the ones a string could not have: a work centre
/// naming a cost centre that does not exist, a cost centre that cannot be
/// deleted because machines still book against it, and two spellings of the same
/// code that must be one row. Those are the reasons the column became an entity,
/// so they are what this suite is about.
/// </para>
/// </summary>
public class CostCenterTests : IClassFixture<CostCenterFixture>
{
    private readonly CostCenterFixture _api;

    public CostCenterTests(CostCenterFixture api) => _api = api;

    [Fact]
    public async Task The_seeded_plant_carries_the_cost_centres_the_browser_app_seeds()
    {
        var client = await _api.SignedInAsync("guest");

        var costCenters = await client.GetFromJsonAsync<List<CostCenterDto>>("/api/cost-centers", Ct);

        Assert.NotNull(costCenters);
        var machining = Assert.Single(costCenters, c => c.Code == "CC-2000");

        // Same code, same name as the in-browser seed. A reviewer who pulls this
        // plant into the browser must not find a second, differently-worded
        // catalogue of the same accounting units.
        Assert.Equal("Machining", machining.Name);

        var centers = await client.GetFromJsonAsync<List<WorkCenterDto>>("/api/work-centers", Ct);
        Assert.NotNull(centers);

        // The case a free-text column could never answer: two machines on one
        // cost centre, visibly the same row rather than two equal strings.
        Assert.Equal(2, centers.Count(c => c.CostCenterId == machining.Id));
        Assert.All(
            centers.Where(c => c.CostCenterId == machining.Id),
            c => Assert.Equal("Machining", c.CostCenterName));
    }

    [Fact]
    public async Task A_cost_centre_can_be_created_read_updated_and_deleted()
    {
        var client = await _api.SignedInAsync("planner");

        var created = await Created<CostCenterDto>(
            await client.PostAsJsonAsync("/api/cost-centers", TestData.NewCostCenter("CC-7100"), Ct));
        Assert.NotEqual(0, created.Id);
        Assert.NotEmpty(created.ConcurrencyStamp);

        var read = await client.GetFromJsonAsync<CostCenterDto>($"/api/cost-centers/{created.Id}", Ct);
        Assert.Equal(created, read);

        var updated = await client.PutAsJsonAsync(
            $"/api/cost-centers/{created.Id}", created with { Name = "Heat treatment" }, Ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var after = await updated.Content.ReadFromJsonAsync<CostCenterDto>(Ct);
        Assert.NotNull(after);
        Assert.Equal("Heat treatment", after.Name);
        Assert.NotEqual(created.ConcurrencyStamp, after.ConcurrencyStamp);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/cost-centers/{created.Id}", Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/cost-centers/{created.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_lower_case_code_is_stored_as_the_canonical_spelling()
    {
        var client = await _api.SignedInAsync("planner");

        var created = await Created<CostCenterDto>(
            await client.PostAsJsonAsync("/api/cost-centers", TestData.NewCostCenter("cc-7110"), Ct));

        Assert.Equal("CC-7110", created.Code);
    }

    [Fact]
    public async Task The_code_uniqueness_rule_ignores_case()
    {
        var client = await _api.SignedInAsync("planner");
        await client.PostAsJsonAsync("/api/cost-centers", TestData.NewCostCenter("CC-7120"), Ct);

        var again = await client.PostAsJsonAsync("/api/cost-centers", TestData.NewCostCenter("cc-7120"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("Val_CostCenterCodeTaken", await again.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cost_centre_without_a_name_is_refused_with_the_failing_field()
    {
        var client = await _api.SignedInAsync("planner");

        var response = await client.PostAsJsonAsync(
            "/api/cost-centers", TestData.NewCostCenter("CC-7130") with { Name = "   " }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Val_Required", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_update_without_the_version_it_read_is_refused_and_a_stale_one_loses()
    {
        var client = await _api.SignedInAsync("planner");
        var created = await Created<CostCenterDto>(
            await client.PostAsJsonAsync("/api/cost-centers", TestData.NewCostCenter("CC-7140"), Ct));

        var noVersion = await client.PutAsJsonAsync(
            $"/api/cost-centers/{created.Id}", created with { ConcurrencyStamp = "" }, Ct);

        var first = await client.PutAsJsonAsync($"/api/cost-centers/{created.Id}", created with { Name = "First" }, Ct);
        var second = await client.PutAsJsonAsync($"/api/cost-centers/{created.Id}", created with { Name = "Second" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Reading_needs_an_account_and_writing_needs_the_master_data_policy()
    {
        var anonymous = _api.CreateClient();
        var guest = await _api.SignedInAsync("guest");
        var supervisor = await _api.SignedInAsync("supervisor");

        var unauthenticated = await anonymous.GetAsync("/api/cost-centers", Ct);
        var guestRead = await guest.GetAsync("/api/cost-centers", Ct);
        var guestWrite = await guest.PostAsJsonAsync("/api/cost-centers", TestData.NewCostCenter("CC-7150"), Ct);

        // A supervisor runs the shop floor and books against cost centres; owning
        // them is the planner's job, exactly as for the other master data.
        var supervisorWrite = await supervisor.PostAsJsonAsync(
            "/api/cost-centers", TestData.NewCostCenter("CC-7151"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("application/problem+json", unauthenticated.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, guestRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, guestWrite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, supervisorWrite.StatusCode);
    }

    [Fact]
    public async Task A_cost_centre_work_centres_still_book_against_cannot_be_deleted()
    {
        var client = await _api.SignedInAsync("planner");
        var costCenter = await Created<CostCenterDto>(
            await client.PostAsJsonAsync("/api/cost-centers", TestData.NewCostCenter("CC-7160"), Ct));
        var center = await Created<WorkCenterDto>(
            await client.PostAsJsonAsync("/api/work-centers", TestData.NewCenter("CC-WC-1", costCenter.Id), Ct));

        var refused = await client.DeleteAsync($"/api/cost-centers/{costCenter.Id}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Val_CostCenterInUse", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Released from its last machine, it can go.
        await client.PutAsJsonAsync($"/api/work-centers/{center.Id}", center with { CostCenterId = null }, Ct);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/cost-centers/{costCenter.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_work_centre_naming_a_cost_centre_that_does_not_exist_is_refused()
    {
        var client = await _api.SignedInAsync("planner");

        var response = await client.PostAsJsonAsync(
            "/api/work-centers", TestData.NewCenter("CC-WC-2", costCenterId: 987654), Ct);

        // Not created on the way past, and not silently dropped to "unassigned":
        // the caller selected something that is not there and is told so.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Val_CostCenterMissing", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_display_fields_on_a_write_are_ignored_and_answered_from_the_stored_row()
    {
        var client = await _api.SignedInAsync("planner");
        var costCenter = await Created<CostCenterDto>(
            await client.PostAsJsonAsync("/api/cost-centers", TestData.NewCostCenter("CC-7170"), Ct));

        var created = await Created<WorkCenterDto>(await client.PostAsJsonAsync(
            "/api/work-centers",
            TestData.NewCenter("CC-WC-3", costCenter.Id) with
            {
                CostCenterCode = "CC-9999",
                CostCenterName = "Invented by the caller"
            },
            Ct));

        // The id is the relationship; the two labels describe the row the server
        // has. Trusting them would let a request add a cost centre through a
        // field the contract calls read-only.
        Assert.Equal(costCenter.Id, created.CostCenterId);
        Assert.Equal(costCenter.Code, created.CostCenterCode);
        Assert.Equal(costCenter.Name, created.CostCenterName);

        var all = await client.GetFromJsonAsync<List<CostCenterDto>>("/api/cost-centers", Ct);
        Assert.DoesNotContain(all!, c => c.Code == "CC-9999");
    }

    private static async Task<T> Created<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(Ct)
               ?? throw new InvalidOperationException("The create response was empty.");
    }
}
