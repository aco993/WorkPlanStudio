using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Data;
using WorkPlanStudio.Models;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Validation;

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

        return await DatabaseMutation.RunAsync<PlantSettings>(
            _db,
            async (db, token) =>
            {
                var existing = await db.PlantSettings.FirstOrDefaultAsync(s => s.Id == PlantSettings.SingletonId, token);
                if (existing is null)
                {
                    existing = new PlantSettings();
                    db.PlantSettings.Add(existing);
                }

                existing.State = settings.State.Trim().ToUpperInvariant();
                existing.IncludePartialHolidays = settings.IncludePartialHolidays;
                existing.AllowExtendedDay = settings.AllowExtendedDay;
                existing.AveragingWindow = settings.AveragingWindow;
                existing.AllowExtendedNight = settings.AllowExtendedNight;
                existing.SundayWorkAllowed = settings.SundayWorkAllowed;
                existing.HolidayWorkAllowed = settings.HolidayWorkAllowed;
                existing.SundayRotationWeeks = settings.SundayRotationWeeks;
                existing.SundayBoundaryShiftHours = settings.SundayBoundaryShiftHours;
                existing.RestExceptionSector = settings.RestExceptionSector;
                existing.MinimumRestHours = settings.MinimumRestHours;
                existing.ModifiedUtc = DateTime.UtcNow;

                return ApplicationResult<Func<PlantSettings>>.Success(() => existing);
            },
            cancellationToken: cancellationToken);
    }
}
