using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using WorkPlanStudio.Api.Data;
using WorkPlanStudio.Api.Endpoints;
using WorkPlanStudio.Api.Scheduling;
using WorkPlanStudio.Api.Security;
using WorkPlanStudio.Contracts;
using WorkPlanStudio.Persistence;

var builder = WebApplication.CreateBuilder(args);
var provider = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connectionString = builder.Configuration.GetConnectionString("Production")
    ?? throw new InvalidOperationException("ConnectionStrings:Production is required.");
if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) &&
    !connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Production PostgreSQL mode requires a PostgreSQL ConnectionStrings:Production value.");

var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("WorkPlanStudio");
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    Directory.CreateDirectory(dataProtectionKeysPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

builder.Services.AddDbContext<ProductionDbContext>(options =>
{
    if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.EnableRetryOnFailure(5);
            npgsql.MigrationsAssembly("WorkPlanStudio.PostgresMigrations");
        });
    else
        options.UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly("WorkPlanStudio.Persistence"));
});

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength = 12;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<ProductionDbContext>()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "__Host-WorkPlanStudio";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("operator", policy => policy.RequireAuthenticatedUser())
    .AddPolicy("administrator", policy => policy.RequireRole("Administrator"));
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("api", context => RateLimitPartition.GetTokenBucketLimiter(
        context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 120,
            TokensPerPeriod = 60,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            AutoReplenishment = true,
            QueueLimit = 0
        }));
    options.AddPolicy("ai", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.Identity?.Name ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddHttpClient("assistant", client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ProductionDbContext>("database", tags: ["ready"]);
builder.Services.AddSingleton<ScheduleRunQueue>();
builder.Services.AddScoped<ScheduleRunLeaseManager>();
builder.Services.AddHostedService<ScheduleWorker>();

var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("WorkPlanStudio.Api"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
    });

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new ApiError("antiforgery_validation_failed", "The request verification token is missing or invalid."));
    }
});
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapWorkPlanIdentity(app.Configuration);
app.MapWorkCenterEndpoints();
app.MapWorkPlanEndpoints();
app.MapProductionOrderEndpoints();
app.MapCapacityEndpoints();
app.MapScheduleRunEndpoints();
app.MapAssistantEndpoints();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapGet("/api/health/ready", async (ProductionDbContext db, CancellationToken cancellationToken) =>
{
    var canConnect = await db.Database.CanConnectAsync(cancellationToken);
    var migrationsApplied = canConnect && !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any();
    var readiness = new ReadinessStatus(
        canConnect && migrationsApplied ? "ready" : "not-ready",
        db.Database.ProviderName ?? "unknown",
        migrationsApplied);
    return readiness.Status == "ready"
        ? Results.Ok(readiness)
        : Results.Json(readiness, statusCode: StatusCodes.Status503ServiceUnavailable);
})
    .RequireRateLimiting("api");
app.MapFallbackToFile("index.html");

await DatabaseStartup.ApplyAsync(app.Services, app.Configuration, app.Logger, app.Lifetime.ApplicationStopping);
await app.RunAsync();

public partial class Program;
