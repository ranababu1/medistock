using MediStock.Application.Catalogue;
using MediStock.Application.Identity;
using MediStock.Domain.Catalogue;
using MediStock.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Identity;

/// <summary>
/// Reading the audit trail back: filtering it, counting it without the page size getting in the way, and the
/// diagnostics panel that any signed-in operator may open.
/// </summary>
[Collection(TestDatabaseCollection.Name)]
public class AuditTrailTests
{
    private const string Administrator = TestDatabaseFixture.AdministratorUserName;

    private readonly TestDatabaseFixture _fixture;

    public AuditTrailTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_row_written_by_a_catalogue_names_the_account_that_was_signed_in()
    {
        // Regression guard: the catalogues take the actor from the session. Written with the default instead,
        // every catalogue row would name "system" and the trail would not say who changed the price.
        var name = $"Audited medicine {Stamp()}";

        var row = await _fixture.InScopeAsync(async services =>
        {
            var created = await services.GetRequiredService<IMedicineCatalog>().CreateAsync(Draft(name));
            Assert.True(created.Succeeded, created.Message);

            var trail = services.GetRequiredService<IAuditTrail>();

            return (await trail.SearchAsync(new AuditQuery
            {
                Action = AuditAction.MedicineCreated,
                EntityType = nameof(Medicine),
                Term = name,
                Take = 5,
            })).Single(r => r.EntityId == created.Value!.Barcode);
        });

        Assert.Equal(Administrator, row.Actor);
        Assert.NotEqual(AuditEntry.SystemActor, row.Actor);
        Assert.Equal($"Medicine {name} was added to the catalogue.", row.Summary);
    }

