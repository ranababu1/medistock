using MediStock.Application.Catalogue;
using MediStock.Application.Common;
using MediStock.Domain.Catalogue;
using MediStock.Domain.Common;

namespace MediStock.Application.Tests.Catalogue;

public class MedicineDraftTests
{
    private static MedicineDraft Valid => new()
    {
        DisplayName = "Paracetamol",
        DosageForm = DosageForm.Tablet,
        BrandName = "FeverKare",
        GenericName = "Paracetamol",
        Strength = "500 mg",
        Barcode = "8900000000011",
        ReorderThreshold = 50m,
    };

    [Fact]
    public void Validate_returns_nothing_for_a_valid_draft()
        => Assert.Empty(Valid.Validate());

    [Fact]
    public void Validate_reports_the_missing_display_name()
    {
        var errors = (Valid with { DisplayName = "  " }).Validate();

        Assert.Single(errors);
        Assert.Contains("Display name is required.", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_reports_a_negative_reorder_threshold()
    {
        var errors = (Valid with { ReorderThreshold = -1m }).Validate();

        Assert.Single(errors);
        Assert.Contains("Reorder threshold", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_is_invalid_so_the_editor_can_start_with_a_message()
        => Assert.NotEmpty(MedicineDraft.Empty.Validate());

    [Fact]
    public void ToNewMedicine_builds_a_validated_entity()
    {
        var medicine = Valid.ToNewMedicine(DateTimeOffset.UnixEpoch);

        Assert.Equal("Paracetamol", medicine.DisplayName);
        Assert.Equal(DosageForm.Tablet, medicine.DosageForm);
        Assert.True(medicine.IsActive);
    }

    [Fact]
    public void ToNewMedicine_throws_for_an_invalid_draft()
        => Assert.Throws<DomainValidationException>(
            () => (Valid with { DisplayName = "" }).ToNewMedicine(DateTimeOffset.UnixEpoch));

    [Fact]
    public void ApplyTo_updates_an_existing_entity()
    {
        var medicine = Medicine.Create("Parecetamol", DosageForm.Unknown, nowUtc: DateTimeOffset.UnixEpoch);

        Valid.ApplyTo(medicine, DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal("Paracetamol", medicine.DisplayName);
        Assert.Equal("8900000000011", medicine.Barcode);
        Assert.Equal(50m, medicine.ReorderThreshold);
    }

    [Fact]
    public void ApplyTo_rejects_a_null_entity()
        => Assert.Throws<ArgumentNullException>(() => Valid.ApplyTo(null!, DateTimeOffset.UnixEpoch));

    [Fact]
    public void FromDto_round_trips_every_editable_field()
    {
        var medicine = Valid.ToNewMedicine(DateTimeOffset.UnixEpoch);
        var dto = new MedicineDto(
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
            medicine.IsActive,
            medicine.CreatedAtUtc,
            medicine.UpdatedAtUtc);

        Assert.Equal(Valid, MedicineDraft.FromDto(dto));
    }
}
