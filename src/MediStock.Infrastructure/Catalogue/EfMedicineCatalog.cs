using System.Globalization;
using System.Linq.Expressions;
using MediStock.Application.Catalogue;
using MediStock.Application.Common;
using MediStock.Application.Identity;
using MediStock.Domain.Catalogue;
using MediStock.Domain.Common;
using MediStock.Domain.Identity;
using MediStock.Infrastructure.Identity;
using MediStock.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Catalogue;

/// <summary>Entity Framework Core implementation of the medicine catalogue.</summary>
internal sealed class EfMedicineCatalog : IMedicineCatalog
{
    /// <summary>
    /// Single projection so every query returns the same shape. Written as an expression tree
    /// because a method call would not translate to SQL.
    /// </summary>
    private static readonly Expression<Func<Medicine, MedicineDto>> Projection = m => new MedicineDto(
        m.Id,
        m.DisplayName,
        m.BrandName,
        m.GenericName,
        m.Strength,
        m.DosageForm,
        m.Manufacturer,
        m.Barcode,
        m.PackDescription,
        m.ReorderThreshold,
        m.TaxRatePercent,
        m.IsActive,
        m.CreatedAtUtc,
        m.UpdatedAtUtc);

    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IClock _clock;
    private readonly IUserSession _session;
    private readonly ILogger<EfMedicineCatalog> _logger;

