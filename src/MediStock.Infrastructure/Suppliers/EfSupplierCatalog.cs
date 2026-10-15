using System.Linq.Expressions;
using MediStock.Application.Common;
using MediStock.Application.Suppliers;
using MediStock.Domain.Common;
using MediStock.Domain.Suppliers;
using MediStock.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Suppliers;

/// <summary>Entity Framework Core implementation of the supplier register.</summary>
internal sealed class EfSupplierCatalog : ISupplierCatalog
{
    /// <summary>
    /// Single projection so every query returns the same shape. Written as an expression tree because a
    /// method call would not translate to SQL.
    /// </summary>
    private static readonly Expression<Func<Supplier, SupplierDto>> Projection = s => new SupplierDto
    {
        Id = s.Id,
        Name = s.Name,
        ContactPerson = s.ContactPerson,
        Phone = s.Phone,
        Email = s.Email,
        AddressLine = s.AddressLine,
        City = s.City,
        State = s.State,
        PostalCode = s.PostalCode,
        Gstin = s.Gstin,
        DrugLicenseNumber = s.DrugLicenseNumber,
        Notes = s.Notes,
        IsActive = s.IsActive,
        CreatedAtUtc = s.CreatedAtUtc,
        UpdatedAtUtc = s.UpdatedAtUtc,
    };

    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IClock _clock;
    private readonly ILogger<EfSupplierCatalog> _logger;

    public EfSupplierCatalog(
        IDbContextFactory<MediStockDbContext> factory,
        IClock clock,
        ILogger<EfSupplierCatalog> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<SupplierDto>> SearchAsync(
        SupplierSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = db.Suppliers.AsNoTracking();

        if (!query.IncludeInactive)
        {
            source = source.Where(s => s.IsActive);
        }

        var term = Normalise(query.Term);
        if (term is not null)
        {
            var pattern = $"%{EscapeLike(term)}%";
            source = source.Where(s =>
                EF.Functions.Like(s.Name, pattern)
                || (s.ContactPerson != null && EF.Functions.Like(s.ContactPerson, pattern))
                || (s.Phone != null && EF.Functions.Like(s.Phone, pattern))
                || (s.Email != null && EF.Functions.Like(s.Email, pattern))
                || (s.Gstin != null && EF.Functions.Like(s.Gstin, pattern))
                || (s.DrugLicenseNumber != null && EF.Functions.Like(s.DrugLicenseNumber, pattern))
                || (s.City != null && EF.Functions.Like(s.City, pattern)));
        }

        var take = Math.Clamp(query.Take, 1, SupplierSearchQuery.MaxResults);

        return await source
            .OrderBy(s => s.Name)
            .ThenBy(s => s.Id)
            .Take(take)
            .Select(Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SupplierDto?> FindAsync(int supplierId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await db.Suppliers
            .AsNoTracking()
            .Where(s => s.Id == supplierId)
            .Select(Projection)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> CountAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = db.Suppliers.AsNoTracking();
        if (!includeInactive)
        {
            source = source.Where(s => s.IsActive);
        }

        return await source.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<SupplierDto>> CreateAsync(
        SupplierDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        Supplier supplier;
        try
        {
            supplier = draft.ToNewSupplier(_clock.UtcNow);
        }
        catch (DomainValidationException ex)
        {
            return OperationResult<SupplierDto>.Fail(ex.Message);
        }

        db.Suppliers.Add(supplier);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
        {
            db.Entry(supplier).State = EntityState.Detached;
            return OperationResult<SupplierDto>.Fail(message);
        }

        _logger.LogInformation("Created supplier {SupplierId} ({Name}).", supplier.Id, supplier.Name);

        return OperationResult<SupplierDto>.Success(Project(supplier));
    }

    public async Task<OperationResult<SupplierDto>> UpdateAsync(
        int supplierId,
        SupplierDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var supplier = await db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken)
            .ConfigureAwait(false);

        if (supplier is null)
        {
            return OperationResult<SupplierDto>.Fail($"Supplier {supplierId} no longer exists.");
        }

        try
        {
            draft.ApplyTo(supplier, _clock.UtcNow);
        }
        catch (DomainValidationException ex)
        {
            return OperationResult<SupplierDto>.Fail(ex.Message);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
        {
            return OperationResult<SupplierDto>.Fail(message);
        }

        _logger.LogInformation("Updated supplier {SupplierId} ({Name}).", supplier.Id, supplier.Name);

        return OperationResult<SupplierDto>.Success(Project(supplier));
    }

    public async Task<OperationResult> SetActiveAsync(
        int supplierId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var supplier = await db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken)
            .ConfigureAwait(false);

        if (supplier is null)
        {
            return OperationResult.Fail($"Supplier {supplierId} no longer exists.");
        }

        if (isActive)
        {
            supplier.Reactivate(_clock.UtcNow);
        }
        else
        {
            supplier.Deactivate(_clock.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Supplier {SupplierId} ({Name}) was {Action}.",
            supplier.Id,
            supplier.Name,
            isActive ? "reactivated" : "deactivated");

        return OperationResult.Success();
    }

    private static SupplierDto Project(Supplier supplier) => new()
    {
        Id = supplier.Id,
        Name = supplier.Name,
        ContactPerson = supplier.ContactPerson,
        Phone = supplier.Phone,
        Email = supplier.Email,
        AddressLine = supplier.AddressLine,
        City = supplier.City,
        State = supplier.State,
        PostalCode = supplier.PostalCode,
        Gstin = supplier.Gstin,
        DrugLicenseNumber = supplier.DrugLicenseNumber,
        Notes = supplier.Notes,
        IsActive = supplier.IsActive,
        CreatedAtUtc = supplier.CreatedAtUtc,
        UpdatedAtUtc = supplier.UpdatedAtUtc,
    };

    private static string? Normalise(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string term)
        => term.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);

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
                message = sql.Message.Contains("UX_Suppliers_Gstin", StringComparison.OrdinalIgnoreCase)
                    ? "Another supplier already uses this GSTIN. Check the register before adding a second record."
                    : "Another supplier already has this name. Open the existing record or make the name distinct.";
                return true;

            case 547:
                message = "The change was rejected by a database rule. Check the values and try again.";
                return true;

            default:
                return false;
        }
    }
}
