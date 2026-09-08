using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Validation;
using WorkPlanStudio.WorkingTime;

namespace WorkPlanStudio.Services;

/// <summary>Reads and writes the single <see cref="PlantSettings"/> row.</summary>
public sealed class PlantSettingsService
{
    private readonly BrowserDatabase _db;
    private readonly IPermissionGuard _guard;

    public PlantSettingsService(BrowserDatabase db, IPermissionGuard? guard = null)
    {
        _db = db;
        _guard = guard ?? AllowAllGuard.Instance;
    }

    /// <summary>The stored settings, or the statutory defaults when the row is missing.</summary>
    public async Task<PlantSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _db.CreateContextAsync(cancellationToken);
        return await db.PlantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == PlantSettings.SingletonId, cancellationToken)
               ?? new PlantSettings();
    }

    public async Task<ApplicationResult<PlantSettings>> SaveAsync(PlantSettings settings, CancellationToken cancellationToken = default)
    {
        if (!await _guard.CanAsync(Permissions.ManagePlantRules, cancellationToken))
            return ApplicationResult<PlantSettings>.Forbidden();

        ArgumentNullException.ThrowIfNull(settings);

        var issues = PlantSettingsValidator.Validate(settings);
        if (issues.Count > 0)
            return ApplicationResult<PlantSettings>.Validation(issues);

        await using (var db = await _db.CreateContextAsync(cancellationToken))
        {
            var existing = await db.PlantSettings.FirstOrDefaultAsync(s => s.Id == PlantSettings.SingletonId, cancellationToken);
            if (existing is null)
            {
                existing = new PlantSettings();
                db.PlantSettings.Add(existing);
            }

            existing.State = settings.State.Trim().ToUpperInvariant();
            existing.IncludePartialHolidays = settings.IncludePartialHolidays;
            existing.AllowExtendedDay = settings.AllowExtendedDay;
            existing.AllowExtendedNight = settings.AllowExtendedNight;
            existing.SundayWorkAllowed = settings.SundayWorkAllowed;
            existing.HolidayWorkAllowed = settings.HolidayWorkAllowed;
            existing.SundayBoundaryShiftHours = settings.SundayBoundaryShiftHours;
            existing.MinimumRestHours = settings.MinimumRestHours;
            existing.ModifiedUtc = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return ApplicationResult<PlantSettings>.PersistenceFailed();
            }
        }

        var persisted = await _db.PersistAsync(cancellationToken);
        return persisted.IsSuccess
            ? ApplicationResult<PlantSettings>.Success(settings)
            : ApplicationResult<PlantSettings>.PersistenceFailed();
    }
}

/// <summary>Business rules for the plant settings.</summary>
public static class PlantSettingsValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(PlantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var issues = new List<ValidationIssue>();

        if (!Enum.TryParse<GermanState>(settings.State?.Trim(), ignoreCase: true, out _))
            issues.Add(new(nameof(settings.State), "Val_StateInvalid"));
        if (settings.SundayBoundaryShiftHours is < 0 or > 6)
            issues.Add(new(nameof(settings.SundayBoundaryShiftHours), "Val_Range", 0, 6));
        if (settings.MinimumRestHours is < 10 or > 11)
            issues.Add(new(nameof(settings.MinimumRestHours), "Val_Range", 10, 11));

        return issues;
    }
}
