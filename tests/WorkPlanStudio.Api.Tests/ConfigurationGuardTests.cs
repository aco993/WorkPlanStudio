using Microsoft.Extensions.Options;
using WorkPlanStudio.Api.Auth;

namespace WorkPlanStudio.Api.Tests;

/// <summary>
/// The API has to refuse to start rather than start insecurely.
/// <para>
/// Each case below would otherwise produce a service that looks entirely
/// healthy: it answers, it logs, it serves data — and it accepts access tokens
/// that anybody with a copy of this repository could have signed.
/// </para>
/// </summary>
public class ConfigurationGuardTests
{
    [Theory]
    [InlineData("", "not configured")]
    [InlineData("too-short", "too short")]
    [InlineData(JwtOptions.DevelopmentSigningKey, "development sample key")]
    public void An_unusable_signing_key_stops_the_host(string signingKey, string expectedFragment)
    {
        using var api = new ApiFactory("guard", new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Jwt:SigningKey"] = signingKey
        });

        var failure = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Contains(expectedFragment, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_development_sample_key_is_accepted_in_development_only()
    {
        var options = new JwtOptions { SigningKey = JwtOptions.DevelopmentSigningKey };

        options.Validate(isDevelopment: true);

        Assert.Throws<InvalidOperationException>(() => options.Validate(isDevelopment: false));
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(5, 0)]
    [InlineData(5, 100000)]
    public void A_nonsensical_lockout_configuration_stops_the_host(int attempts, int minutes)
    {
        using var api = new ApiFactory("guard-auth", new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Auth:MaxFailedAttempts"] = attempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Auth:LockoutMinutes"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

        Assert.Throws<OptionsValidationException>(() => api.CreateClient());
    }

    [Fact]
    public void A_seeded_account_with_an_unknown_role_stops_the_host()
    {
        using var api = new ApiFactory("guard-seed", new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Seed:Users:0:Role"] = "Administrator"
        });

        // A role nobody registered would create an account that silently has no
        // permissions at all, which is worse than not starting.
        var failure = Assert.Throws<InvalidOperationException>(() => api.CreateClient());

        Assert.Contains("unknown role", failure.Message, StringComparison.Ordinal);
    }
}
