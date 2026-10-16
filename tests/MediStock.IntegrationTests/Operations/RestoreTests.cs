using MediStock.Application.Catalogue;
using MediStock.Application.Identity;
using MediStock.Application.Operations;
using MediStock.Domain.Catalogue;
using MediStock.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Operations;

/// <summary>
/// The restore round trip: a backup is taken, the database moves on, and restoring puts it back exactly as
/// it was — including the audit trail, which is the only thing left to tell afterwards that it happened.
/// </summary>
/// <remarks>
/// <para>
/// This is the acceptance criterion Milestone 5 sets: "a backup can be restored successfully in a test
/// environment". It runs against a database of its own because a restore replaces everything in it, so it
/// cannot share the database the rest of the run is using. The file is written to the configured test
/// folder rather than to a pharmacy's real backup location.
/// </para>
/// <para>
/// Everything here is one test on purpose. Setting an installation up can only be done once, the restore
/// can only be the last thing that happens to the data, and only an administrator may do either, so the
/// steps have an order and cannot be split across tests that xUnit would be free to run in any order.
/// </para>
/// </remarks>
[Collection(TestDatabaseCollection.Name)]
public sealed class RestoreTests : IClassFixture<ScratchRestoreDatabase>
{
    private const string AdministratorUserName = "restore.admin";
    private const string AdministratorDisplayName = "Restore Administrator";
    private const string KeptMedicine = "Amoxicillin 500 mg capsules";
    private const string LostMedicine = "Ibuprofen 400 mg tablets";
    private const string RestoreReason =
        "The second medicine was a mistake and the receipt is being re-entered.";

    private static readonly string AdministratorPassword = string.Concat("Wicket", "-Lantern-", "29");

    private readonly ScratchDatabase _database;

    public RestoreTests(ScratchRestoreDatabase scratch) => _database = scratch.Database;

    [Fact]
    public async Task A_backup_restores_the_database_and_leaves_the_restore_on_the_record()
    {
        // A fresh database has to be set up before anything can be written to it, exactly as a new
        // installation is. This is the same bootstrap the application runs on its first start.
        var setUp = await _database.InScopeAsync(services =>
            services.GetRequiredService<IAuthenticationService>().SetUpFirstAdministratorAsync(
                new UserDraft
                {
                    UserName = AdministratorUserName,
                    DisplayName = AdministratorDisplayName,
                    Password = AdministratorPassword,
                    ConfirmPassword = AdministratorPassword,
                    Reason = "Setting up the scratch database the restore test runs against.",
                }));

        Assert.True(setUp.Succeeded, setUp.Message);

        // One medicine, then a backup of the database that holds exactly that medicine.
        var (kept, backup) = await _database.InScopeAsync(async services =>
        {
            var medicine = await services.GetRequiredService<IMedicineCatalog>().CreateAsync(
                new MedicineDraft
                {
                    DisplayName = KeptMedicine,
                    Strength = "500 mg",
                    DosageForm = DosageForm.Capsule,
                    TaxRatePercent = 12m,
                });

            Assert.True(medicine.Succeeded, medicine.Message);

            var taken = await services.GetRequiredService<IBackupService>().CreateAsync(CancellationToken.None);

            Assert.True(taken.Succeeded, taken.Message);

            return (medicine.Value!, taken.Value!);
        });

        Assert.True(File.Exists(backup.Path), $"Taking a backup should leave a file on disk: {backup.Path}");
        Assert.True(backup.SizeBytes > 8 * 1024, "A database backup should not be nearly empty.");

        // It went to the folder the tests configured, not to the one a pharmacy uses for real backups.
        Assert.StartsWith(TestDatabaseFixture.BackupFolder + Path.DirectorySeparatorChar, backup.Path, StringComparison.Ordinal);

        // The database moves on: a second medicine, added by mistake, which the restore should take away.
        var added = await _database.InScopeAsync(async services =>
        {
            var catalogue = services.GetRequiredService<IMedicineCatalog>();

            var second = await catalogue.CreateAsync(new MedicineDraft
            {
                DisplayName = LostMedicine,
                Strength = "400 mg",
                DosageForm = DosageForm.Tablet,
                TaxRatePercent = 12m,
            });

            Assert.True(second.Succeeded, second.Message);

            return await catalogue.SearchAsync(new MedicineSearchQuery(), CancellationToken.None);
        });

        Assert.Equal(2, added.Count);

        var verify = await _database.InScopeAsync(services =>
            services.GetRequiredService<IBackupService>().VerifyAsync(backup.Path, CancellationToken.None));

        Assert.True(verify.Succeeded, verify.Message);

        var restore = await _database.InScopeAsync(services =>
            services.GetRequiredService<IBackupService>()
                .RestoreAsync(backup.Path, RestoreReason, CancellationToken.None));

        Assert.True(restore.Succeeded, restore.Message);

        // The same provider is used for the reads below rather than a fresh one, because a pharmacy restores
        // the database the application is already running against and then carries on with it.
        var (after, trail) = await _database.InScopeAsync(async services =>
        {
            var all = await services.GetRequiredService<IMedicineCatalog>()
                .SearchAsync(new MedicineSearchQuery(), CancellationToken.None);

            var rows = await services.GetRequiredService<IAuditTrail>().RecentAsync(100, CancellationToken.None);

            return (all, rows);
        });

        var survivor = Assert.Single(after);

        Assert.Equal(kept.Id, survivor.Id);
        Assert.Equal(KeptMedicine, survivor.DisplayName);
        Assert.Equal("500 mg", survivor.Strength);

        // The trail was rolled back with everything else, so the mistaken entry is not merely hidden: the row
        // that recorded it went back in time with it, and the restore is the newest thing on the record.
        Assert.DoesNotContain(trail, row => row.Summary.Contains(LostMedicine, StringComparison.Ordinal));

        var restoreRow = trail[0];

        Assert.Equal(AuditAction.RestoreCompleted, restoreRow.Action);
        Assert.Equal(AdministratorUserName, restoreRow.Actor);
        Assert.Equal(RestoreReason, restoreRow.Reason);
        Assert.Equal(backup.FileName, restoreRow.EntityId);
        Assert.Equal(backup.Path, restoreRow.Detail);
        Assert.Contains(backup.FileName, restoreRow.Summary, StringComparison.Ordinal);
        Assert.Contains(AdministratorDisplayName, restoreRow.Summary, StringComparison.Ordinal);

        // The account that restored the database is still there afterwards, which is what makes the two-step
        // "restore, then restart" safe: a restore is not allowed to take the sign-in with it.
        var accounts = await _database.InScopeAsync(services =>
            services.GetRequiredService<IUserDirectory>().SearchAsync(new UserQuery(), CancellationToken.None));

        var administrator = Assert.Single(accounts);

        Assert.Equal(AdministratorUserName, administrator.UserName);
        Assert.True(administrator.IsActive);
        Assert.Equal(Role.Administrator, administrator.Role);
    }
}
