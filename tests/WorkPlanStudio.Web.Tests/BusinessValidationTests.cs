using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Web.Tests;

public class BusinessValidationTests
{
    private static WorkCenter Center(bool active = true) => new()
    {
        Id = 1,
        Code = "WC-1",
        Name = "Center",
        IsActive = active,
        ParallelCapacity = 1
    };

    private static WorkPlan ValidPlan() => new()
    {
        PlanNumber = " WP-1 ",
        PartName = " Part ",
        LotSize = 10,
        Status = WorkPlanStatus.Released,
        Operations =
        [
            new Operation
            {
                OperationNumber = 10,
                Description = " Cut ",
                WorkCenterId = 1,
                SetupTimeMinutes = 1,
                TimePerPieceMinutes = 2
            }
        ]
    };

    [Fact]
    public void Work_plan_validator_accepts_a_complete_released_plan()
    {
        var issues = WorkPlanValidator.Validate(ValidPlan(), new Dictionary<int, WorkCenter> { [1] = Center() });

        Assert.Empty(issues);
    }

    [Fact]
    public void Work_plan_validator_reports_ranges_duplicates_and_inactive_center()
    {
        var plan = ValidPlan();
        plan.LotSize = 0;
        plan.Operations.Add(new Operation
        {
            OperationNumber = 10,
            Description = "Second",
            WorkCenterId = 1,
            SetupTimeMinutes = -1,
            TimePerPieceMinutes = WorkPlanValidator.MaxOperationMinutes + 1
        });

        var issues = WorkPlanValidator.Validate(
            plan,
            new Dictionary<int, WorkCenter> { [1] = Center(active: false) });

        Assert.Contains(issues, issue => issue.MessageKey == "Val_LotSizeRange");
        Assert.Contains(issues, issue => issue.MessageKey == "Val_OperationNumberDuplicate");
        Assert.Contains(issues, issue => issue.MessageKey == "Val_SetupTimeRange");
        Assert.Contains(issues, issue => issue.MessageKey == "Val_RunTimeRange");
        Assert.Contains(issues, issue => issue.MessageKey == "Val_WorkCenterInactive");
    }

    [Fact]
    public void Work_plan_validator_blocks_direct_archived_to_released_transition()
    {
        var issues = WorkPlanValidator.Validate(
            ValidPlan(),
            new Dictionary<int, WorkCenter> { [1] = Center() },
            WorkPlanStatus.Archived);

        Assert.Contains(issues, issue => issue.MessageKey == "Val_StatusTransition");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void Work_center_validator_rejects_unsupported_capacity(int capacity)
    {
        var center = Center();
        center.ParallelCapacity = capacity;

        var issues = WorkCenterValidator.Validate(center);

        Assert.Contains(issues, issue => issue.MessageKey == "Val_CapacityRange");
    }

    [Fact]
    public void ToSeconds_rejects_negative_and_overflowing_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleMapper.ToSeconds(-1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleMapper.ToSeconds(0, 0, 0));
        Assert.Throws<OverflowException>(() => ScheduleMapper.ToSeconds(decimal.MaxValue, 0, 1));
    }

    [Fact]
    public void Mapper_ignores_an_unreferenced_invalid_machine_but_rejects_a_referenced_one()
    {
        var valid = Center();
        var invalid = new WorkCenter
        {
            Id = 2,
            Code = "BAD",
            Name = "Bad capacity",
            IsActive = true,
            ParallelCapacity = 0
        };

        var validOnly = ScheduleMapper.BuildInput(
            [ValidPlan()],
            [valid, invalid],
            new SchedulingParameters { MultiStartRuns = 1, LocalSearchMaxSteps = 0 });
        Assert.NotNull(validOnly.Input);

        var plan = ValidPlan();
        plan.Operations[0].WorkCenterId = 2;
        var rejected = ScheduleMapper.BuildInput(
            [plan],
            [valid, invalid],
            new SchedulingParameters { MultiStartRuns = 1, LocalSearchMaxSteps = 0 });
        Assert.Null(rejected.Input);
        Assert.Contains(rejected.Errors, error => error.Code == SchedulePreparationErrorCode.InvalidWorkCenterCapacity);
    }
}
