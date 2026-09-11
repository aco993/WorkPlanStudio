using System.Globalization;

namespace WorkPlanStudio.Web.Tests;

/// <summary>
/// The formatter is the one place a number or a date becomes text, so these tests
/// pin the two things a reader of this app can actually be misled by: a date whose
/// meaning depends on which language you read it in, and a money amount that drops
/// its cents or shows the wrong currency in one of the two languages.
/// </summary>
public sealed class FormatTests
{
    /// <summary>
    /// The formatter joins a value to its unit with a no-break space, so that a line
    /// never breaks between "7,5" and "h". Spelling that out in every expectation
    /// would make these tests unreadable.
    /// </summary>
    private static string Nb(string text) => text.Replace(" ", Format.NoBreak, StringComparison.Ordinal);

    private static T InCulture<T>(string culture, Func<T> render)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            return render();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>
    /// 6/4/2026 is 6 April in en-US and, read as 4.6.2026, 4 June in de-DE. Both
    /// languages of this app render the same dates, so neither may use a form that
    /// only one of them reads correctly.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void A_date_never_renders_as_a_bare_run_of_numbers(string culture)
    {
        var text = InCulture(culture, () => Format.Date(new DateTime(2026, 6, 4)));

        Assert.Contains("2026", text, StringComparison.Ordinal);
        Assert.Contains("4", text, StringComparison.Ordinal);
        // A month name, in whatever abbreviation the culture uses — never "6".
        Assert.Contains(
            CultureInfo.GetCultureInfo(culture).DateTimeFormat.AbbreviatedMonthNames[5][..3],
            text,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("6/4", text, StringComparison.Ordinal);
        Assert.DoesNotContain("4.6.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_languages_render_the_same_day_differently_but_both_unambiguously()
    {
        var english = InCulture("en-US", () => Format.Date(new DateTime(2026, 6, 4)));
        var german = InCulture("de-DE", () => Format.Date(new DateTime(2026, 6, 4)));

        Assert.Equal("4 Jun 2026", english);
        Assert.Equal("4. Juni 2026", german);
    }

    /// <summary>
    /// The previous implementation was <c>value.ToString("N0") + " €"</c>: it threw
    /// away the cents of every cost figure and applied the German symbol position to
    /// English as well.
    /// </summary>
    [Fact]
    public void A_money_amount_keeps_its_cents_and_takes_the_symbol_position_from_the_culture()
    {
        var english = InCulture("en-US", () => Format.Euro(1234.56m));
        var german = InCulture("de-DE", () => Format.Euro(1234.56m));

        Assert.Equal("€1,234.56", english);
        Assert.Equal("1.234,56 €", german);
    }

    [Fact]
    public void A_percentage_takes_its_spacing_from_the_culture_rather_than_from_the_call_site()
    {
        Assert.Equal("80%", InCulture("en-US", () => Format.Percent(0.8)));

        var german = InCulture("de-DE", () => Format.Percent(0.8));
        Assert.StartsWith("80", german, StringComparison.Ordinal);
        Assert.EndsWith("%", german, StringComparison.Ordinal);
        Assert.True(char.IsWhiteSpace(german[2]), "German separates the number from the sign; English does not");
    }

    [Fact]
    public void A_duration_drops_the_minutes_only_when_there_are_none()
    {
        Assert.Equal(Nb("45 min"), InCulture("de-DE", () => Format.Duration(TimeSpan.FromMinutes(45))));
        Assert.Equal(Nb("2 h"), InCulture("de-DE", () => Format.Duration(TimeSpan.FromHours(2))));
        Assert.Equal(Nb("2 h") + " " + Nb("30 min"), InCulture("de-DE", () => Format.Duration(TimeSpan.FromMinutes(150))));
    }

    [Fact]
    public void Numbers_and_hours_follow_the_culture_s_decimal_separator()
    {
        Assert.Equal("1,234", InCulture("en-US", () => Format.Number(1234m)));
        Assert.Equal("1.234", InCulture("de-DE", () => Format.Number(1234m)));
        Assert.Equal(Nb("7.5 h"), InCulture("en-US", () => Format.Hours(450m)));
        Assert.Equal(Nb("7,5 h"), InCulture("de-DE", () => Format.Hours(450m)));
    }

    /// <summary>
    /// A production plan is read against machine logs and shift rosters. Those are
    /// 24-hour in both languages, so the time of day is too.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void A_time_of_day_is_on_the_twenty_four_hour_clock_in_both_languages(string culture)
    {
        var text = InCulture(culture, () => Format.Time(new DateTime(2026, 6, 4, 18, 30, 0)));

        Assert.Equal("18:30", text);
    }

    /// <summary>
    /// The euro symbol is forced onto the culture's own number format. CultureInfo
    /// instances are shared and cached, so doing that by mutation rather than on a
    /// clone would turn every other amount in the process into euro as a side effect.
    /// </summary>
    [Fact]
    public void Forcing_the_euro_symbol_does_not_mutate_the_shared_culture()
    {
        InCulture("en-US", () => Format.Euro(10m));

        Assert.Equal("$", CultureInfo.GetCultureInfo("en-US").NumberFormat.CurrencySymbol);
    }
}
