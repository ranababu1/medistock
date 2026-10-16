using System.Reflection;
using MediStock.Application.Catalogue;
using MediStock.Application.Dashboard;
using MediStock.Application.Identity;
using MediStock.Application.Identity.Authorization;
using MediStock.Application.Inventory;
using MediStock.Application.Purchasing;
using MediStock.Application.Reporting;
using MediStock.Application.Sales;
using MediStock.Application.Suppliers;
using MediStock.Domain.Common;
using MediStock.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.Application.Tests.Identity;

/// <summary>
/// The permission guards and the two lists that place every catalog contract in one of them.
/// </summary>
/// <remarks>
/// These are the tests that keep the guards honest as the application grows. A contract that is in neither
/// list is one somebody has to make a decision about; without the reflection test the decision would be made
/// by omission, and the contract would be reachable by everybody.
/// </remarks>
public sealed class CatalogAuthorizationTests
{
    /// <summary>The namespaces whose interfaces are the data contracts a screen resolves.</summary>
    private static readonly string[] DataContractNamespaces =
    [
        "MediStock.Application.Catalogue",
        "MediStock.Application.Suppliers",
        "MediStock.Application.Purchasing",
        "MediStock.Application.Sales",
        "MediStock.Application.Inventory",
        "MediStock.Application.Reporting",
        "MediStock.Application.Dashboard",
    ];

    private static IEnumerable<Type> DeclaredDataContracts() =>
        typeof(IUserSession).Assembly
            .GetTypes()
            .Where(t => t.IsInterface && t.IsPublic)
            .Where(t => DataContractNamespaces.Contains(t.Namespace))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    [Fact]
    public void The_two_lists_together_cover_every_data_contract_exactly_once()
    {
        var guarded = CatalogAuthorization.GuardedContracts.ToHashSet();
        var unguarded = CatalogAuthorization.UnguardedContracts.ToHashSet();

        Assert.Empty(guarded.Intersect(unguarded));

        var placed = guarded.Concat(unguarded).ToHashSet();
        var missing = DeclaredDataContracts().Where(t => !placed.Contains(t)).ToList();

        Assert.True(
            missing.Count == 0,
            $"These contracts are in neither GuardedContracts nor UnguardedContracts, so nothing stops them "
            + $"being reached without a permission. Either guard them with AddGuardedCatalog or list them in "
            + $"UnguardedContracts with the reason: {string.Join(", ", missing.Select(t => t.Name))}");

        var stale = placed.Where(t => !DeclaredDataContracts().Contains(t)).ToList();

        Assert.True(
            stale.Count == 0,
            $"These are listed but no longer exist as contracts: {string.Join(", ", stale.Select(t => t.Name))}");
    }

    [Fact]
    public void The_guarded_list_is_the_ones_that_change_records_or_are_the_management_view()
    {
        // Pinned so that moving a contract from one list to the other is a deliberate, reviewed act.
        string[] expected =
        [
            nameof(IMedicineCatalog),
            nameof(ISupplierCatalog),
            nameof(IPurchaseCatalog),
            nameof(ISaleCatalog),
            nameof(ISaleReturnCatalog),
            nameof(ISupplierReturnCatalog),
            nameof(IInventoryOperations),
            nameof(IReportQueries),
            nameof(ICsvExporter),
        ];

        Assert.Equal(
            expected.OrderBy(n => n, StringComparer.Ordinal),
            CatalogAuthorization.GuardedContracts.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void A_contract_nobody_has_decided_about_is_refused_at_registration()
    {
        var services = new ServiceCollection();

        var thrown = Assert.Throws<InvalidOperationException>(
            services.AddGuardedCatalog<IUserSession, UserSession>);

        Assert.Contains(nameof(IUserSession), thrown.Message);
        Assert.Contains("no permission guard", thrown.Message);
    }

    [Fact]
    public void A_guarded_contract_resolves_as_the_guard_and_not_as_the_catalog_itself()
    {
        var provider = Build(isAdministrator: false);

        var resolved = provider.GetRequiredService<ICsvExporter>();

        Assert.IsNotType<RecordingCsvExporter>(resolved);
    }

    [Fact]
    public void A_session_without_the_permission_is_refused_before_the_catalog_is_touched()
    {
        // The guard runs before the catalog is called, so the refusal is synchronous and the exporter beneath
        // it is never entered at all.
        var exporter = new RecordingCsvExporter();
        var provider = Build(isAdministrator: false, exporter);
        var csv = provider.GetRequiredService<ICsvExporter>();

        var thrown = Assert.Throws<PermissionDeniedException>(() =>
        {
            _ = csv.ExportAsync(Table, "out.csv");
        });

        Assert.Contains("export this report", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(Permission.RunReports), thrown.Message);
        Assert.Equal(0, exporter.Exports);
    }

    [Fact]
    public async Task A_session_with_the_permission_reaches_the_catalog_beneath_the_guard()
    {
        var exporter = new RecordingCsvExporter();
        var provider = Build(isAdministrator: true, exporter);

        var path = await provider.GetRequiredService<ICsvExporter>().ExportAsync(Table, "out.csv");

        Assert.Equal(1, exporter.Exports);
        Assert.Equal("out.csv", path);
    }

    [Fact]
    public void Every_guarded_contract_is_a_type_the_guards_table_knows()
    {
        // Guards is private; the public lists are the way in. This asserts that the reflection test above is
        // looking at the same set the registration path uses, by walking all of them through the container.
        foreach (var contract in CatalogAuthorization.GuardedContracts)
        {
            Assert.True(contract.IsInterface, $"{contract.Name} is guarded but is not an interface.");
            Assert.True(contract.IsPublic, $"{contract.Name} is guarded but is not public, so no screen can use it.");
        }
    }

    private static readonly ReportTable Table = ReportTable.Empty("Sales", "Today");

    private static ServiceProvider Build(bool isAdministrator, RecordingCsvExporter? exporter = null)
    {
        exporter ??= new RecordingCsvExporter();

        var session = new UserSession();
        session.Adopt(new UserDto(
            1,
            isAdministrator ? "ravi" : "anita",
            "Somebody",
            isAdministrator ? Role.Administrator : Role.Cashier,
            true,
            DateTimeOffset.UnixEpoch,
            null));

        var services = new ServiceCollection();
        services.AddSingleton<IUserSession>(session);
        services.AddGuardedCatalog<ICsvExporter, RecordingCsvExporter>();
        services.AddScoped(_ => exporter);

        return services.BuildServiceProvider();
    }

    private sealed class RecordingCsvExporter : ICsvExporter
    {
        public int Exports { get; private set; }

        public Task<string> ExportAsync(ReportTable report, string path, CancellationToken cancellationToken = default)
        {
            Exports++;
            return Task.FromResult(path);
        }
    }
}
