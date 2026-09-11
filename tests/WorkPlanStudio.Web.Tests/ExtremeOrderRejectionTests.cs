using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using WorkPlanStudio.Resources;
using WorkPlanStudio.Services.Chat;
using WorkPlanStudio.Validation;
using SchedulePage = WorkPlanStudio.Pages.Schedule;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// An order the master-data validators accept and the engine cannot.
/// <para>
/// The engine gained an absolute ten-year ceiling on a single operation, because
/// without one a long enough step wraps the machine clock negative and a negative
/// total tardiness scores better than every feasible schedule. The app's own limits
/// allow a lot of 1 000 000 pieces, so anything above about 5.26 minutes a piece
/// crosses that ceiling — and crossed it by throwing out of the context
/// constructor, which the page could only render as "the schedule could not be
/// generated". It belongs where every other unschedulable order already goes: a
/// named row in the rejection list, with the rest of the plan still produced.
/// </para>
/// </summary>
public sealed class ExtremeOrderRejectionTests : AppBunitContext
{
    private static readonly DateTime Horizon = new(2026, 6, 15, 6, 0, 0, DateTimeKind.Utc);

    private static WorkCenter Center(int id) =>
        new() { Id = id, Code = $"WC-{id}", Name = $"Center {id}", IsActive = true };

    private static Operation Op(int number, int workCenterId, decimal perPiece) =>
        new()
        {
            OperationNumber = number,
            WorkCenterId = workCenterId,
            WorkCenter = Center(workCenterId),
            Description = $"Op {number}",
            SetupTimeMinutes = 0m,
            TimePerPieceMinutes = perPiece
        };

    private static ProductionOrder Order(int id, int quantity, decimal perPiece) =>
        new()
        {
            Id = id,
            OrderNumber = $"PO-{id}",
            WorkPlanId = id,
            Quantity = quantity,
            Priority = 1,
            ReleaseLocal = Horizon,
            DueLocal = Horizon.AddHours(240),
            Status = ProductionOrderStatus.Released,
            RoutingSnapshotJson = RoutingSnapshot.Capture(new WorkPlan
            {
                Id = id,
                PlanNumber = $"WP-{id}",
                PartName = $"Part {id}",
                Status = WorkPlanStatus.Released,
                LotSize = quantity,
                Operations = [Op(10, 1, perPiece)]
            }).Serialize()
        };

    private static SchedulingParameters RuleOnly =>
        new() { DueDateRule = DueDateRule.Explicit, MultiStartRuns = 1, LocalSearchMaxSteps = 0 };

    /// <summary>The largest quantity the order validator accepts, at six minutes a piece.</summary>
    private static ProductionOrder Extreme() =>
        Order(99, ProductionOrderValidator.MaxQuantity, perPiece: 6m);

    [Fact]
    public void An_operation_past_the_engines_ceiling_is_rejected_instead_of_thrown()
    {
        // 1 000 000 pieces x 6 min = 360 000 000 s, against a ceiling of 315 360 000.
        Assert.True(
            ScheduleMapper.ToSeconds(0m, 6m, ProductionOrderValidator.MaxQuantity)
            > SchedulingParameterLimits.MaxStepDurationSeconds);

        var preparation = ScheduleMapper.BuildInputFromOrders([Extreme()], [Center(1)], RuleOnly);

        Assert.Null(preparation.Input);
        var issue = Assert.Single(preparation.Errors);
        Assert.Equal("PO-99", issue.OrderReference);
        Assert.Equal(10, issue.OperationNumber);

        // Not OperationExceedsShiftWindow: this work center has no calendar at all,
        // so its longest placement is unbounded and "longer than the longest shift"
        // would be a false explanation of a true rejection.
        Assert.Equal(SchedulePreparationErrorCode.InvalidOperationDuration, issue.Code);
    }

    [Fact]
    public void The_orders_that_can_be_scheduled_still_are()
    {
        var preparation = ScheduleMapper.BuildInputFromOrders(
            [Extreme(), Order(1, quantity: 100, perPiece: 2m)], [Center(1)], RuleOnly);

        Assert.NotNull(preparation.Input);
        Assert.Equal("PO-1", Assert.Single(preparation.Input!.Context.Jobs).Reference);
        Assert.Equal(99, Assert.Single(preparation.Errors).OrderId);
    }

    [Fact]
    public void An_order_just_inside_the_ceiling_is_scheduled()
    {
        // 5 min a piece is 300 000 000 s - under the ceiling, and a boundary worth
        // pinning because the rejection is a > and not a >=.
        var preparation = ScheduleMapper.BuildInputFromOrders(
            [Order(1, ProductionOrderValidator.MaxQuantity, perPiece: 5m)], [Center(1)], RuleOnly);

        Assert.NotNull(preparation.Input);
        Assert.Empty(preparation.Errors);
    }

    [Fact]
    public void The_page_renders_it_as_a_rejection_rather_than_a_failed_run()
    {
        var files = new TempDatabaseFiles();
        try
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton<IProductionScheduleService>(new FakeScheduleService
            {
                Result = Sample.OnTime() with
                {
                    PreparationErrors =
                    [
                        new SchedulePreparationIssue(
                            99, "PO-99", 10, SchedulePreparationErrorCode.InvalidOperationDuration, "WC-1")
                    ]
                }
            });
            Services.AddSingleton<IStringLocalizer<SharedResource>>(new PassThroughLocalizer<SharedResource>());
            Services.AddSingleton<RuleBasedNarrator>();
            Services.AddSingleton<IAssistantConfig>(new FakeAssistantConfig());
            Services.AddSingleton(new HttpClient());
            Services.AddSingleton<ScheduleAssistant>();
            Services.AddSingleton<OfflineScheduleAnswerer>();
            Services.AddSingleton<ScheduleChat>();
            Services.AddScheduleExport();
            Services.AddSingleton(new PlantSettingsService(files.CreateDatabase("extreme-order.db", new FakeStorage())));

            var cut = Render<SchedulePage>();

            cut.WaitForAssertion(() => Assert.Contains("Sched_RejectedTitle", cut.Markup, StringComparison.Ordinal));
            var banner = cut.Find(".form-banner.error");
            Assert.Equal("alert", banner.GetAttribute("role"));
            Assert.Contains("PO-99", banner.TextContent, StringComparison.Ordinal);
            Assert.Contains("Sched_Error_InvalidOperationDuration", banner.TextContent, StringComparison.Ordinal);
            Assert.Equal("production-orders", cut.Find(".form-banner a").GetAttribute("href"));

            // And it is a rejection beside a schedule, not instead of one.
            Assert.DoesNotContain("Error_ScheduleFailed", cut.Markup, StringComparison.Ordinal);
            Assert.Equal(2, cut.FindAll(".jobs-table tbody tr").Count);
        }
        finally
        {
            files.Dispose();
        }
    }
}
