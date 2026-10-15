using MediStock.Domain.Catalogue;

namespace MediStock.Application.Catalogue;

/// <summary>Read model for a catalogue record, safe to hand to the UI.</summary>
public sealed record MedicineDto(
    int Id,
    string DisplayName,
    string? BrandName,
    string? GenericName,
    string? Strength,
    DosageForm DosageForm,
    string? Manufacturer,
    string? Barcode,
    string? PackDescription,
    decimal? ReorderThreshold,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>Compact label used in pickers, for example "Paracetamol 500 mg (Tablet)".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Strength))
            {
                parts.Add(Strength);
            }

            if (DosageForm != DosageForm.Unknown)
            {
                parts.Add(DosageForm.ToString());
            }

            return parts.Count == 0 ? DisplayName : $"{DisplayName} ({string.Join(", ", parts)})";
        }
    }
}
