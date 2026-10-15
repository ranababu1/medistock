using MediStock.Application.Catalogue;
using MediStock.Domain.Catalogue;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Catalogue;

[Collection(TestDatabaseCollection.Name)]
public class MedicineCatalogTests
{
    private readonly TestDatabaseFixture _fixture;

    public MedicineCatalogTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Create_persists_the_record_and_find_returns_it()
    {
        var name = $"Paracetamol {Token()}";

        var dto = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();

            var created = await catalog.CreateAsync(Draft(name, barcode: Barcode()));
            Assert.True(created.Succeeded, created.Message);

            var found = await catalog.FindAsync(created.Value!.Id);
            Assert.NotNull(found);

            return found;
        });

        Assert.Equal(name, dto.DisplayName);
        Assert.True(dto.IsActive);
        Assert.Equal("FeverKare", dto.BrandName);
        Assert.Equal(50m, dto.ReorderThreshold);
    }

    [Fact]
    public async Task Create_reports_a_duplicate_barcode_as_an_actionable_message()
    {
        var barcode = Barcode();

        var failure = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();

            var first = await catalog.CreateAsync(Draft($"Barcode owner {Token()}", barcode));
            Assert.True(first.Succeeded, first.Message);

            return await catalog.CreateAsync(Draft($"Barcode rival {Token()}", barcode));
        });

        Assert.False(failure.Succeeded);
        Assert.Contains("barcode", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_reports_a_blank_name_as_a_domain_rule_violation()
    {
        var failure = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IMedicineCatalog>().CreateAsync(Draft("   ")));

        Assert.False(failure.Succeeded);
        Assert.Contains("Display name is required.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_matches_every_documented_field()
    {
        var token = Token();
        var barcode = Barcode();

        var results = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();
            var created = await catalog.CreateAsync(Draft($"Searchable {token}", barcode));
            Assert.True(created.Succeeded, created.Message);

            return await catalog.SearchAsync(new MedicineSearchQuery { Term = token });
        });

        Assert.Single(results);
        Assert.Equal($"Searchable {token}", results[0].DisplayName);
    }

    [Fact]
    public async Task Search_can_match_on_the_barcode()
    {
        var barcode = Barcode();

        var results = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();
            var created = await catalog.CreateAsync(Draft($"Scannable {Token()}", barcode));
            Assert.True(created.Succeeded, created.Message);

            return await catalog.SearchAsync(new MedicineSearchQuery { Term = barcode });
        });

        Assert.Single(results);
        Assert.Equal(barcode, results[0].Barcode);
    }

    [Fact]
    public async Task Search_treats_the_operator_input_as_text_and_not_as_a_pattern()
    {
        var results = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IMedicineCatalog>()
                .SearchAsync(new MedicineSearchQuery { Term = "%" }));

        // Without LIKE escaping this would match every row in the catalogue.
        Assert.Empty(results);
    }

    [Fact]
    public async Task Update_changes_the_stored_record()
    {
        var name = $"Editable {Token()}";

        var updated = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();
            var created = await catalog.CreateAsync(Draft(name));
            Assert.True(created.Succeeded, created.Message);

            var revision = Draft(name) with { BrandName = "Renamed brand", Strength = "650 mg" };
            var result = await catalog.UpdateAsync(created.Value!.Id, revision);
            Assert.True(result.Succeeded, result.Message);

            return result.Value;
        });

        Assert.Equal("Renamed brand", updated!.BrandName);
        Assert.Equal("650 mg", updated.Strength);
        Assert.Equal(50m, updated.ReorderThreshold);
    }

    [Fact]
    public async Task Update_reports_a_medicine_that_no_longer_exists()
    {
        var failure = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IMedicineCatalog>().UpdateAsync(999_999, Draft($"Ghost {Token()}")));

        Assert.False(failure.Succeeded);
        Assert.Contains("no longer exists", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deactivating_hides_a_medicine_from_the_catalogue_but_keeps_its_history()
    {
        var name = $"Retirable {Token()}";

        var (defaultSearch, withInactiveSearch, stillFindable) = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();
            var created = await catalog.CreateAsync(Draft(name));
            Assert.True(created.Succeeded, created.Message);

            var deactivated = await catalog.SetActiveAsync(created.Value!.Id, false);
            Assert.True(deactivated.Succeeded, deactivated.Message);

            return (
                await catalog.SearchAsync(new MedicineSearchQuery { Term = name }),
                await catalog.SearchAsync(new MedicineSearchQuery { Term = name, IncludeInactive = true }),
                await catalog.FindAsync(created.Value.Id));
        });

        Assert.Empty(defaultSearch);
        Assert.Single(withInactiveSearch);
        Assert.False(withInactiveSearch[0].IsActive);
        Assert.NotNull(stillFindable);
    }

    [Fact]
    public async Task Reactivating_restores_a_medicine()
    {
        var name = $"Revivable {Token()}";

        var results = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();
            var created = await catalog.CreateAsync(Draft(name));
            await catalog.SetActiveAsync(created.Value!.Id, false);
            await catalog.SetActiveAsync(created.Value.Id, true);

            return await catalog.SearchAsync(new MedicineSearchQuery { Term = name });
        });

        Assert.Single(results);
        Assert.True(results[0].IsActive);
    }

    [Fact]
    public async Task SetActive_reports_a_medicine_that_no_longer_exists()
    {
        var failure = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IMedicineCatalog>().SetActiveAsync(999_999, false));

        Assert.False(failure.Succeeded);
        Assert.Contains("no longer exists", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindPotentialDuplicates_flags_a_shared_barcode()
    {
        var barcode = Barcode();
        var name = $"Original {Token()}";

        var duplicates = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();
            var created = await catalog.CreateAsync(Draft(name, barcode));
            Assert.True(created.Succeeded, created.Message);

            var matches = await catalog.FindPotentialDuplicatesAsync(Draft($"Another brand {Token()}", barcode));
            return (CreatedId: created.Value!.Id, Matches: matches);
        });

        Assert.Contains(duplicates.Matches, m => m.Id == duplicates.CreatedId);
    }

    [Fact]
    public async Task FindPotentialDuplicates_can_exclude_the_record_being_edited()
    {
        var barcode = Barcode();
        var name = $"Self edit {Token()}";

        var duplicates = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();
            var created = await catalog.CreateAsync(Draft(name, barcode));
            Assert.True(created.Succeeded, created.Message);

            var matches = await catalog.FindPotentialDuplicatesAsync(Draft(name, barcode), created.Value!.Id);
            return (CreatedId: created.Value.Id, Matches: matches);
        });

        Assert.DoesNotContain(duplicates.Matches, m => m.Id == duplicates.CreatedId);
    }

    [Fact]
    public async Task FindPotentialDuplicates_ignores_a_draft_with_nothing_to_compare()
    {
        var duplicates = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IMedicineCatalog>().FindPotentialDuplicatesAsync(new MedicineDraft()));

        Assert.Empty(duplicates);
    }

    [Fact]
    public async Task Counting_can_include_or_exclude_inactive_records()
    {
        var before = await _fixture.InScopeAsync(services => CountPair(services));

        var after = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();
            var created = await catalog.CreateAsync(Draft($"Countable {Token()}"));
            Assert.True(created.Succeeded, created.Message);
            await catalog.SetActiveAsync(created.Value!.Id, false);

            return await CountPair(services);
        });

        Assert.Equal(before.Active + 1, after.Total);
        Assert.Equal(before.Active, after.Active);
    }

    [Fact]
    public async Task A_long_display_name_is_never_truncated_silently()
    {
        var name = new string('L', Medicine.DisplayNameMaxLength);

        var stored = await _fixture.InScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<IMedicineCatalog>();
            var created = await catalog.CreateAsync(Draft(name));
            Assert.True(created.Succeeded, created.Message);

            return (await catalog.FindAsync(created.Value!.Id))!.DisplayName;
        });

        Assert.Equal(Medicine.DisplayNameMaxLength, stored.Length);
    }

    [Fact]
    public async Task An_over_long_display_name_is_rejected_by_the_application_layer()
    {
        var failure = await _fixture.InScopeAsync(services =>
            services.GetRequiredService<IMedicineCatalog>()
                .CreateAsync(Draft(new string('L', Medicine.DisplayNameMaxLength + 1))));

        Assert.False(failure.Succeeded);
        Assert.Contains($"{Medicine.DisplayNameMaxLength} characters or fewer", failure.Message, StringComparison.Ordinal);
    }

    private static async Task<(int Total, int Active)> CountPair(IServiceProvider services)
    {
        var catalog = services.GetRequiredService<IMedicineCatalog>();
        return (await catalog.CountAsync(includeInactive: true), await catalog.CountAsync(includeInactive: false));
    }

    private static MedicineDraft Draft(string displayName, string? barcode = null) => new()
    {
        DisplayName = displayName,
        DosageForm = DosageForm.Tablet,
        BrandName = "FeverKare",
        GenericName = "Paracetamol",
        Strength = "500 mg",
        Manufacturer = "Northline Pharma",
        Barcode = barcode,
        PackDescription = "Strip of 10 tablets",
        ReorderThreshold = 50m,
    };

    private static string Token() => Guid.NewGuid().ToString("N")[..8];

    private static string Barcode() => $"890{Random.Shared.Next(1_000_000_000, 1_999_999_999)}";
}
