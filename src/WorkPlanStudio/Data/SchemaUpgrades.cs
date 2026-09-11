using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkPlanStudio.Models;

namespace WorkPlanStudio.Data;

/// <summary>
/// The upgrade path a stored browser database takes when the schema moves on.
/// <para>
/// The app has no EF migrations and creates its schema with
/// <c>EnsureCreated</c>, which cannot evolve one. Until now that meant a schema
/// bump destroyed every returning visitor's data: the recovery screen's only two
/// buttons were "export a file nothing can read" and "reset". This is the
/// missing third option.
/// </para>
/// <para>
/// The shape is deliberately not "write the ALTER statements by hand". Hand-written
/// DDL has to stay identical to whatever <c>EnsureCreated</c> produces from the
/// model, and it silently stops being identical the first time someone adds a
/// property. Instead each step <b>reads the old database with its own known
/// shape</b>, lets EF build the current schema from the model, and writes the
/// rows back with their original ids — so the primary keys that released orders
/// have frozen into their routing snapshots still resolve.
/// </para>
/// </summary>
public static class SchemaUpgrades
{
    /// <summary>
    /// The schema the running build expects. Kept beside the steps rather than in
    /// <c>Program.cs</c> so that adding a step and forgetting the bump is one
    /// edit, not two.
    /// </summary>
    public const int CurrentVersion = 7;

    private static readonly int[] UpgradableFrom = [5, 6];

    /// <summary>True when a payload stamped <paramref name="storedVersion"/> can still be rescued.</summary>
    public static bool CanUpgradeFrom(int storedVersion) => UpgradableFrom.Contains(storedVersion);

    /// <summary>
    /// Reads everything out of the old database at <paramref name="sourcePath"/>,
    /// recreates the current schema through <paramref name="factory"/> and writes
    /// the rows back.
    /// </summary>
    /// <param name="factory">Context factory bound to the <b>target</b> database path.</param>
    /// <param name="sourcePath">The restored old payload, on disk.</param>
    /// <param name="targetPath">Where the upgraded database must end up.</param>
    /// <param name="storedVersion">The version stamped on the payload.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task ApplyAsync(
        IDbContextFactory<AppDbContext> factory,
        string sourcePath,
        string targetPath,
        int storedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        ILegacyPayload legacy = storedVersion switch
        {
            5 => await LegacyV5.ReadAsync(sourcePath, cancellationToken),
            6 => await LegacyV6.ReadAsync(sourcePath, cancellationToken),
            _ => throw new InvalidOperationException($"No upgrade step from schema version {storedVersion}.")
        };

