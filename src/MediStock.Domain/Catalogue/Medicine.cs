using MediStock.Domain.Common;

namespace MediStock.Domain.Catalogue;

/// <summary>
/// Catalogue-level information about a medicine. Physical stock is never held here; it lives
/// on <see cref="Inventory.MedicineBatch"/> records so that batch number and expiry are tracked.
/// </summary>
public class Medicine
{
    public const int DisplayNameMaxLength = 200;
    public const int ShortTextMaxLength = 200;
    public const int BarcodeMaxLength = 64;
    public const int PackDescriptionMaxLength = 100;

    /// <summary>
    /// Highest tax rate the catalogue accepts, expressed as a percentage. Rates above this are far
    /// more likely to be a typing error than a real tax slab.
    /// </summary>
    public const decimal MaxTaxRatePercent = 100m;

    /// <summary>Required by Entity Framework Core.</summary>
    private Medicine()
    {
    }

    private Medicine(string displayName)
    {
        DisplayName = displayName;
    }

    public int Id { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string? BrandName { get; private set; }

    public string? GenericName { get; private set; }

    public string? Strength { get; private set; }

    public DosageForm DosageForm { get; private set; } = DosageForm.Unknown;

    public string? Manufacturer { get; private set; }

    /// <summary>Primary scannable code. Additional codes are a later milestone.</summary>
    public string? Barcode { get; private set; }

    public string? PackDescription { get; private set; }

    /// <summary>Total quantity across batches below which the item is reported as low stock.</summary>
    public decimal? ReorderThreshold { get; private set; }

    /// <summary>
    /// Tax rate applied to this medicine at the point of sale, as a percentage. Held here because tax
    /// follows the product, not the batch. The rate in force is copied onto the sale line, so changing
    /// it here never alters an invoice that has already been issued.
    /// </summary>
    public decimal TaxRatePercent { get; private set; }

    /// <summary>Inactive medicines stay in the catalogue so historical documents remain readable.</summary>
    public bool IsActive { get; private set; } = true;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static Medicine Create(
        string displayName,
        DosageForm dosageForm = DosageForm.Unknown,
        string? brandName = null,
        string? genericName = null,
        string? strength = null,
        string? manufacturer = null,
        string? barcode = null,
        string? packDescription = null,
        decimal? reorderThreshold = null,
        decimal taxRatePercent = 0m,
        DateTimeOffset? nowUtc = null)
    {
        var timestamp = nowUtc ?? DateTimeOffset.UtcNow;
        var medicine = new Medicine(Guard.Required(displayName, DisplayNameMaxLength, "Display name"))
        {
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
            IsActive = true,
        };

        medicine.ApplyDetails(
            dosageForm, brandName, genericName, strength, manufacturer, barcode, packDescription,
            reorderThreshold, taxRatePercent);

        return medicine;
    }

    public void Update(
        string displayName,
        DosageForm dosageForm,
        string? brandName,
        string? genericName,
        string? strength,
        string? manufacturer,
        string? barcode,
        string? packDescription,
        decimal? reorderThreshold,
        decimal taxRatePercent = 0m,
        DateTimeOffset? nowUtc = null)
    {
        DisplayName = Guard.Required(displayName, DisplayNameMaxLength, "Display name");
        ApplyDetails(
            dosageForm, brandName, genericName, strength, manufacturer, barcode, packDescription,
            reorderThreshold, taxRatePercent);
        UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
    }

    public void Deactivate(DateTimeOffset? nowUtc = null)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
    }

    public void Reactivate(DateTimeOffset? nowUtc = null)
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
    }

    private void ApplyDetails(
        DosageForm dosageForm,
        string? brandName,
        string? genericName,
        string? strength,
        string? manufacturer,
        string? barcode,
        string? packDescription,
        decimal? reorderThreshold,
        decimal taxRatePercent)
    {
        if (!Enum.IsDefined(dosageForm))
        {
            throw new DomainValidationException($"'{dosageForm}' is not a supported dosage form.");
        }

        if (taxRatePercent < 0m || taxRatePercent > MaxTaxRatePercent)
        {
            throw new DomainValidationException(
                $"Tax rate must be between 0 and {MaxTaxRatePercent:0.##} percent; {taxRatePercent:0.##} was given.");
        }

        DosageForm = dosageForm;
        BrandName = Guard.Optional(brandName, ShortTextMaxLength, "Brand name");
        GenericName = Guard.Optional(genericName, ShortTextMaxLength, "Generic name");
        Strength = Guard.Optional(strength, ShortTextMaxLength, "Strength");
        Manufacturer = Guard.Optional(manufacturer, ShortTextMaxLength, "Manufacturer");
        Barcode = Guard.Optional(barcode, BarcodeMaxLength, "Barcode");
        PackDescription = Guard.Optional(packDescription, PackDescriptionMaxLength, "Pack description");
        ReorderThreshold = Guard.NonNegative(reorderThreshold, "Reorder threshold");
        TaxRatePercent = Money.Round(taxRatePercent);
    }
}
