using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace WorkPlanStudio.Api.Auth;

/// <summary>
/// Configures the bearer handler from <see cref="JwtOptions"/> once the
/// container exists, rather than from a value captured while the application
/// was still being assembled.
/// </summary>
public sealed class ConfigureJwtBearer : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly JwtOptions _jwt;

    /// <summary>Creates the configurator.</summary>
    /// <param name="jwt">The validated token settings.</param>
    public ConfigureJwtBearer(IOptions<JwtOptions> jwt)
    {
        ArgumentNullException.ThrowIfNull(jwt);
        _jwt = jwt.Value;
    }

    /// <inheritdoc />
    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);

    /// <inheritdoc />
    public void Configure(string? name, JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (name is not null && !string.Equals(name, JwtBearerDefaults.AuthenticationScheme, StringComparison.Ordinal))
            return;

        options.MapInboundClaims = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = _jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(_jwt.KeyBytes()),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(_jwt.ClockSkewSeconds),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role,

            // One algorithm, named. Leaving the set open is how a signed token
            // becomes an unsigned one.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
        };
    }
}