        if (File.Exists(targetPath))
            File.Delete(targetPath);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await legacy.WriteAsync(db, cancellationToken);
    }

    /// <summary>
    /// The statutory defaults a row written before a setting existed has to take
    /// on. Named rather than inlined as literals because they are the answer to
    /// "what did this plant mean before it could say?", and the safe answer is
    /// always the one that claims no permission: 24 weeks (§ 3 s. 2), no § 5 (2)
    /// sector, and a rota of one week — the reading under which a crew that works
    /// Sundays works all of them.
    /// </summary>
    private static class DefaultsForUpgradedRows
    {
        public const int AveragingWindow = (int)WorkingTime.AveragingWindow.TwentyFourWeeks;
        public const int RestExceptionSector = (int)WorkingTime.RestExceptionSector.None;
        public const int SundayRotationWeeks = 1;

        /// <summary>
        /// A rest of ten hours stored before the sector column existed has no
        /// sector behind it, so the new CHECK constraint would refuse the row and
        /// take the whole upgrade with it. Eleven hours is the only reading that
        /// keeps both the database and the statute: the shortening was a
        /// permission the old schema could not qualify, and an unqualified
        /// § 5 (2) is no permission at all. The planner takes it back on the page,
        /// this time by naming the sector.
        /// </summary>
        public const int MinimumRestHours = 11;
    }

    /// <summary>One stored shape, able to write itself into the current schema.</summary>
    private interface ILegacyPayload
    {
        Task WriteAsync(AppDbContext db, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Everything schema 5 held, in the shape schema 5 held it. Frozen on
    /// purpose: it must keep describing the old database after the model moves on.
    /// </summary>
    private sealed class LegacyV5 : ILegacyPayload
    {
        private readonly List<object?[]> _workCenters = [];
        private readonly List<object?[]> _absences = [];
        private readonly List<object?[]> _plantSettings = [];
        private readonly List<object?[]> _workPlans = [];
        private readonly List<object?[]> _operations = [];
        private readonly List<object?[]> _orders = [];

        public static async Task<LegacyV5> ReadAsync(string path, CancellationToken cancellationToken)
        {
            var legacy = new LegacyV5();
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync(cancellationToken);

            await ReadInto(connection, legacy._workCenters, 8,
                "SELECT Id, Code, Name, CostCenter, HourlyRate, ParallelCapacity, ShiftPatternKey, IsActive FROM WorkCenters", cancellationToken);
            await ReadInto(connection, legacy._absences, 6,
                "SELECT Id, WorkCenterId, Start, \"End\", Kind, Label FROM WorkCenterAbsences", cancellationToken);
            await ReadInto(connection, legacy._plantSettings, 10,
                "SELECT Id, State, IncludePartialHolidays, AllowExtendedDay, AllowExtendedNight, SundayWorkAllowed, "
                + "HolidayWorkAllowed, SundayBoundaryShiftHours, MinimumRestHours, ModifiedUtc FROM PlantSettings", cancellationToken);
            await ReadInto(connection, legacy._workPlans, 9,
                "SELECT Id, PlanNumber, PartNumber, PartName, Revision, Status, LotSize, CreatedUtc, ModifiedUtc FROM WorkPlans", cancellationToken);
            await ReadInto(connection, legacy._operations, 8,
                "SELECT Id, WorkPlanId, OperationNumber, Description, WorkCenterId, SetupTimeMinutes, TimePerPieceMinutes, Remarks FROM Operations", cancellationToken);
            await ReadInto(connection, legacy._orders, 12,
                "SELECT Id, OrderNumber, WorkPlanId, Quantity, ReleaseUtc, DueUtc, Priority, Status, RoutingRevision, "
                + "RoutingSnapshotJson, CreatedUtc, ModifiedUtc FROM ProductionOrders", cancellationToken);

            return legacy;
        }

        public async Task WriteAsync(AppDbContext db, CancellationToken cancellationToken)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            var connection = db.Database.GetDbConnection();

            // Cost centres first: the work centres are rewritten to point at them.
            var costCenterIdByCode = await PromoteCostCentresAsync(connection, cancellationToken);

            foreach (var row in _workCenters)
            {
                var legacyCode = Text(row[3])?.Trim() ?? "";
                object? costCenterId = legacyCode.Length > 0 && costCenterIdByCode.TryGetValue(legacyCode, out var id)
                    ? id
                    : null;

                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO WorkCenters (Id, Code, Name, CostCenterId, HourlyRate, ParallelCapacity, ShiftPatternKey, IsActive) "
                    + "VALUES ($0, $1, $2, $3, $4, $5, $6, $7)",
                    row[0], row[1], row[2], costCenterId, AsDecimalText(row[4]), row[5], row[6], row[7]);
            }

            foreach (var row in _absences)
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO WorkCenterAbsences (Id, WorkCenterId, Start, \"End\", Kind, Label) VALUES ($0, $1, $2, $3, $4, $5)",
                    row);

            // Schema 5 predates all three § settings, so they take the values a
            // plant that never declared them is entitled to — see
            // <see cref="DefaultsForUpgradedRows"/>.
            foreach (var row in _plantSettings)
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO PlantSettings (Id, State, IncludePartialHolidays, AllowExtendedDay, AveragingWindow, AllowExtendedNight, "
                    + "SundayWorkAllowed, HolidayWorkAllowed, SundayRotationWeeks, SundayBoundaryShiftHours, "
                    + "RestExceptionSector, MinimumRestHours, ModifiedUtc) "
                    + "VALUES ($0, $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)",
                    row[0], row[1], row[2], row[3], DefaultsForUpgradedRows.AveragingWindow, row[4], row[5], row[6],
                    DefaultsForUpgradedRows.SundayRotationWeeks, row[7], DefaultsForUpgradedRows.RestExceptionSector,
                    DefaultsForUpgradedRows.MinimumRestHours, row[9]);

            foreach (var row in _workPlans)
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO WorkPlans (Id, PlanNumber, PartNumber, PartName, Revision, Status, LotSize, CreatedUtc, ModifiedUtc) "
                    + "VALUES ($0, $1, $2, $3, $4, $5, $6, $7, $8)",
                    row);

            foreach (var row in _operations)
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO Operations (Id, WorkPlanId, OperationNumber, Description, WorkCenterId, SetupTimeMinutes, TimePerPieceMinutes, Remarks) "
                    + "VALUES ($0, $1, $2, $3, $4, $5, $6, $7)",
                    row[0], row[1], row[2], row[3], row[4], AsDecimalText(row[5]), AsDecimalText(row[6]), row[7]);

            foreach (var row in _orders)
            {
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO ProductionOrders (Id, OrderNumber, WorkPlanId, Quantity, ReleaseLocal, DueLocal, Priority, Status, "
                    + "RoutingRevision, RoutingSnapshotJson, CreatedUtc, ModifiedUtc) "
                    + "VALUES ($0, $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)",
                    row);

                // The work centres a released order still depends on were only ever
                // inside the JSON blob, where no guard and no foreign key could see
                // them. Lift them out now, for the orders that already exist.
                if (Convert.ToInt32(row[7], CultureInfo.InvariantCulture) != (int)ProductionOrderStatus.Released)
                    continue;

                var snapshot = RoutingSnapshot.Deserialize(Text(row[9]));
                foreach (var workCenterId in snapshot?.WorkCenterIds ?? [])
                    await ExecuteAsync(connection, cancellationToken,
                        "INSERT OR IGNORE INTO OrderRoutingCenters (ProductionOrderId, WorkCenterId) "
                        + "SELECT $0, $1 WHERE EXISTS (SELECT 1 FROM WorkCenters WHERE Id = $1)",
                        row[0], workCenterId);
            }
        }

        /// <summary>
        /// Turns the repeated free-text cost-centre strings into rows. Trimmed,
        /// de-duplicated case-insensitively, first spelling wins — so
        /// <c>CC-2000</c> and <c>cc-2000</c> become the one cost centre they were
        /// always meant to be. The name starts out as the code because the old
        /// column never held one; the planner renames it on the new page.
        /// </summary>
        private async Task<Dictionary<string, long>> PromoteCostCentresAsync(
            DbConnection connection,
            CancellationToken cancellationToken)
        {
            var byCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long next = 1;

            foreach (var row in _workCenters)
            {
                var code = Text(row[3])?.Trim() ?? "";
                if (code.Length == 0 || byCode.ContainsKey(code))
                    continue;

                // The old column had no CHECK, so a 21-character value could be in
                // there. Truncating keeps the row rather than failing the upgrade.
                var stored = code.Length > 20 ? code[..20] : code;
                if (byCode.ContainsKey(stored))
                {
                    byCode[code] = byCode[stored];
                    continue;
                }

                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO CostCenters (Id, Code, Name, Description, IsActive) VALUES ($0, $1, $2, NULL, 1)",
                    next, stored, stored);
                byCode[stored] = next;
                byCode[code] = next;
                next++;
            }

            return byCode;
        }

    }

    /// <summary>
    /// Everything schema 6 held, in the shape schema 6 held it: cost centres are
    /// already master data and the order dates are already plant-local, so the
    /// step is narrower than the one before it — three columns the settings row
    /// did not have.
    /// </summary>
    private sealed class LegacyV6 : ILegacyPayload
    {
        private readonly List<object?[]> _costCenters = [];
        private readonly List<object?[]> _workCenters = [];
        private readonly List<object?[]> _absences = [];
        private readonly List<object?[]> _plantSettings = [];
        private readonly List<object?[]> _workPlans = [];
        private readonly List<object?[]> _operations = [];
        private readonly List<object?[]> _orders = [];
        private readonly List<object?[]> _routingCenters = [];

        public static async Task<LegacyV6> ReadAsync(string path, CancellationToken cancellationToken)
        {
            var legacy = new LegacyV6();
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync(cancellationToken);

            await ReadInto(connection, legacy._costCenters, 5,
                "SELECT Id, Code, Name, Description, IsActive FROM CostCenters", cancellationToken);
            await ReadInto(connection, legacy._workCenters, 8,
                "SELECT Id, Code, Name, CostCenterId, HourlyRate, ParallelCapacity, ShiftPatternKey, IsActive FROM WorkCenters", cancellationToken);
            await ReadInto(connection, legacy._absences, 6,
                "SELECT Id, WorkCenterId, Start, \"End\", Kind, Label FROM WorkCenterAbsences", cancellationToken);
            await ReadInto(connection, legacy._plantSettings, 10,
                "SELECT Id, State, IncludePartialHolidays, AllowExtendedDay, AllowExtendedNight, SundayWorkAllowed, "
                + "HolidayWorkAllowed, SundayBoundaryShiftHours, MinimumRestHours, ModifiedUtc FROM PlantSettings", cancellationToken);
            await ReadInto(connection, legacy._workPlans, 9,
                "SELECT Id, PlanNumber, PartNumber, PartName, Revision, Status, LotSize, CreatedUtc, ModifiedUtc FROM WorkPlans", cancellationToken);
            await ReadInto(connection, legacy._operations, 8,
                "SELECT Id, WorkPlanId, OperationNumber, Description, WorkCenterId, SetupTimeMinutes, TimePerPieceMinutes, Remarks FROM Operations", cancellationToken);
            await ReadInto(connection, legacy._orders, 12,
                "SELECT Id, OrderNumber, WorkPlanId, Quantity, ReleaseLocal, DueLocal, Priority, Status, RoutingRevision, "
                + "RoutingSnapshotJson, CreatedUtc, ModifiedUtc FROM ProductionOrders", cancellationToken);
            await ReadInto(connection, legacy._routingCenters, 2,
                "SELECT ProductionOrderId, WorkCenterId FROM OrderRoutingCenters", cancellationToken);

            return legacy;
        }

        public async Task WriteAsync(AppDbContext db, CancellationToken cancellationToken)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            var connection = db.Database.GetDbConnection();

            foreach (var row in _costCenters)
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO CostCenters (Id, Code, Name, Description, IsActive) VALUES ($0, $1, $2, $3, $4)", row);

            foreach (var row in _workCenters)
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO WorkCenters (Id, Code, Name, CostCenterId, HourlyRate, ParallelCapacity, ShiftPatternKey, IsActive) "
                    + "VALUES ($0, $1, $2, $3, $4, $5, $6, $7)", row);

            foreach (var row in _absences)
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO WorkCenterAbsences (Id, WorkCenterId, Start, \"End\", Kind, Label) VALUES ($0, $1, $2, $3, $4, $5)",
                    row);

            foreach (var row in _plantSettings)
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO PlantSettings (Id, State, IncludePartialHolidays, AllowExtendedDay, AveragingWindow, AllowExtendedNight, "
                    + "SundayWorkAllowed, HolidayWorkAllowed, SundayRotationWeeks, SundayBoundaryShiftHours, "
                    + "RestExceptionSector, MinimumRestHours, ModifiedUtc) "
                    + "VALUES ($0, $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)",
                    row[0], row[1], row[2], row[3], DefaultsForUpgradedRows.AveragingWindow, row[4], row[5], row[6],
                    DefaultsForUpgradedRows.SundayRotationWeeks, row[7], DefaultsForUpgradedRows.RestExceptionSector,
                    DefaultsForUpgradedRows.MinimumRestHours, row[9]);

            foreach (var row in _workPlans)
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO WorkPlans (Id, PlanNumber, PartNumber, PartName, Revision, Status, LotSize, CreatedUtc, ModifiedUtc) "
                    + "VALUES ($0, $1, $2, $3, $4, $5, $6, $7, $8)", row);

            foreach (var row in _operations)
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO Operations (Id, WorkPlanId, OperationNumber, Description, WorkCenterId, SetupTimeMinutes, TimePerPieceMinutes, Remarks) "
                    + "VALUES ($0, $1, $2, $3, $4, $5, $6, $7)", row);

            foreach (var row in _orders)
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT INTO ProductionOrders (Id, OrderNumber, WorkPlanId, Quantity, ReleaseLocal, DueLocal, Priority, Status, "
                    + "RoutingRevision, RoutingSnapshotJson, CreatedUtc, ModifiedUtc) "
                    + "VALUES ($0, $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)", row);

            // Schema 6 already has the routing index, so it is copied rather than
            // reconstructed from the snapshot blob: an order whose snapshot names
            // a centre that has since been deleted must not gain a row here that
            // schema 6 deliberately did not have.
            foreach (var row in _routingCenters)
                await ExecuteAsync(connection, cancellationToken,
                    "INSERT OR IGNORE INTO OrderRoutingCenters (ProductionOrderId, WorkCenterId) VALUES ($0, $1)", row);
        }
    }

    private static async Task ReadInto(
        DbConnection connection,
        List<object?[]> rows,
        int columns,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new object?[columns];
            for (int i = 0; i < columns; i++)
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(values);
        }
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        CancellationToken cancellationToken,
        string sql,
        params object?[] values)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (int i = 0; i < values.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"${i}";
            parameter.Value = values[i] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? Text(object? value) =>
        value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Schema 5 declared money and minutes as <c>decimal(10,2)</c>, which gives
    /// SQLite NUMERIC affinity — so they are sitting in the old file as binary
    /// doubles. Rendering them as text here is what moves them onto the TEXT
    /// columns the new schema uses; the value does not change, only the way it
    /// is stored stops being lossy from here on.
    /// </summary>
    private static string AsDecimalText(object? value) => value switch
    {
        null => "0",
        double number => number.ToString("0.####################", CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0"
    };
}
