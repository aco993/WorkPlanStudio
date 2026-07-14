using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Api.Scheduling;
using WorkPlanStudio.Models;
using WorkPlanStudio.Persistence;

namespace WorkPlanStudio.Api.Tests;

public sealed class ScheduleRunLeaseTests
{
    [Fact]
    public async Task Only_one_worker_claims_and_an_expired_lease_can_be_recovered()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<ProductionDbContext>().UseSqlite(connection).Options;
        var id = Guid.NewGuid();
        await using (var setup = new ProductionDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            setup.ScheduleRuns.Add(new ScheduleRun
            {
                Id = id,
                OwnerId = "owner",
                Status = ScheduleRunStatus.Queued,
                ParametersJson = "{}",
                CreatedUtc = DateTime.UtcNow
            });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var now = new DateTime(2026, 7, 14, 20, 0, 0, DateTimeKind.Utc);
        await using (var firstDb = new ProductionDbContext(options))
        await using (var secondDb = new ProductionDbContext(options))
        {
            var first = new ScheduleRunLeaseManager(firstDb);
            var second = new ScheduleRunLeaseManager(secondDb);
            Assert.True(await first.TryClaimAsync(id, "worker-a", now, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
            Assert.False(await second.TryClaimAsync(id, "worker-b", now, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
            Assert.Equal(1, await first.RenewAsync(id, "worker-a", now.AddMinutes(3), TestContext.Current.CancellationToken));
        }

        await using (var recoveryDb = new ProductionDbContext(options))
        {
            var recovery = new ScheduleRunLeaseManager(recoveryDb);
            Assert.True(await recovery.TryClaimAsync(
                id, "worker-b", now.AddMinutes(4), TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
            var run = await recoveryDb.ScheduleRuns.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal("worker-b", run.LeaseOwner);
            Assert.Equal(2, run.AttemptCount);
        }
    }

    [Fact]
    public async Task Cancellation_request_prevents_a_claim()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<ProductionDbContext>().UseSqlite(connection).Options;
        await using var db = new ProductionDbContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var run = new ScheduleRun
        {
            Id = Guid.NewGuid(),
            OwnerId = "owner",
            Status = ScheduleRunStatus.Queued,
            ParametersJson = "{}",
            CreatedUtc = DateTime.UtcNow,
            CancellationRequestedUtc = DateTime.UtcNow
        };
        db.ScheduleRuns.Add(run);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var leases = new ScheduleRunLeaseManager(db);
        Assert.False(await leases.TryClaimAsync(
            run.Id, "worker", DateTime.UtcNow, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_request_prevents_completion_after_claim()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<ProductionDbContext>().UseSqlite(connection).Options;
        await using var db = new ProductionDbContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var run = new ScheduleRun
        {
            Id = Guid.NewGuid(),
            OwnerId = "owner",
            Status = ScheduleRunStatus.Queued,
            ParametersJson = "{}",
            CreatedUtc = DateTime.UtcNow
        };
        db.ScheduleRuns.Add(run);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var leases = new ScheduleRunLeaseManager(db);
        var now = DateTime.UtcNow;
        Assert.True(await leases.TryClaimAsync(
            run.Id, "worker", now, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
        await db.ScheduleRuns.Where(item => item.Id == run.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.CancellationRequestedUtc, now),
                TestContext.Current.CancellationToken);

        var completed = await leases.TryCompleteAsync(
            run.Id, "worker", "{}", now.AddSeconds(1), TestContext.Current.CancellationToken);

        Assert.Equal(0, completed);
    }
}
