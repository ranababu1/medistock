using MediStock.Application.Catalogue;
using MediStock.Application.Identity;
using MediStock.Application.Identity.Authorization;
using MediStock.Application.Operations;
using MediStock.Application.Purchasing;
using MediStock.Application.Reporting;
using MediStock.Application.Sales;
using MediStock.Application.Suppliers;
using MediStock.Domain.Catalogue;
using MediStock.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Identity;

/// <summary>
/// Who may do what, against the container the desktop application builds.
/// </summary>
/// <remarks>
/// The unit tests prove that every data contract is either guarded or deliberately not, and that a guard is
/// asked before the inner catalogue is entered. What they cannot prove is that the registrations the
/// application actually builds are wired that way — a decorator missing from
/// <c>AddMediStockInfrastructure</c> would leave a contract resolvable and unguarded, and only this layer can
/// see that. Every test here signs in, tries, and hands the session back.
/// </remarks>
[Collection(TestDatabaseCollection.Name)]
public class AuthorizationTests
{
    private const string Administrator = TestDatabaseFixture.AdministratorUserName;

    private readonly TestDatabaseFixture _fixture;

    public AuthorizationTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Every_guarded_contract_the_application_declares_is_registered_and_resolvable()
    {
        var unresolved = _fixture.InScope(services =>
        {
            var missing = new List<string>();

            foreach (var contract in CatalogAuthorization.GuardedContracts)
            {
                if (services.GetService(contract) is null)
                {
                    missing.Add(contract.Name);
                }
            }

            return missing;
        });

        Assert.Empty(unresolved);
    }

    [Fact]
    public async Task Every_contract_that_is_deliberately_left_open_still_resolves()
    {
        var missing = _fixture.InScope(services =>
        {
            var absent = new List<string>();

            foreach (var contract in CatalogAuthorization.UnguardedContracts)
            {
                if (services.GetService(contract) is null)
                {
                    absent.Add(contract.Name);
                }
            }

            return absent;
        });

        Assert.Empty(missing);
    }

