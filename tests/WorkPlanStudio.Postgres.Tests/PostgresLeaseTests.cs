using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Scheduling;
using WorkPlanStudio.Models;
using WorkPlanStudio.Persistence;

namespace WorkPlanStudio.Postgres.Tests;

public sealed class PostgresLeaseTests
{
    public static bool PostgresAvailable => !string.IsNullOrWhiteSpace(ConnectionString);

    [Fact(Skip = "WPS_POSTGRES_CONNECTION must point to a disposable PostgreSQL database.", SkipUnless = nameof(PostgresAvailable))]
    public async Task Migrations_match_the_model_on_real_postgresql()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("Npgsql", db.Database.ProviderName, StringComparison.Ordinal);
    }

    [Fact(Skip = "WPS_POSTGRES_CONNECTION must point to a disposable PostgreSQL database.", SkipUnless = nameof(PostgresAvailable))]
    public async Task Claim_takeover_and_stale_completion_are_atomic_on_real_postgresql()
    {
        var id = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            setup.ScheduleRuns.Add(new ScheduleRun
            {
                Id = id,
                OwnerId = "postgres-integration-owner",
                Status = ScheduleRunStatus.Queued,
                ParametersJson = "{}",
                CreatedUtc = DateTime.UtcNow
            });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var now = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var first = new ScheduleRunLeaseManager(firstDb);
        var second = new ScheduleRunLeaseManager(secondDb);
        var claims = await Task.WhenAll(
            first.TryClaimAsync(id, "worker-a", now, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken),
            second.TryClaimAsync(id, "worker-b", now, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
        Assert.Equal(1, claims.Count(claimed => claimed));

        var initialOwner = claims[0] ? "worker-a" : "worker-b";
        var recoveryOwner = claims[0] ? "worker-b" : "worker-a";
        await using var recoveryDb = CreateContext();
        var recovery = new ScheduleRunLeaseManager(recoveryDb);
        Assert.True(await recovery.TryClaimAsync(
            id, recoveryOwner, now.AddMinutes(3), TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));

        await using var staleDb = CreateContext();
        var stale = new ScheduleRunLeaseManager(staleDb);
        Assert.Equal(0, await stale.TryCompleteAsync(
            id, initialOwner, "{\"stale\":true}", now.AddMinutes(3), TestContext.Current.CancellationToken));
        Assert.Equal(1, await recovery.TryCompleteAsync(
            id, recoveryOwner, "{\"recovered\":true}", now.AddMinutes(4), TestContext.Current.CancellationToken));

        var run = await recoveryDb.ScheduleRuns.AsNoTracking().SingleAsync(
            item => item.Id == id, TestContext.Current.CancellationToken);
        Assert.Equal(ScheduleRunStatus.Completed, run.Status);
        Assert.Equal(2, run.AttemptCount);
        Assert.Null(run.LeaseOwner);
        Assert.Equal("{\"recovered\":true}", run.ResultJson);
    }

    private static ProductionDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ProductionDbContext>()
            .UseNpgsql(ConnectionString, npgsql =>
                npgsql.MigrationsAssembly("WorkPlanStudio.PostgresMigrations"))
            .Options;
        return new ProductionDbContext(options);
    }

    private static string? ConnectionString => Environment.GetEnvironmentVariable("WPS_POSTGRES_CONNECTION");
}
