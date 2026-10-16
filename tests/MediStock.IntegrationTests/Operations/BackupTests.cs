using MediStock.Application.Common;
using MediStock.Application.Identity;
using MediStock.Application.Operations;
using MediStock.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Operations;

/// <summary>
/// Taking a backup, checking one, and the refusals around restoring one.
/// </summary>
/// <remarks>
/// These run against the shared test database and write real backup files into the folder the fixture
/// configures, which it deletes when the run finishes. The restore itself is not exercised here: replacing
/// the database would destroy the fixture every other test is using, so the one round trip lives in
/// <c>RestoreTests</c> against a scratch database of its own.
/// </remarks>
[Collection(TestDatabaseCollection.Name)]
public class BackupTests
{
    private readonly TestDatabaseFixture _fixture;

    public BackupTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Backups_are_written_to_the_folder_the_configuration_names()
    {
        var folder = _fixture.InScope(services => services.GetRequiredService<IBackupService>().Folder);

        Assert.Equal(TestDatabaseFixture.BackupFolder, folder);
    }

    [Fact]
    public async Task Taking_a_backup_writes_a_readable_file_and_records_it()
    {
        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var backups = services.GetRequiredService<IBackupService>();
            var taken = await backups.CreateAsync();

            Assert.True(taken.Succeeded, taken.Message);

            var file = taken.Value!;

            Assert.True(File.Exists(file.Path), $"{file.Path} was reported as written but is not on disk.");
            Assert.Equal(file.Path, Path.Combine(backups.Folder, file.FileName));

            // Named for the moment it was taken, so two backups on the same day do not overwrite each other.
            Assert.StartsWith("MediStock-", file.FileName);
            Assert.EndsWith(".bak", file.FileName);
            Assert.Matches(@"^MediStock-\d{8}-\d{6}\.bak$", file.FileName);

            // A real backup of this database is far bigger than a header, so a few hundred bytes would mean
            // SQL Server wrote a stub.
            Assert.True(file.SizeBytes > 8 * 1024, $"The backup is only {file.SizeBytes} bytes.");

            var trail = services.GetRequiredService<IAuditTrail>();

            return (
                file,
                Rows: await trail.SearchAsync(new AuditQuery { EntityType = "Backup", Take = 20 }));
        });

        var row = outcome.Rows.FirstOrDefault(candidate => candidate.EntityId == outcome.file.FileName);

        Assert.NotNull(row);
        Assert.Equal(AuditAction.BackupTaken, row.Action);
        Assert.Equal(TestDatabaseFixture.AdministratorUserName, row.Actor);
        Assert.Contains($"A backup of MediStock_Test was written to {outcome.file.FileName}.", row.Summary);

        // The full path goes in the detail, which is what makes a backup findable months later.
        Assert.Equal(outcome.file.Path, row.Detail);
    }

    [Fact]
    public async Task The_list_names_every_backup_in_the_folder_newest_first()
    {
        var listing = await _fixture.InScopeAsync(async services =>
        {
            var backups = services.GetRequiredService<IBackupService>();

            // Two, a moment apart, so that ordering has something to order.
            var first = await backups.CreateAsync();
            Assert.True(first.Succeeded, first.Message);

            await Task.Delay(TimeSpan.FromMilliseconds(1100));

            var second = await backups.CreateAsync();
            Assert.True(second.Succeeded, second.Message);

            return (First: first.Value!, Second: second.Value!, Listed: await backups.ListAsync());
        });

        Assert.Equal(
            listing.Listed.Select(file => file.WrittenAtUtc).OrderByDescending(written => written),
            listing.Listed.Select(file => file.WrittenAtUtc));

        Assert.Contains(listing.Listed, file => file.FileName == listing.First.FileName);
        Assert.Contains(listing.Listed, file => file.FileName == listing.Second.FileName);

        // The one taken later is above the one taken earlier, whatever else the folder holds.
        var newest = listing.Listed.ToList().FindIndex(file => file.FileName == listing.Second.FileName);
        var older = listing.Listed.ToList().FindIndex(file => file.FileName == listing.First.FileName);

        Assert.True(newest < older, "A backup taken later was listed below one taken earlier.");
    }

    [Fact]
    public async Task A_backup_that_SQL_Server_can_read_verifies_and_one_it_cannot_is_refused()
    {
        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var backups = services.GetRequiredService<IBackupService>();
            var taken = await backups.CreateAsync();

            Assert.True(taken.Succeeded, taken.Message);

            var verified = await backups.VerifyAsync(taken.Value!.Path);

            // Something that is a .bak by name only, which is what a truncated copy looks like.
            var impostor = Path.Combine(backups.Folder, $"MediStock-19700101-000000.bak");
            await File.WriteAllTextAsync(impostor, "This is not a backup.");

            var rejected = await backups.VerifyAsync(impostor);

            File.Delete(impostor);

            return (Verified: verified, Rejected: rejected, Impostor: Path.GetFileName(impostor));
        });

        Assert.True(outcome.Verified.Succeeded, outcome.Verified.Message);
        Assert.False(outcome.Rejected.Succeeded);
        Assert.Contains(
            $"SQL Server could not read {outcome.Impostor} as a backup. The file is damaged or is not a MediStock backup.",
            outcome.Rejected.Errors);
    }

    [Fact]
    public async Task A_file_that_is_not_there_is_refused_rather_than_throwing()
    {
        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var backups = services.GetRequiredService<IBackupService>();
            var missing = Path.Combine(backups.Folder, "MediStock-19991231-235959.bak");

            Assert.False(File.Exists(missing));

            return (
                Verified: await backups.VerifyAsync(missing),
                Restored: await backups.RestoreAsync(missing, "Checking that a missing file is refused."));
        });

        Assert.False(outcome.Verified.Succeeded);
        Assert.Contains(outcome.Verified.Errors, error => error.Contains("There is no file at "));

        Assert.False(outcome.Restored.Succeeded);
        Assert.Contains(outcome.Restored.Errors, error => error.Contains("There is no file at "));
    }

    [Fact]
    public async Task Restoring_without_saying_why_is_refused()
    {
        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var backups = services.GetRequiredService<IBackupService>();
            var taken = await backups.CreateAsync();

            Assert.True(taken.Succeeded, taken.Message);

            return new
            {
                Blank = await backups.RestoreAsync(taken.Value!.Path, "   "),
                File = taken.Value,
            };
        });

        Assert.False(outcome.Blank.Succeeded);
        Assert.Contains(
            "A reason is required. The trail records why a restore was done, so that the next person can tell a"
            + " deliberate rollback from a mistake.",
            outcome.Blank.Errors);

        // Nothing was restored, so the file is still there to restore from another day.
        Assert.True(File.Exists(outcome.File.Path));
    }

    [Fact]
    public async Task A_role_without_the_database_permission_cannot_back_up_check_or_restore()
    {
        var refusals = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                const string password = "Marigold-Till-92";
                var name = $"it.backup.{Guid.NewGuid():N}"[..22];

                var created = await services.GetRequiredService<IUserDirectory>().CreateAsync(new UserDraft
                {
                    UserName = name,
                    DisplayName = "Backup Refused",
                    Role = Role.Pharmacist,
                    Password = password,
                    ConfirmPassword = password,
                });

                Assert.True(created.Succeeded, created.Message);

                var authentication = services.GetRequiredService<IAuthenticationService>();
                Assert.True((await authentication.SignInAsync(name, password)).Succeeded);

                var backups = services.GetRequiredService<IBackupService>();
                var messages = new List<string>();

                messages.Add(await Refused(() => backups.CreateAsync()));
                messages.Add(await Refused(() => backups.ListAsync()));
                messages.Add(await Refused(() => backups.VerifyAsync(Path.Combine(backups.Folder, "any.bak"))));
                messages.Add(await Refused(() => backups.RestoreAsync(
                    Path.Combine(backups.Folder, "any.bak"),
                    "A pharmacist should not get this far.")));

                return messages;
            }
            finally
            {
                var back = await services.GetRequiredService<IAuthenticationService>()
                    .SignInAsync(
                        TestDatabaseFixture.AdministratorUserName,
                        TestDatabaseFixture.AdministratorPassword);

                Assert.True(back.Succeeded, back.Message);
            }
        });

        Assert.Equal(4, refusals.Count);

        foreach (var message in refusals)
        {
            Assert.Contains($"An administrator can grant it (permission: {Permission.BackupDatabase}).", message);
        }

        // The refusal names what the role tried to do, so the note on the Backup tab says something useful.
        Assert.Contains(refusals, m => m.Contains("take a backup of the database"));
        Assert.Contains(refusals, m => m.Contains("list the backup files"));
        Assert.Contains(refusals, m => m.Contains("check a backup file"));
        Assert.Contains(refusals, m => m.Contains("restore the database from a backup"));
    }

    private static async Task<string> Refused(Func<Task> call)
        => (await Assert.ThrowsAsync<PermissionDeniedException>(call)).Message;
}
