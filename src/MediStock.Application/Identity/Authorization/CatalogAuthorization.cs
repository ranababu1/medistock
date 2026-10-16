using MediStock.Application.Catalogue;
using MediStock.Application.Dashboard;
using MediStock.Application.Inventory;
using MediStock.Application.Purchasing;
using MediStock.Application.Reporting;
using MediStock.Application.Sales;
using MediStock.Application.Suppliers;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.Application.Identity.Authorization;

/// <summary>
/// Wires the permission checks onto the catalogs.
/// </summary>
/// <remarks>
/// <para>
/// The guards live in this layer and this is the only place that knows how to put them on, so a new screen
/// cannot reach a catalog by resolving it a different way and quietly bypass the check. The concrete
/// catalog stays in the infrastructure layer, where the database lives; this layer only needs to know which
/// contract is being guarded.
/// </para>
/// <para>
/// A guard is a decorator rather than a check inside each catalog because the rule belongs to the
/// application layer, not to Entity Framework: the same rule has to hold for a report that re-reads stock,
/// for an import tool and for a test that calls the catalog directly.
/// </para>
/// </remarks>
public static class CatalogAuthorization
{
    /// <summary>
    /// The catalog contracts that carry a permission, and the guard each one gets.
    /// </summary>
    /// <remarks>
    /// A table rather than a chain of <c>if</c>s so that the set of guarded contracts can be listed, and
    /// tested, in one place. <see cref="AddGuardedCatalog{TCatalog, TImplementation}"/> constrains the
    /// implementation to the contract, so a row here cannot pair a guard with the wrong catalog.
    /// </remarks>
    private static readonly Dictionary<Type, Func<object, IUserSession, object>> Guards = new()
    {
        [typeof(IMedicineCatalog)] = (inner, session) => new GuardedMedicineCatalog((IMedicineCatalog)inner, session),
        [typeof(ISupplierCatalog)] = (inner, session) => new GuardedSupplierCatalog((ISupplierCatalog)inner, session),
        [typeof(IPurchaseCatalog)] = (inner, session) => new GuardedPurchaseCatalog((IPurchaseCatalog)inner, session),
        [typeof(ISaleCatalog)] = (inner, session) => new GuardedSaleCatalog((ISaleCatalog)inner, session),
        [typeof(ISaleReturnCatalog)] = (inner, session) => new GuardedSaleReturnCatalog((ISaleReturnCatalog)inner, session),
        [typeof(ISupplierReturnCatalog)] = (inner, session) => new GuardedSupplierReturnCatalog((ISupplierReturnCatalog)inner, session),
        [typeof(IInventoryOperations)] = (inner, session) => new GuardedInventoryOperations((IInventoryOperations)inner, session),
        [typeof(IReportQueries)] = (inner, session) => new GuardedReportQueries((IReportQueries)inner, session),
        [typeof(ICsvExporter)] = (inner, session) => new GuardedCsvExporter((ICsvExporter)inner, session),
    };

    /// <summary>
    /// Registers <typeparamref name="TImplementation"/> and exposes it as
    /// <typeparamref name="TCatalog"/>, wrapped in the permission guard that belongs to that contract.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the contract has no guard. That is a programming error, not a runtime condition: a new
    /// catalog contract must be given a permission deliberately rather than be exposed unguarded by default.
    /// </exception>
    public static IServiceCollection AddGuardedCatalog<TCatalog, TImplementation>(this IServiceCollection services)
        where TCatalog : class
        where TImplementation : class, TCatalog
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!Guards.TryGetValue(typeof(TCatalog), out var guard))
        {
            throw new InvalidOperationException(
                $"{typeof(TCatalog).Name} has no permission guard. Add one to " +
                $"{nameof(CatalogAuthorization)}.{nameof(Guards)} so it cannot be reached unguarded.");
        }

        services.AddScoped<TImplementation>();
        services.AddScoped<TCatalog>(provider => (TCatalog)guard(
            provider.GetRequiredService<TImplementation>(),
            provider.GetRequiredService<IUserSession>()));

        return services;
    }

    /// <summary>
    /// The contracts <see cref="AddGuardedCatalog{TCatalog, TImplementation}"/> knows how to guard.
    /// Exposed so a test can assert that every catalog in the application layer has a permission.
    /// </summary>
    public static IReadOnlyCollection<Type> GuardedContracts => Guards.Keys;

    /// <summary>
    /// The data contracts that deliberately carry no permission of their own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is registered through <see cref="AddGuardedCatalog{TCatalog, TImplementation}"/> here, so
    /// every one of these is reachable by any signed-in operator. Each is listed for a reason, and the list
    /// exists so that a contract added later cannot be left out of both sets by accident: a test asserts that
    /// every data contract in this assembly appears in <see cref="GuardedContracts"/> or in this list.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="IStockQueries"/> — batch balances, expiry and the movement history. This is what the
    /// pharmacy already knows by looking at the shelf, and a cashier has to be able to check it while a
    /// customer waits.
    /// </description></item>
    /// <item><description>
    /// <see cref="IDashboardQueries"/> — today's takings, low stock and near expiry. Deliberately today
    /// only, which is the figure the till itself already shows.
    /// </description></item>
    /// <item><description>
    /// <see cref="ISalesQueries"/> — completed invoices, so that a return can be found by its number. The
    /// reports, which are the management view of the same data, are guarded by
    /// <see cref="MediStock.Domain.Identity.Permission.RunReports"/>.
    /// </description></item>
    /// <item><description>
    /// <see cref="IReceiptExporter"/> — writes out the receipt for a sale the operator is already looking
    /// at. It reads no data it was not handed.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static IReadOnlyCollection<Type> UnguardedContracts { get; } =
    [
        typeof(IStockQueries),
        typeof(IDashboardQueries),
        typeof(ISalesQueries),
        typeof(IReceiptExporter),
    ];
}