    public EfMedicineCatalog(
        IDbContextFactory<MediStockDbContext> factory,
        IClock clock,
        IUserSession session,
        ILogger<EfMedicineCatalog> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Adds an audit row naming the signed-in person as the actor. Medicine records carry no free-text
    /// operator box, so unlike a till or a receiving note there is nothing to reconcile: whoever is signed
    /// in is who changed the catalogue.
    /// </summary>
    private AuditEntry Stage(MediStockDbContext db, AuditRecord record)
    {
        var (actor, userId) = AuditJournal.ActorOf(_session);

        return AuditJournal.Stage(db, record, actor, userId);
    }

    public async Task<IReadOnlyList<MedicineDto>> SearchAsync(
        MedicineSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = db.Medicines.AsNoTracking();

        if (!query.IncludeInactive)
        {
            source = source.Where(m => m.IsActive);
        }

        var term = Normalise(query.Term);
        if (term is not null)
        {
            var pattern = $"%{EscapeLike(term)}%";
            source = source.Where(m =>
                EF.Functions.Like(m.DisplayName, pattern)
                || (m.BrandName != null && EF.Functions.Like(m.BrandName, pattern))
                || (m.GenericName != null && EF.Functions.Like(m.GenericName, pattern))
                || (m.Manufacturer != null && EF.Functions.Like(m.Manufacturer, pattern))
                || (m.Barcode != null && EF.Functions.Like(m.Barcode, pattern)));
        }

        var take = Math.Clamp(query.Take, 1, MedicineSearchQuery.MaxResults);

        return await source
            .OrderBy(m => m.DisplayName)
            .ThenBy(m => m.Id)
            .Take(take)
            .Select(Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MedicineDto?> FindAsync(int medicineId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await db.Medicines
            .AsNoTracking()
            .Where(m => m.Id == medicineId)
            .Select(Projection)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> CountAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = db.Medicines.AsNoTracking();
        if (!includeInactive)
        {
            source = source.Where(m => m.IsActive);
        }

        return await source.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<MedicineDto>> CreateAsync(
        MedicineDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        Medicine medicine;
        try
        {
            medicine = draft.ToNewMedicine(_clock.UtcNow);
        }
        catch (DomainValidationException ex)
        {
            return OperationResult<MedicineDto>.Fail(ex.Message);
        }

        db.Medicines.Add(medicine);

        Stage(
            db,
            new AuditRecord(
                AuditAction.MedicineCreated,
                $"Medicine {medicine.DisplayName} was added to the catalogue.")
            {
                EntityType = "Medicine",
                EntityId = medicine.Barcode,
                Detail = $"{medicine.BrandName} / {medicine.GenericName}, {medicine.Strength} {medicine.DosageForm}.",
            });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
        {
            db.Entry(medicine).State = EntityState.Detached;
            return OperationResult<MedicineDto>.Fail(message);
        }

        _logger.LogInformation("Created medicine {MedicineId} ({DisplayName}).", medicine.Id, medicine.DisplayName);

        return OperationResult<MedicineDto>.Success(Project(medicine));
    }

    public async Task<OperationResult<MedicineDto>> UpdateAsync(
        int medicineId,
        MedicineDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var medicine = await db.Medicines
            .FirstOrDefaultAsync(m => m.Id == medicineId, cancellationToken)
            .ConfigureAwait(false);

        if (medicine is null)
        {
            return OperationResult<MedicineDto>.Fail($"Medicine {medicineId} no longer exists.");
        }

        // Read before the edit: the tax rate decides what the customer pays, so a change to it is a price
        // change and the trail has to show the before and after.
        var previousTaxRate = medicine.TaxRatePercent;

        try
        {
            draft.ApplyTo(medicine, _clock.UtcNow);
        }
        catch (DomainValidationException ex)
        {
            return OperationResult<MedicineDto>.Fail(ex.Message);
        }

        var taxMoved = previousTaxRate != medicine.TaxRatePercent;

        Stage(
            db,
            new AuditRecord(
                taxMoved ? AuditAction.PriceChanged : AuditAction.MedicineUpdated,
                taxMoved
                    ? $"The tax rate on {medicine.DisplayName} was changed."
                    : $"Medicine {medicine.DisplayName} was edited.")
            {
                EntityType = "Medicine",
                EntityId = medicine.Barcode,
                Detail = taxMoved
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "Tax rate {0:0.####}% → {1:0.####}%.",
                        previousTaxRate,
                        medicine.TaxRatePercent)
                    : null,
            });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
        {
            return OperationResult<MedicineDto>.Fail(message);
        }

        _logger.LogInformation("Updated medicine {MedicineId} ({DisplayName}).", medicine.Id, medicine.DisplayName);

        return OperationResult<MedicineDto>.Success(Project(medicine));
    }

    public async Task<OperationResult> SetActiveAsync(
        int medicineId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var medicine = await db.Medicines
            .FirstOrDefaultAsync(m => m.Id == medicineId, cancellationToken)
            .ConfigureAwait(false);

        if (medicine is null)
        {
            return OperationResult.Fail($"Medicine {medicineId} no longer exists.");
        }

        if (isActive)
        {
            medicine.Reactivate(_clock.UtcNow);
        }
        else
        {
            medicine.Deactivate(_clock.UtcNow);
        }

        Stage(
            db,
            new AuditRecord(
                isActive ? AuditAction.MedicineReactivated : AuditAction.MedicineDeactivated,
                $"Medicine {medicine.DisplayName} was {(isActive ? "reactivated" : "deactivated")}.")
            {
                EntityType = "Medicine",
                EntityId = medicine.Barcode,
                Detail = isActive
                    ? null
                    : "Historical sales and batches keep referring to it, so the record itself is kept.",
            });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
        {
            return OperationResult.Fail(message);
        }

        _logger.LogInformation(
            "Medicine {MedicineId} ({DisplayName}) was {Action}.",
            medicine.Id,
            medicine.DisplayName,
            isActive ? "reactivated" : "deactivated");

        return OperationResult.Success();
    }

    public async Task<IReadOnlyList<MedicineDto>> FindPotentialDuplicatesAsync(
        MedicineDraft draft,
        int? excludeMedicineId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var name = Normalise(draft.DisplayName);
        var barcode = Normalise(draft.Barcode);
        var generic = Normalise(draft.GenericName);
        var brand = Normalise(draft.BrandName);
        var strength = Normalise(draft.Strength);

        if (name is null && barcode is null && generic is null)
        {
            return [];
        }

        var source = db.Medicines.AsNoTracking();
        if (excludeMedicineId is { } excluded)
        {
            source = source.Where(m => m.Id != excluded);
        }

        return await source
            .Where(m =>
                (barcode != null && m.Barcode != null && m.Barcode == barcode)
                || (name != null && m.DisplayName == name)
                || (generic != null && m.GenericName == generic && m.Strength == strength && m.BrandName == brand))
            .OrderBy(m => m.DisplayName)
            .Take(10)
            .Select(Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static MedicineDto Project(Medicine medicine) => new(
        medicine.Id,
        medicine.DisplayName,
        medicine.BrandName,
        medicine.GenericName,
        medicine.Strength,
        medicine.DosageForm,
        medicine.Manufacturer,
        medicine.Barcode,
        medicine.PackDescription,
        medicine.ReorderThreshold,
        medicine.TaxRatePercent,
        medicine.IsActive,
        medicine.CreatedAtUtc,
        medicine.UpdatedAtUtc);

    private static string? Normalise(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Removes LIKE wildcards from operator input. Without this a search for "50%" would match
    /// every row instead of none.
    /// </summary>
    private static string EscapeLike(string term)
        => term.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);

    /// <summary>
    /// Turns a SQL unique-index violation or check-constraint failure into a message an operator
    /// can act on, instead of surfacing a raw database error.
    /// </summary>
    private static bool TryDescribeConstraintViolation(DbUpdateException exception, out string message)
    {
        message = string.Empty;

        if (exception.InnerException is not SqlException sql)
        {
            return false;
        }

        switch (sql.Number)
        {
            case 2601:
            case 2627:
                message = sql.Message.Contains("UX_Medicines_Barcode", StringComparison.OrdinalIgnoreCase)
                    ? "Another medicine already uses this barcode. Clear the barcode or merge the records."
                    : "A record with these details already exists.";
                return true;

            case 547:
                message = "The change was rejected by a database rule. Check the values and try again.";
                return true;

            default:
                return false;
        }
    }
}
