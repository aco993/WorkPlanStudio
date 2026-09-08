namespace WorkPlanStudio.WorkingTime.Tests;

public class ShiftPatternAndRulesTests
{
    [Fact]
    public void Presets_are_valid_and_findable_by_key()
    {
        foreach (var preset in ShiftPatterns.Presets)
        {
            preset.Validate();
            Assert.Same(preset, ShiftPatterns.ByKey(preset.Key));
        }

        Assert.Null(ShiftPatterns.ByKey("no-such-pattern"));
        Assert.True(ShiftPatterns.Continuous.IsContinuous);
        Assert.False(ShiftPatterns.OneShift.IsContinuous);
    }

    [Fact]
    public void A_night_shift_that_crosses_midnight_lasts_eight_hours()
    {
        var night = ShiftPatterns.ThreeShift.Shifts.Single(s => s.Key == "night");

        Assert.Equal(TimeSpan.FromHours(8), night.Duration);
        Assert.Equal("night", night.Crew);
    }

    [Fact]
    public void Shift_validation_rejects_unusable_definitions()
    {
        Assert.Throws<ArgumentException>(() => new ShiftDefinition("", new(6, 0), new(14, 0), WorkDays.Weekdays).Validate());
        Assert.Throws<ArgumentException>(() => new ShiftDefinition("x", new(6, 0), new(14, 0), WorkDays.None).Validate());
        Assert.Throws<ArgumentException>(() => new ShiftDefinition("x", new(6, 0), new(6, 0), WorkDays.Monday).Validate());
        Assert.Throws<ArgumentException>(() => new ShiftPattern("dup",
        [
            new ShiftDefinition("a", new(6, 0), new(14, 0), WorkDays.Monday),
            new ShiftDefinition("a", new(14, 0), new(22, 0), WorkDays.Monday)
        ]).Validate());
    }

    [Fact]
    public void Work_days_flags_map_to_days_of_week()
    {
        Assert.True(WorkDays.Weekdays.Includes(DayOfWeek.Friday));
        Assert.False(WorkDays.Weekdays.Includes(DayOfWeek.Saturday));
        Assert.True(WorkDays.All.Includes(DayOfWeek.Sunday));
        Assert.Equal(WorkDays.Sunday, DayOfWeek.Sunday.ToFlag());
    }

    [Fact]
    public void Statutory_defaults_are_the_numbers_in_the_act()
    {
        var rules = WorkingTimeRules.Statutory;

        Assert.Equal(TimeSpan.FromHours(8), rules.MaxDailyWorkingTime);
        Assert.Equal(TimeSpan.FromHours(10), rules.ExtendedDailyWorkingTime);
        Assert.Equal(TimeSpan.FromMinutes(30), rules.BreakAfterSixHours);
        Assert.Equal(TimeSpan.FromMinutes(45), rules.BreakAfterNineHours);
        Assert.Equal(TimeSpan.FromMinutes(15), rules.MinimumBreakPiece);
        Assert.Equal(TimeSpan.FromHours(6), rules.MaxWorkWithoutBreak);
        Assert.Equal(TimeSpan.FromHours(11), rules.MinimumRest);
        Assert.Equal(new TimeOnly(23, 0), rules.NightStart);
        Assert.Equal(new TimeOnly(6, 0), rules.NightEnd);
        Assert.Equal(15, rules.MinimumFreeSundaysPerYear);
        Assert.False(rules.SundayWorkAllowed);
        Assert.False(rules.HolidayWorkAllowed);
        rules.Validate();
    }

    [Theory]
    [InlineData(6, 0)]
    [InlineData(6.5, 30)]
    [InlineData(9, 30)]
    [InlineData(9.5, 45)]
    public void Break_owed_follows_section_4(double workingHours, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), WorkingTimeRules.Statutory.BreakOwed(TimeSpan.FromHours(workingHours)));

    [Fact]
    public void Every_rule_has_a_legal_reference()
    {
        foreach (var id in Enum.GetValues<WorkingTimeRuleId>())
            Assert.Contains(WorkingTimeRules.Catalog, r => r.Id == id && r.LegalReference.StartsWith("§", StringComparison.Ordinal));
    }

    [Fact]
    public void Rule_validation_rejects_inconsistent_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (WorkingTimeRules.Statutory with { ExtendedDailyWorkingTime = TimeSpan.FromHours(7) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (WorkingTimeRules.Statutory with { SundayBoundaryShift = TimeSpan.FromHours(7) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (WorkingTimeRules.Statutory with { MinimumRest = TimeSpan.Zero }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (WorkingTimeRules.Statutory with { BreakAfterNineHours = TimeSpan.FromMinutes(10) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (WorkingTimeRules.Statutory with { MinimumFreeSundaysPerYear = 60 }).Validate());
    }

    [Fact]
    public void Absences_must_be_ordered()
    {
        Assert.Throws<ArgumentException>(() =>
            new AbsencePeriod(new DateTime(2026, 6, 2), new DateTime(2026, 6, 1), AbsenceKind.Vacation).Validate());
    }
}
