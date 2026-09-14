using System.Globalization;
using WorkPlanStudio.Services.Import;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The stated rules for numbers and dates. These are the tests that keep an
/// hourly rate from being multiplied by a thousand and a due date from moving six
/// months, so each one pins the rule rather than an example of it.
/// </summary>
public sealed class ImportValueTests
{
    [Theory]
    [InlineData("74,50", 74.50)]
    [InlineData("74.50", 74.50)]
    [InlineData("1,5", 1.5)]
    [InlineData("0", 0)]
    [InlineData("-3,25", -3.25)]
    [InlineData("+3.25", 3.25)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("1,234,567", 1234567)]
    [InlineData("1 234,56", 1234.56)]
    public void A_decimal_reads_the_same_whichever_convention_wrote_it(string text, double expected)
    {
        Assert.True(ImportValues.TryParseDecimal(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    /// <summary>
    /// The documented tie-break. <c>1,234</c> is one and a bit, not one thousand:
    /// resolving it the other way would silently multiply a rate by a thousand,
    /// and the wrong answer there costs more than the wrong answer here.
    /// </summary>
    [Fact]
    public void A_single_separator_on_a_decimal_is_always_the_decimal_point()
    {
        Assert.True(ImportValues.TryParseDecimal("1,234", out var comma));
        Assert.True(ImportValues.TryParseDecimal("1.234", out var dot));

        Assert.Equal(1.234m, comma);
        Assert.Equal(1.234m, dot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12abc")]
    [InlineData("1.2.3,4,5")]
    public void Anything_that_is_not_a_number_is_refused_rather_than_guessed(string text) =>
        Assert.False(ImportValues.TryParseDecimal(text, out _));

    /// <summary>
    /// A quantity has no fractional part, so the same text reads differently: here
    /// a separator can only be grouping, and <c>1.23</c> is refused rather than
    /// rounded to one piece or a hundred and twenty-three.
    /// </summary>
    [Theory]
    [InlineData("1200", 1200)]
    [InlineData("1.200", 1200)]
    [InlineData("1,200", 1200)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("-25", -25)]
    [InlineData("1 200", 1200)]
    public void A_whole_number_treats_a_separator_as_grouping(string text, int expected)
    {
        Assert.True(ImportValues.TryParseInt(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("1.23")]
    [InlineData("1,5")]
    [InlineData("1.2345")]
    [InlineData("12.34.567")]
    [InlineData("")]
    [InlineData("x")]
    public void A_whole_number_refuses_anything_with_a_fraction_in_it(string text) =>
        Assert.False(ImportValues.TryParseInt(text, out _));

    [Theory]
    [InlineData("31.12.2026", 2026, 12, 31)]
    [InlineData("1.6.2026", 2026, 6, 1)]
    [InlineData("2026-12-31", 2026, 12, 31)]
    [InlineData("2026-06-01", 2026, 6, 1)]
    public void Both_accepted_date_shapes_are_read(string text, int year, int month, int day)
    {
        Assert.True(ImportValues.TryParseDate(text, out var value));
        Assert.Equal(new DateTime(year, month, day), value);
    }

    [Theory]
    [InlineData("31.12.2026 14:30")]
    [InlineData("2026-12-31 14:30")]
    [InlineData("2026-12-31T14:30")]
    public void A_time_of_day_may_follow_the_date(string text)
    {
        Assert.True(ImportValues.TryParseDate(text, out var value));
        Assert.Equal(new TimeSpan(14, 30, 0), value.TimeOfDay);
    }

    /// <summary>
    /// The whole point of the rule. <c>01/02/2026</c> is two different dates in
    /// two conventions and there is nothing in the file that says which, so it is
    /// refused by name instead of being read as one of them.
    /// </summary>
    [Theory]
    [InlineData("01/02/2026")]
    [InlineData("12/31/2026")]
    [InlineData("2026/12/31")]
    [InlineData("31 Dec 2026")]
    [InlineData("20261231")]
    public void An_ambiguous_or_unknown_date_shape_is_refused(string text) =>
        Assert.False(ImportValues.TryParseDate(text, out _));

    /// <summary>
    /// The failure this rule exists to prevent: the same file, two visitors, two
    /// different answers. The parse must not move when the thread culture does.
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public void The_visitor_culture_changes_nothing(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            Assert.True(ImportValues.TryParseDecimal("74,50", out var rate));
            Assert.True(ImportValues.TryParseDate("31.12.2026", out var date));

            Assert.Equal(74.50m, rate);
            Assert.Equal(new DateTime(2026, 12, 31), date);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>A parsed date is wall clock with no zone attached — the one time model.</summary>
    [Fact]
    public void A_parsed_date_carries_no_time_zone()
    {
        Assert.True(ImportValues.TryParseDate("2026-06-01", out var value));

        Assert.Equal(DateTimeKind.Unspecified, value.Kind);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("Ja", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("x", true)]
    [InlineData("no", false)]
    [InlineData("Nein", false)]
    [InlineData("0", false)]
    [InlineData("inaktiv", false)]
    public void A_flag_reads_in_either_language(string text, bool expected)
    {
        Assert.True(ImportValues.TryParseBool(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData("2")]
    public void A_flag_that_is_neither_is_refused(string text) =>
        Assert.False(ImportValues.TryParseBool(text, out _));

    /// <summary>Spreadsheets pad numbers with non-breaking spaces; they are not part of the value.</summary>
    [Fact]
    public void Non_breaking_spaces_are_treated_as_padding()
    {
        Assert.True(ImportValues.TryParseDecimal("\u00A01\u202F234,56\u00A0", out var value));

        Assert.Equal(1234.56m, value);
    }
}