    [Fact]
    public async Task Editing_the_tax_rate_is_recorded_as_a_price_change_with_the_reason()
    {
        var name = $"Taxed medicine {Stamp()}";

        var row = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();
            var created = await catalog.CreateAsync(Draft(name) with { TaxRatePercent = 5m });
            Assert.True(created.Succeeded, created.Message);

            var edited = await catalog.UpdateAsync(
                created.Value!.Id,
                MedicineDraft.FromDto(created.Value) with { TaxRatePercent = 12m });

            Assert.True(edited.Succeeded, edited.Message);
            Assert.Equal(12m, edited.Value!.TaxRatePercent);

            return (await services.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery
            {
                Action = AuditAction.PriceChanged,
                Term = name,
                Take = 5,
            })).Single(r => r.EntityId == created.Value.Barcode);
        });

        Assert.Equal(Administrator, row.Actor);
        Assert.Equal($"The tax rate on {name} was changed.", row.Summary);
    }

    [Fact]
    public async Task Editing_something_other_than_the_tax_rate_stays_an_ordinary_edit()
    {
        var name = $"Retitled medicine {Stamp()}";

        var actions = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();
            var created = await catalog.CreateAsync(Draft(name));
            Assert.True(created.Succeeded, created.Message);

            var edited = await catalog.UpdateAsync(
                created.Value!.Id,
                MedicineDraft.FromDto(created.Value) with { Manufacturer = "Another Works" });

            Assert.True(edited.Succeeded, edited.Message);

            var trail = services.GetRequiredService<IAuditTrail>();

            return (
                Updates: (await trail.SearchAsync(new AuditQuery { Action = AuditAction.MedicineUpdated, Term = name, Take = 5 })).Count,
                Prices: (await trail.SearchAsync(new AuditQuery { Action = AuditAction.PriceChanged, Term = name, Take = 5 })).Count);
        });

        Assert.Equal(1, actions.Updates);
        Assert.Equal(0, actions.Prices);
    }

    [Fact]
    public async Task A_term_is_matched_against_the_reason_and_the_detail_as_well_as_the_summary()
    {
        var name = NewName("reasons");

        var found = await _fixture.InScopeAsync(async services =>
        {
            var directory = services.GetRequiredService<IUserDirectory>();

            var created = await directory.CreateAsync(new UserDraft
            {
                UserName = name,
                DisplayName = "Motive Person",
                Role = Role.Cashier,
                Password = "Marigold-Till-92",
                ConfirmPassword = "Marigold-Till-92",
                Reason = "Taken on for the evening shift.",
            });

            Assert.True(created.Succeeded, created.Message);

            await directory.SetRoleAsync(created.Value!.Id, Role.Pharmacist, "Qualified on Tuesday.");

            var trail = services.GetRequiredService<IAuditTrail>();

            return (
                ByReason: await trail.SearchAsync(new AuditQuery { Term = "evening shift", Take = 5 }),
                ByDetail: await trail.SearchAsync(new AuditQuery { Term = "now Pharmacist", Take = 5 }));
        });

        Assert.Contains(found.ByReason, r => r.EntityId == name);
        Assert.Contains(found.ByDetail, r => r.EntityId == name && r.Action == AuditAction.UserRoleChanged);
    }

    [Fact]
    public async Task A_term_containing_a_wildcard_is_matched_literally()
    {
        var token = Stamp();
        var typed = $"100% of the {token}";

        var rows = await _fixture.InScopeAsync(async services =>
        {
            var trail = services.GetRequiredService<IAuditTrail>();

            await trail.RecordAsync(new AuditRecord(AuditAction.BackupTaken, $"A backup was taken for {typed}."));
            await trail.RecordAsync(new AuditRecord(AuditAction.BackupTaken, "A backup was taken for 100 other things."));

            return await trail.SearchAsync(new AuditQuery { Term = "100%", Take = 5 });
        });

        Assert.Contains(rows, r => r.Summary.Contains(token));
        Assert.DoesNotContain(rows, r => r.Summary.Contains("other things"));
    }

    [Fact]
    public async Task The_count_is_of_everything_that_matches_and_ignores_the_page_size()
    {
        var entityId = $"it-page-{Stamp()}";

        var (page, total, pageSizeIgnored) = await _fixture.InScopeAsync(async services =>
        {
            var trail = services.GetRequiredService<IAuditTrail>();

            for (var i = 0; i < 3; i++)
            {
                await trail.RecordAsync(new AuditRecord(AuditAction.RestoreCompleted, $"A restore was completed, attempt {i}.")
                {
                    EntityType = "Database",
                    EntityId = entityId,
                });
            }

            var query = new AuditQuery { EntityType = "Database", Term = entityId, Take = 1 };

            // The list is paged; the count is not, so a screen can say "showing 1 of 3".
            return (
                await trail.SearchAsync(query),
                await trail.CountAsync(query),
                await trail.CountAsync(query with { Take = 500 }));
        });

        Assert.Single(page);
        Assert.Equal(3, total);
        Assert.Equal(total, pageSizeIgnored);
    }

    [Fact]
    public async Task The_newest_rows_are_returned_first_unless_asked_otherwise()
    {
        var entityId = $"it-order-{Stamp()}";

        var (newest, oldest) = await _fixture.InScopeAsync(async services =>
        {
            var trail = services.GetRequiredService<IAuditTrail>();

            for (var i = 0; i < 3; i++)
            {
                await trail.RecordAsync(new AuditRecord(AuditAction.BackupTaken, $"A backup was taken, number {i}.")
                {
                    EntityType = "Database",
                    EntityId = entityId,
                });
            }

            var query = new AuditQuery { EntityType = "Database", Term = entityId, Take = 10 };

            return (await trail.SearchAsync(query), await trail.SearchAsync(query with { NewestFirst = false }));
        });

        Assert.Equal(
            newest.Select(r => r.Id).OrderByDescending(id => id),
            newest.Select(r => r.Id));
        Assert.Equal(
            oldest.Select(r => r.Id).OrderBy(id => id),
            oldest.Select(r => r.Id));
    }

    [Fact]
    public async Task A_range_around_the_moment_a_row_was_written_includes_it_and_one_that_misses_it_does_not()
    {
        var entityId = $"it-range-{Stamp()}";

        // Captured immediately before the row is written, so the window is one the row has to fall inside.
        var before = DateTimeOffset.UtcNow;

        var (inside, outside, exactly) = await _fixture.InScopeAsync(async services =>
        {
            var trail = services.GetRequiredService<IAuditTrail>();

            var row = await trail.RecordAsync(new AuditRecord(AuditAction.BackupTaken, "A backup was taken for the range test.")
            {
                EntityType = "Database",
                EntityId = entityId,
            });

            var query = new AuditQuery { EntityType = "Database", Term = entityId, Take = 10 };

            return (
                await trail.SearchAsync(query with { From = before, To = before.AddMinutes(1) }),
                await trail.SearchAsync(query with { From = row.OccurredAtUtc.AddMinutes(1), To = row.OccurredAtUtc.AddMinutes(2) }),
                await trail.SearchAsync(query with { From = row.OccurredAtUtc, To = row.OccurredAtUtc }));
        });

        Assert.Single(inside);
        Assert.Empty(outside);

        // Both ends of a range are inclusive, so a row sitting exactly on its own moment is inside it.
        Assert.Single(exactly);
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_rejected_rather_than_answered()
    {
        var thrown = await Assert.ThrowsAsync<ArgumentException>(() => _fixture.InScopeAsync(services =>
            services.GetRequiredService<IAuditTrail>().SearchAsync(new AuditQuery
            {
                From = DateTimeOffset.UtcNow,
                To = DateTimeOffset.UtcNow.AddHours(-1),
            })));

        Assert.Equal("The start of the range is after the end of it. (Parameter 'From')", thrown.Message);
    }

    [Fact]
    public async Task A_row_written_while_nobody_is_signed_in_names_the_system_account()
    {
        var entityId = $"it-anonymous-{Stamp()}";

        var row = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                await services.GetRequiredService<IAuthenticationService>().SignOutAsync();

                return await services.GetRequiredService<IAuditTrail>()
                    .RecordAsync(new AuditRecord(AuditAction.BackupTaken, "A backup was taken by nobody in particular.")
                    {
                        EntityType = "Database",
                        EntityId = entityId,
                    });
            }
            finally
            {
                var back = await services.GetRequiredService<IAuthenticationService>()
                    .SignInAsync(Administrator, TestDatabaseFixture.AdministratorPassword);

                Assert.True(back.Succeeded, back.Message);
            }
        });

        Assert.Equal(AuditEntry.SystemActor, row.Actor);
        Assert.Null(row.UserId);
    }

    [Fact]
    public async Task The_diagnostics_panel_is_open_to_any_operator_but_the_main_trail_is_not()
    {
        var (refusal, recent, cashierName) = await _fixture.InScopeAsync(async services =>
        {
            try
            {
                const string password = "Marigold-Till-92";
                var cashier = $"it.cashier.{Stamp()}";

                var created = await services.GetRequiredService<IUserDirectory>().CreateAsync(new UserDraft
                {
                    UserName = cashier,
                    DisplayName = "Counter Cashier",
                    Role = Role.Cashier,
                    Password = password,
                    ConfirmPassword = password,
                });

                Assert.True(created.Succeeded, created.Message);

                var authentication = services.GetRequiredService<IAuthenticationService>();
                Assert.True((await authentication.SignInAsync(cashier, password)).Succeeded);

                var trail = services.GetRequiredService<IAuditTrail>();

                var thrown = await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                    trail.SearchAsync(new AuditQuery { Take = 5 }));

                // The support call needs to see the last few things that happened without being an
                // administrator. The rows are ordinary and carry no credentials.
                var last = await trail.RecentAsync(5);

                var counted = await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                    trail.CountAsync(new AuditQuery()));

                Assert.Contains($"permission: {Permission.ViewAuditTrail}", counted.Message);

                return (thrown.Message, last, cashier);
            }
            finally
            {
                var back = await services.GetRequiredService<IAuthenticationService>()
                    .SignInAsync(Administrator, TestDatabaseFixture.AdministratorPassword);

                Assert.True(back.Succeeded, back.Message);
            }
        });

        Assert.Contains("read the audit trail", refusal);
        Assert.Equal(5, recent.Count);

        // The newest row is the cashier's own sign-in, which is what makes the panel useful on a support call.
        Assert.Equal(cashierName, recent[0].Actor);
        Assert.Equal(AuditAction.SignInSucceeded, recent[0].Action);
    }

    [Fact]
    public async Task Asking_for_more_than_two_hundred_recent_rows_is_capped()
    {
        var rows = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IAuditTrail>().RecentAsync(10_000));

        Assert.True(rows.Count <= 200);
        Assert.Equal(
            rows.Select(r => r.Id).OrderByDescending(id => id),
            rows.Select(r => r.Id));
    }

    private static MedicineDraft Draft(string displayName) => new()
    {
        DisplayName = displayName,
        DosageForm = DosageForm.Tablet,
        BrandName = "FeverKare",
        GenericName = "Paracetamol",
        Strength = "500 mg",
        Manufacturer = "Northline Pharma",
        Barcode = $"890{Random.Shared.Next(1_000_000_000, 1_999_999_999)}",
        PackDescription = "Strip of 10 tablets",
        ReorderThreshold = 50m,
    };

    private static string NewName(string stem) => $"it.{stem}.{Stamp()}";

    private static string Stamp() => Guid.NewGuid().ToString("N")[..8];
}
