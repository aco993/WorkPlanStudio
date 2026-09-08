using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Components;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Auth;

namespace WorkPlanStudio.Web.Tests;

/// <summary>The dashboard's glance cards and the colour-theme toggle.</summary>
public sealed class HomeAndThemeTests : BunitContext
{
    [Fact]
    public async Task The_dashboard_shows_the_plant_and_a_freshly_computed_schedule()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        using var files = new TempDatabaseFiles();
        var database = files.CreateDatabase("home.db", new FakeStorage());
        Assert.True((await database.EnsureReadyAsync()).IsReady);

        var fake = new FakeScheduleService { Result = Sample.WithLateJob() };
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        Services.AddDemoAuthorization(WorkspaceRole.Supervisor);
        Services.AddSingleton(new WorkPlanService(database));
        Services.AddSingleton(new WorkCenterService(database));
        Services.AddSingleton(new PlantSettingsService(database));
        Services.AddSingleton<IProductionScheduleService>(fake);

        var cut = Render<WorkPlanStudio.Pages.Home>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".glance-kpi")));
        Assert.Contains("Nordrhein-Westfalen", cut.Markup);
        Assert.Contains("Holiday_CorpusChristi", cut.Markup);          // next holiday after the demo horizon
        Assert.Contains("Role_Supervisor", cut.Markup);
        Assert.Contains("7 / 7", System.Text.RegularExpressions.Regex.Replace(cut.Find(".glance-list").TextContent, @"\s+", " "));   // every seed center runs shifts
        Assert.Single(cut.FindAll(".glance-kpi.late"));                 // the fake schedule has one late job
        Assert.Equal(DueDateRule.Explicit, fake.LastParameters!.DueDateRule);
    }

    [Fact]
    public void The_theme_toggle_cycles_system_light_dark_and_persists_through_js()
    {
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
        JSInterop.Setup<string>("workplanTheme.get").SetResult("system");
        JSInterop.Setup<string>("workplanTheme.set", _ => true).SetResult("light");

        var cut = Render<ThemeToggle>();
        cut.WaitForAssertion(() => Assert.Equal("system", cut.Find("button").GetAttribute("data-theme-mode")));

        cut.Find("button").Click();
        Assert.Equal("light", cut.Find("button").GetAttribute("data-theme-mode"));
        cut.Find("button").Click();
        Assert.Equal("dark", cut.Find("button").GetAttribute("data-theme-mode"));
        cut.Find("button").Click();
        Assert.Equal("system", cut.Find("button").GetAttribute("data-theme-mode"));

        var sets = JSInterop.Invocations.Where(i => i.Identifier == "workplanTheme.set").Select(i => i.Arguments[0]).ToList();
        Assert.Equal(["light", "dark", "system"], sets);
        Assert.Contains("Theme_Toggle", cut.Find("button").GetAttribute("aria-label"));
    }
}
