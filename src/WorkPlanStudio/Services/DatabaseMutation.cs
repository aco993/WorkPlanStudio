using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Validation;

namespace WorkPlanStudio.Services;

/// <summary>
/// The one place a mutation is committed, so every service boundary behaves the
/// same way.
/// <para>
/// It exists because the four services did not. Nine <c>SaveChangesAsync</c> call
/// sites, five of them wrapped in a <c>try/catch</c> and four bare — and one of
/// the bare ones replaced the whole application with the error boundary when a
/// planner deleted a work plan that the shipped seed data already references.
/// A typed result that half the boundaries can throw past is not a typed result.
/// </para>
/// <para>
/// It also closes the gap between the commit and the durable snapshot. SQLite
/// commits first and <c>PersistAsync</c> runs after it, so a failed snapshot used
/// to leave the change live for the rest of the session and gone after a reload,
/// while the page said the save had failed. Here the snapshot is part of the
/// success condition: when it fails, the database file is put back the way it was.
/// </para>
/// </summary>
internal static class DatabaseMutation
{
    /// <summary>
    /// Stages a change, commits it, and only reports success once it is durable.
    /// </summary>
    /// <typeparam name="T">What the caller's result carries.</typeparam>
    /// <param name="database">The browser database.</param>
    /// <param name="stage">
    /// Runs the checks and stages the change. It must not call
    /// <c>SaveChangesAsync</c>. On success it returns a function read
    /// <em>after</em> the save, so a caller can return a key the database assigns.
    /// </param>
    /// <param name="constraintIssue">
    /// What a database constraint violation means in business terms, when the
    /// caller's own pre-check can be raced or forgotten. Without it a violation
    /// is reported as a persistence failure.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<ApplicationResult<T>> RunAsync<T>(
        BrowserDatabase database,
        Func<AppDbContext, CancellationToken, Task<ApplicationResult<Func<T>>>> stage,
        ValidationIssue? constraintIssue = null,
        CancellationToken cancellationToken = default)
    {
        var preImage = await database.CaptureAsync(cancellationToken);

        T value;
        await using (var db = await database.CreateContextAsync(cancellationToken))
        {
            ApplicationResult<Func<T>> staged;
            try
            {
                staged = await stage(db, cancellationToken);
                if (!staged.IsSuccess)
                    return Carry<T>(staged);

                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
            {
                db.ChangeTracker.Clear();
                return Translate<T>(exception, constraintIssue);
            }

            value = staged.Value!();
        }

        var persisted = await database.PersistAsync(cancellationToken);
        if (persisted.IsSuccess)
            return ApplicationResult<T>.Success(value);

        await database.RestoreAsync(preImage, cancellationToken);

        // Every path puts the database back; they differ in what the person in
        // front of the screen can do about it, which is the only reason to tell
        // them apart.
        return persisted.Failure switch
        {
            BrowserDatabaseFailure.QuotaExceeded => ApplicationResult<T>.StorageFull(),
            BrowserDatabaseFailure.ChangedElsewhere => ApplicationResult<T>.ChangedElsewhere(),
            _ => ApplicationResult<T>.PersistenceFailed()
        };
    }

    /// <summary>Re-types a failed staging result; the value is absent either way.</summary>
    private static ApplicationResult<T> Carry<T>(ApplicationResult<Func<T>> staged) =>
        new(staged.Status, default, staged.ValidationIssues);

    /// <summary>
    /// A foreign-key or CHECK violation is a business conflict, not a storage
    /// outage. Only the constraint family is translated: anything else really is
    /// a failure to persist.
    /// </summary>
    private static ApplicationResult<T> Translate<T>(DbUpdateException exception, ValidationIssue? constraintIssue) =>
        constraintIssue is not null && exception.InnerException is SqliteException { SqliteErrorCode: 19 }
            ? ApplicationResult<T>.Conflict(constraintIssue)
            : ApplicationResult<T>.PersistenceFailed();
}
