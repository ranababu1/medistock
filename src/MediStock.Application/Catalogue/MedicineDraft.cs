using MediStock.Domain.Catalogue;
using MediStock.Domain.Common;

namespace MediStock.Application.Catalogue;

/// <summary>
/// User-supplied values for creating or editing a medicine. Validation delegates to the domain
/// entity so the catalogue rules exist in exactly one place.
/// </summary>
public sealed record MedicineDraft
{
    public static MedicineDraft Empty { get; } = new();

    public string DisplayName { get; init; } = string.Empty;

    public string? BrandName { get; init; }

    public string? GenericName { get; init; }

    public string? Strength { get; init; }

    public DosageForm DosageForm { get; init; } = DosageForm.Unknown;

    public string? Manufacturer { get; init; }

    public string? Barcode { get; init; }

    public string? PackDescription { get; init; }

    public decimal? ReorderThreshold { get; init; }

    /// <summary>Rate applied at the point of sale, as a percentage. Zero means untaxed.</summary>
    public decimal TaxRatePercent { get; init; }

    public static MedicineDraft FromDto(MedicineDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new MedicineDraft
        {
            DisplayName = dto.DisplayName,
            BrandName = dto.BrandName,
            GenericName = dto.GenericName,
            Strength = dto.Strength,
            DosageForm = dto.DosageForm,
            Manufacturer = dto.Manufacturer,
            Barcode = dto.Barcode,
            PackDescription = dto.PackDescription,
            ReorderThreshold = dto.ReorderThreshold,
            TaxRatePercent = dto.TaxRatePercent,
        };
    }

    /// <summary>Returns every domain rule violation, so the editor can show them all at once.</summary>
    public IReadOnlyList<string> Validate()
    {
        try
        {
            Medicine.Create(
                DisplayName,
                DosageForm,
                BrandName,
                GenericName,
                Strength,
                Manufacturer,
                Barcode,
                PackDescription,
                ReorderThreshold,
                TaxRatePercent);

            return [];
        }
        catch (DomainValidationException ex)
        {
            return [ex.Message];
        }
    }

    /// <summary>Builds a validated entity. Throws <see cref="DomainValidationException"/> when invalid.</summary>
    public Medicine ToNewMedicine(DateTimeOffset nowUtc) => Medicine.Create(
        DisplayName,
        DosageForm,
        BrandName,
        GenericName,
        Strength,
        Manufacturer,
        Barcode,
        PackDescription,
        ReorderThreshold,
        TaxRatePercent,
        nowUtc);

    public void ApplyTo(Medicine medicine, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(medicine);

        medicine.Update(
            DisplayName,
            DosageForm,
            BrandName,
            GenericName,
            Strength,
            Manufacturer,
            Barcode,
            PackDescription,
            ReorderThreshold,
            TaxRatePercent,
            nowUtc);
    }
}