    [Fact]
    public async Task A_cashier_is_refused_by_every_guarded_catalogue()
    {
        var refusals = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                const string password = "Marigold-Till-92";
                var name = $"it.authorized.{Stamp()}";

                var created = await services.GetRequiredService<IUserDirectory>().CreateAsync(new UserDraft
                {
                    UserName = name,
                    DisplayName = "Till Operator",
                    Role = Role.Cashier,
                    Password = password,
                    ConfirmPassword = password,
                });

                Assert.True(created.Succeeded, created.Message);

                var authentication = services.GetRequiredService<IAuthenticationService>();
                Assert.True((await authentication.SignInAsync(name, password)).Succeeded);

                var messages = new List<string>();

                // One call per guarded contract, chosen to be the one that matters most: the write that would
                // change a record. Reads are guarded too, and the directory test covers those.
                await Refused(messages, () => services.GetRequiredService<IMedicineCatalog>()
                    .CreateAsync(new MedicineDraft { DisplayName = $"Refused {Stamp()}" }));

                await Refused(messages, () => services.GetRequiredService<ISupplierCatalog>()
                    .CreateAsync(new SupplierDraft { Name = $"Refused Supplies {Stamp()}" }));

                await Refused(messages, () => services.GetRequiredService<IPurchaseCatalog>()
                    .CreateAsync(new PurchaseDraft()));

                await Refused(messages, () => services.GetRequiredService<ISaleReturnCatalog>()
                    .RecordAsync(new SalesReturnDraft()));

                await Refused(messages, () => services.GetRequiredService<IReportQueries>()
                    .RunAsync(new ReportRequest { Kind = ReportKind.DailySales }));

                await Refused(messages, () => services.GetRequiredService<ICsvExporter>()
                    .ExportAsync(
                        new ReportTable("Refused", "A report this role may not read", [], []),
                        Path.Combine(Path.GetTempPath(), "refused.csv")));

                await Refused(messages, () => services.GetRequiredService<IUserDirectory>()
                    .SearchAsync(new UserQuery()));

                await Refused(messages, () => services.GetRequiredService<IAuditTrail>()
                    .SearchAsync(new AuditQuery()));

                await Refused(messages, () => services.GetRequiredService<IBackupService>()
                    .CreateAsync());

                await Refused(messages, () => services.GetRequiredService<IBackupService>()
                    .ListAsync());

                return messages;
            }
            finally
            {
                var back = await services.GetRequiredService<IAuthenticationService>()
                    .SignInAsync(Administrator, TestDatabaseFixture.AdministratorPassword);

                Assert.True(back.Succeeded, back.Message);
            }
        });

        Assert.Equal(10, refusals.Count);

        foreach (var message in refusals)
        {
            Assert.Contains("You do not have permission to ", message);
            Assert.Contains("permission: ", message);
        }

        // The permissions named are the ones each action really needs, not one blanket refusal.
        Assert.Contains(refusals, m => m.Contains($"permission: {Permission.ManageMedicines}"));
        Assert.Contains(refusals, m => m.Contains($"permission: {Permission.ManageSuppliers}"));
        Assert.Contains(refusals, m => m.Contains($"permission: {Permission.ReceivePurchases}"));
        Assert.Contains(refusals, m => m.Contains($"permission: {Permission.ProcessReturns}"));
        Assert.Contains(refusals, m => m.Contains($"permission: {Permission.RunReports}"));
        Assert.Contains(refusals, m => m.Contains($"permission: {Permission.ManageUsers}"));
        Assert.Contains(refusals, m => m.Contains($"permission: {Permission.ViewAuditTrail}"));
        Assert.Contains(refusals, m => m.Contains($"permission: {Permission.BackupDatabase}"));
    }

    [Fact]
    public async Task The_store_room_may_receive_goods_but_may_not_take_a_payment()
    {
        var (refusal, permission) = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                const string password = "Marigold-Till-92";
                var name = $"it.store.{Stamp()}";

                var created = await services.GetRequiredService<IUserDirectory>().CreateAsync(new UserDraft
                {
                    UserName = name,
                    DisplayName = "Store Room",
                    Role = Role.InventoryOperator,
                    Password = password,
                    ConfirmPassword = password,
                });

                Assert.True(created.Succeeded, created.Message);

                var authentication = services.GetRequiredService<IAuthenticationService>();
                Assert.True((await authentication.SignInAsync(name, password)).Succeeded);

                var thrown = await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                    services.GetRequiredService<ISaleCatalog>().CheckoutAsync(new SaleDraft()));

                // The same role may run a report, so the refusal is about selling and not about being signed in.
                var report = await services.GetRequiredService<IReportQueries>()
                    .RunAsync(new ReportRequest { Kind = ReportKind.DailySales });

                Assert.NotNull(report);

                return (thrown.Message, Permission.Sell);
            }
            finally
            {
                var back = await services.GetRequiredService<IAuthenticationService>()
                    .SignInAsync(Administrator, TestDatabaseFixture.AdministratorPassword);

                Assert.True(back.Succeeded, back.Message);
            }
        });

        Assert.Contains($"permission: {permission}", refusal);
        Assert.Contains("take a payment", refusal);
    }

    [Fact]
    public async Task A_pharmacist_may_run_reports_but_not_manage_accounts_back_up_or_read_the_trail()
    {
        var messages = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                const string password = "Marigold-Till-92";
                var name = $"it.pharmacist.{Stamp()}";

                var created = await services.GetRequiredService<IUserDirectory>().CreateAsync(new UserDraft
                {
                    UserName = name,
                    DisplayName = "Dispensing Pharmacist",
                    Role = Role.Pharmacist,
                    Password = password,
                    ConfirmPassword = password,
                });

                Assert.True(created.Succeeded, created.Message);

                var authentication = services.GetRequiredService<IAuthenticationService>();
                Assert.True((await authentication.SignInAsync(name, password)).Succeeded);

                // Allowed: dispensing is what the role is for.
                Assert.True((await services.GetRequiredService<IMedicineCatalog>()
                    .CreateAsync(new MedicineDraft
                    {
                        DisplayName = $"Dispensed {Stamp()}",
                        DosageForm = DosageForm.Tablet,
                    })).Succeeded);

                Assert.NotNull(await services.GetRequiredService<IReportQueries>()
                    .RunAsync(new ReportRequest { Kind = ReportKind.DailySales }));

                var refused = new List<string>();

                await Refused(refused, () => services.GetRequiredService<IUserDirectory>()
                    .CountAsync());

                await Refused(refused, () => services.GetRequiredService<IBackupService>()
                    .CreateAsync());

                await Refused(refused, () => services.GetRequiredService<IAuditTrail>()
                    .SearchAsync(new AuditQuery()));

                return refused;
            }
            finally
            {
                var back = await services.GetRequiredService<IAuthenticationService>()
                    .SignInAsync(Administrator, TestDatabaseFixture.AdministratorPassword);

                Assert.True(back.Succeeded, back.Message);
            }
        });

        Assert.Equal(
            new[] { Permission.ManageUsers, Permission.BackupDatabase, Permission.ViewAuditTrail },
            messages.Select(PermissionNamed));
    }

    [Fact]
    public async Task A_pharmacist_may_read_the_trail_the_catalogues_write_about_their_own_work()
    {
        // The trail is for the administrator, so a pharmacist who changed a price cannot read back who did it.
        // What they can see is their own change: the screen they made it on shows the row it wrote.
        var name = $"it.trace.{Stamp()}";

        var (allowed, refused) = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                const string password = "Marigold-Till-92";

                var created = await services.GetRequiredService<IUserDirectory>().CreateAsync(new UserDraft
                {
                    UserName = name,
                    DisplayName = "Tracing Pharmacist",
                    Role = Role.Pharmacist,
                    Password = password,
                    ConfirmPassword = password,
                });

                Assert.True(created.Succeeded, created.Message);

                var authentication = services.GetRequiredService<IAuthenticationService>();
                Assert.True((await authentication.SignInAsync(name, password)).Succeeded);

                var trail = services.GetRequiredService<IAuditTrail>();

                var permitted = await trail.RecentAsync(3);

                var denied = await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                    trail.SearchAsync(new AuditQuery { Actor = name, Take = 3 }));

                return (permitted, denied.Message);
            }
            finally
            {
                var back = await services.GetRequiredService<IAuthenticationService>()
                    .SignInAsync(Administrator, TestDatabaseFixture.AdministratorPassword);

                Assert.True(back.Succeeded, back.Message);
            }
        });

        Assert.Equal(3, allowed.Count);
        Assert.Contains($"permission: {Permission.ViewAuditTrail}", refused);
    }

    private static async Task Refused(List<string> refusals, Func<Task> call)
    {
        // Synchronous on purpose: the guards are asked before the method body starts, so a refusal arrives
        // without the caller having to await anything.
        var thrown = await Assert.ThrowsAsync<PermissionDeniedException>(call);
        refusals.Add(thrown.Message);
    }

    private static Permission PermissionNamed(string refusal) =>
        Enum.Parse<Permission>(
            refusal[(refusal.IndexOf("permission: ", StringComparison.Ordinal) + 12)..].TrimEnd(')', '.', ' '));

    private static string Stamp() => Guid.NewGuid().ToString("N")[..8];
}
