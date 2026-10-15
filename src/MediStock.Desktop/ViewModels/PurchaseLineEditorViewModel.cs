using CommunityToolkit.Mvvm.ComponentModel;
using MediStock.Application.Purchasing;
using MediStock.Domain.Common;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// One editable line on a draft purchase. Every value is round-tripped through
/// <see cref="PurchaseLineDraft"/> so the domain aggregate remains the only validator.
/// </summary>
public sealed partial class PurchaseLineEditorViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string medicineName = string.Empty;

    [ObservableProperty]
    private int medicineId;

    [ObservableProperty]
    private string batchNumber = string.Empty;

    /// <summary>
    /// Supplied by the caller: the purchase editor seeds a new line from the pharmacy's business date and
    /// a loaded line carries the stored date. Left unset it is the minimum date, which the domain reads as
    /// an already-expired batch - a refusal, never a silent sale.
    /// </summary>
    [ObservableProperty]
    private DateOnly expiryDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal))]
    private decimal quantity = 1m;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineTotal))]
    private decimal unitCost;

    [ObservableProperty]
    private decimal sellingPrice;

    /// <summary>Set once the purchase has been received and the line owns a batch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchLink))]
    private int? medicineBatchId;

    public decimal LineTotal => Money.Round(Quantity * UnitCost);

    public string Summary => string.IsNullOrWhiteSpace(MedicineName)
        ? "Unnamed medicine"
        : $"{MedicineName} · batch {BatchNumber}";

    public string BatchLink => MedicineBatchId is { } id ? $"Batch #{id}" : "Not received";

    public static PurchaseLineEditorViewModel FromDraft(PurchaseLineDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new PurchaseLineEditorViewModel
        {
            MedicineId = draft.MedicineId,
            MedicineName = draft.MedicineName,
            BatchNumber = draft.BatchNumber,
            ExpiryDate = draft.ExpiryDate,
            Quantity = draft.Quantity,
            UnitCost = draft.UnitCost,
            SellingPrice = draft.SellingPrice,
        };
    }

    public static PurchaseLineEditorViewModel FromDto(PurchaseLineDto line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new PurchaseLineEditorViewModel
        {
            MedicineId = line.MedicineId,
            MedicineName = line.MedicineName,
            BatchNumber = line.BatchNumber,
            ExpiryDate = line.ExpiryDate,
            Quantity = line.Quantity,
            UnitCost = line.UnitCost,
            SellingPrice = line.SellingPrice,
            MedicineBatchId = line.MedicineBatchId,
        };
    }

    public PurchaseLineDraft ToDraft() => new()
    {
        MedicineId = MedicineId,
        MedicineName = MedicineName,
        BatchNumber = BatchNumber?.Trim() ?? string.Empty,
        ExpiryDate = ExpiryDate,
        Quantity = Quantity,
        UnitCost = UnitCost,
        SellingPrice = SellingPrice,
    };
}
