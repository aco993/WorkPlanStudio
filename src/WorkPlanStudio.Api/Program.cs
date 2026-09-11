using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WorkPlanStudio.Api.Auth;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Api.Endpoints;
using WorkPlanStudio.Api.Health;
using WorkPlanStudio.Api.Http;
using WorkPlanStudio.Api.Services;
using WorkPlanStudio.Services.Auth;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration
//
// Bound and validated through the options pattern, never read eagerly here.
// Reading it at this point would freeze whatever the file happened to say
// before a test host, a container or a deployment wrapper had a chance to add
// its own sources — and the checks below would then be validating a
// configuration the application does not actually run with.
//
// The checks throw rather than warn. An API that starts with no signing key is
// not degraded, it is open to anyone who can mint a token, and the failure mode
// of a start-up warning is a service that looks healthy for a year.
// ---------------------------------------------------------------------------
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<JwtOptions>, ValidateJwtOptions>();

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AuthOptions>, ValidateAuthOptions>();

builder.Services.AddOptions<SeedOptions>().Bind(builder.Configuration.GetSection(SeedOptions.SectionName));

// Kestrel's own body limit. Every request this API accepts is a small JSON
// document; the largest is a work plan with its operations.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);

// ---------------------------------------------------------------------------
// Storage
// ---------------------------------------------------------------------------
builder.Services.AddDbContext<ApiDbContext>((services, options) =>
{
    var configured = services.GetRequiredService<IConfiguration>().GetConnectionString("Default");

    // An absent *or blank* connection string falls back to a file beside the
    // application. The blank case matters: SQLite reads an empty data source as
    // "give me a private temporary database", so a half-filled configuration
    // would start cleanly, migrate cleanly, and then lose every row between
    // requests.
    options.UseSqlite(string.IsNullOrWhiteSpace(configured)
        ? $"Data Source={Path.Combine(services.GetRequiredService<IHostEnvironment>().ContentRootPath, "workplan-api.db")}"
        : configured);
});

// ---------------------------------------------------------------------------
// Identity: the user store and the password hashing, both off the shelf.
// No cookie handler is added — this API is consumed by a cross-origin SPA and
// answers with bearer tokens only (see docs/adr/0020).
// ---------------------------------------------------------------------------
builder.Services
    .AddIdentityCore<ApiUser>(options =>
    {
        options.User.RequireUniqueEmail = false;
        options.Password.RequiredLength = 12;
        options.Password.RequireNonAlphanumeric = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApiDbContext>()
    .AddSignInManager();

builder.Services.AddOptions<IdentityOptions>()
    .Configure<IOptions<AuthOptions>>((identity, auth) =>
    {
        identity.Lockout.MaxFailedAccessAttempts = auth.Value.MaxFailedAttempts;
        identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(auth.Value.LockoutMinutes);
        identity.Lockout.AllowedForNewUsers = true;
    });

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<ApiScheduleRunner>();

// ---------------------------------------------------------------------------
// Authentication and authorization
//
// The policies come from the browser application's own Permissions table — the
// same file, compiled in — so "who may edit master data" has exactly one answer
// in this repository rather than one per host.
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>();
builder.Services.AddAuthorization(options => options.AddWorkspacePolicies());

// ---------------------------------------------------------------------------
// Cross-cutting HTTP concerns
// ---------------------------------------------------------------------------
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddResponseCompression(options =>
{
    // Left off for HTTPS on purpose: compressing a response that carries a
    // secret and reflects attacker-controlled input is the BREACH setup. This
    // API's responses do both.
    options.EnableForHttps = false;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<IConfiguration>((cors, configuration) =>
    cors.AddPolicy(ApiPolicies.ClientCors, policy => policy
        .WithOrigins(configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .WithHeaders("Authorization", "Content-Type")
        .WithMethods("GET", "POST", "PUT", "DELETE")
        // No AllowCredentials: the client sends a bearer token in a header, not
        // a cookie, so the browser has no credential to withhold and the
        // forbidden pairing of a wildcard origin with credentials cannot arise.
        // An empty origin list therefore means "same origin only", not "any".
        .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthEndpoints.RateLimitPolicy, context =>
    {
        var auth = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;

        return RateLimitPartition.GetFixedWindowLimiter(
            // Per client address. A user-name partition would let an attacker
            // spread guesses across names; an address partition makes the volume
            // itself expensive, which is the property that matters.
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = auth.AuthRequestsPerWindow,
                Window = TimeSpan.FromSeconds(auth.AuthWindowSeconds),
                QueueLimit = 0
            });
    });
});

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSecurityHeaders("/swagger");
app.UseResponseCompression();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "WorkPlan Studio API");
        options.DocumentTitle = "WorkPlan Studio API";
    });
}
else
{
    app.UseHsts();
}

app.UseCors(ApiPolicies.ClientCors);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") })
   .AllowAnonymous();

app.MapAuthEndpoints();
app.MapWorkCenterEndpoints();
app.MapWorkPlanEndpoints();
app.MapProductionOrderEndpoints();
app.MapPlantSettingsEndpoints();
app.MapScheduleEndpoints();

// ---------------------------------------------------------------------------
// Start-up work: check the configuration, migrate, then seed what is missing.
//
// Migrations rather than EnsureCreated, because a demo that cannot be upgraded
// without deleting its data is not a demonstration of anything.
// ---------------------------------------------------------------------------
await using (var scope = app.Services.CreateAsyncScope())
{
    var services = scope.ServiceProvider;
    var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("WorkPlanStudio.Api.Startup");

    // Resolving the options runs their validators. Done before the database is
    // touched, so a misconfigured deployment fails on its configuration rather
    // than halfway through a migration.
    _ = services.GetRequiredService<IOptions<JwtOptions>>().Value;
    _ = services.GetRequiredService<IOptions<AuthOptions>>().Value;
    var seed = services.GetRequiredService<IOptions<SeedOptions>>().Value;

    var db = services.GetRequiredService<ApiDbContext>();
    await db.Database.MigrateAsync();

    await DemoSeeder.EnsureRolesAsync(services.GetRequiredService<RoleManager<IdentityRole>>());
    await DemoSeeder.EnsureUsersAsync(services.GetRequiredService<UserManager<ApiUser>>(), seed.Users, log);

    if (seed.SampleData && await DemoSeeder.EnsureSampleDataAsync(db))
        log.LogInformation("Inserted the sample plant into an empty database");
}

await app.RunAsync();

/// <summary>Names used in more than one place in the composition root.</summary>
internal static class ApiPolicies
{
    /// <summary>The CORS policy the browser client is allowed through.</summary>
    public const string ClientCors = "client";
}

/// <summary>
/// Named so the integration tests can host this exact application through
/// <c>WebApplicationFactory</c>. Testing a copy of the composition root proves
/// nothing about the one that ships.
/// </summary>
public partial class Program;
