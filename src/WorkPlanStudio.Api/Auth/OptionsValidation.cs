using Microsoft.Extensions.Options;

namespace WorkPlanStudio.Api.Auth;

/// <summary>
/// Turns the configuration checks into options validation, so they run through
/// the standard <c>ValidateOnStart</c> path and a bad configuration stops the
/// host rather than producing an API that quietly accepts forged tokens.
/// </summary>
/// <remarks>
/// Reading configuration before <c>builder.Build()</c> would have been shorter,
/// and wrong: nothing configured by a test host or a deployment wrapper exists
/// at that point, so the checks would have been running against a different
/// configuration than the application ends up with.
/// </remarks>
public sealed class ValidateJwtOptions : IValidateOptions<JwtOptions>
{
    private readonly IHostEnvironment _environment;

    /// <summary>Creates the validator.</summary>
    /// <param name="environment">Decides whether the published sample key is tolerated.</param>
    public ValidateJwtOptions(IHostEnvironment environment) => _environment = environment;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            options.Validate(_environment.IsDevelopment());
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}

/// <summary>The same treatment for the lockout and rate-limit numbers.</summary>
public sealed class ValidateAuthOptions : IValidateOptions<AuthOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException exception)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
