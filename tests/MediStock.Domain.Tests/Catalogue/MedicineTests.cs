using MediStock.Domain.Catalogue;
using MediStock.Domain.Common;

namespace MediStock.Domain.Tests.Catalogue;

public class MedicineTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_requires_a_display_name()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => Medicine.Create("   ", DosageForm.Tablet, nowUtc: Now));

        Assert.Contains("Display name is required.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_rejects_an_undefined_dosage_form()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => Medicine.Create("Paracetamol", (DosageForm)1234, nowUtc: Now));

        Assert.Contains("not a supported dosage form", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_rejects_a_negative_reorder_threshold()
        => Assert.Throws<DomainValidationException>(
            () => Medicine.Create("Paracetamol", DosageForm.Tablet, reorderThreshold: -1m, nowUtc: Now));

    [Fact]
    public void Create_starts_active_and_timestamped()
    {
        var medicine = Medicine.Create("Paracetamol", DosageForm.Tablet, brandName: " FeverKare ", nowUtc: Now);

        Assert.True(medicine.IsActive);
        Assert.Equal("FeverKare", medicine.BrandName);
        Assert.Equal(Now, medicine.CreatedAtUtc);
        Assert.Equal(Now, medicine.UpdatedAtUtc);
    }

    [Fact]
    public void Create_keeps_blank_optional_fields_as_null()
    {
        var medicine = Medicine.Create("Paracetamol", DosageForm.Tablet, barcode: "  ", nowUtc: Now);

        Assert.Null(medicine.Barcode);
        Assert.Null(medicine.BrandName);
    }

    [Fact]
    public void Deactivate_keeps_the_record_and_is_idempotent()
    {
        var medicine = Medicine.Create("Paracetamol", nowUtc: Now);
        var later = Now.AddDays(1);

        medicine.Deactivate(later);
        medicine.Deactivate(later.AddDays(1));

        Assert.False(medicine.IsActive);
        // The second call must not move the timestamp; history stays accurate.
        Assert.Equal(later, medicine.UpdatedAtUtc);
        Assert.Equal("Paracetamol", medicine.DisplayName);
    }

    [Fact]
    public void Reactivate_restores_an_inactive_record()
    {
        var medicine = Medicine.Create("Paracetamol", nowUtc: Now);
        medicine.Deactivate(Now);

        medicine.Reactivate(Now.AddDays(1));

        Assert.True(medicine.IsActive);
    }

    [Fact]
    public void Update_replaces_details_and_advances_the_timestamp()
    {
        var medicine = Medicine.Create("Parecetamol", DosageForm.Tablet, nowUtc: Now);

        medicine.Update(
            "Paracetamol", DosageForm.Tablet, "FeverKare", "Paracetamol", "500 mg", null,
            "8900000000011", null, 50m, taxRatePercent: 12m, nowUtc: Now.AddHours(2));

        Assert.Equal("Paracetamol", medicine.DisplayName);
        Assert.Equal("FeverKare", medicine.BrandName);
        Assert.Equal(50m, medicine.ReorderThreshold);
        Assert.Equal(12m, medicine.TaxRatePercent);
        Assert.Equal(Now, medicine.CreatedAtUtc);
        Assert.Equal(Now.AddHours(2), medicine.UpdatedAtUtc);
    }

    [Fact]
    public void Update_validates_before_mutating_the_display_name()
    {
        var medicine = Medicine.Create("Paracetamol", nowUtc: Now);

        Assert.Throws<DomainValidationException>(
            () => medicine.Update(
                "  ", DosageForm.Tablet, null, null, null, null, null, null, null, nowUtc: Now));

        Assert.Equal("Paracetamol", medicine.DisplayName);
    }
}
