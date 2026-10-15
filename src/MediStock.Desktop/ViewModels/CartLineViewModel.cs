using CommunityToolkit.Mvvm.ComponentModel;
using MediStock.Application.Catalogue;
using MediStock.Application.Sales;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// One row of the point-of-sale basket. The operator may override the quantity, the unit price and the
/// discount; leaving the price blank means "charge whatever the batch says", which is what the
/// scan-a-product path does.
/// </summary>
public sealed partial class CartLineViewModel : ObservableObject
{
    public CartLineViewModel(MedicineDto medicine)
    {
        ArgumentNullException.ThrowIfNull(medicine);

        MedicineId = medicine.Id;
        MedicineName = medicine.DisplayName;
        Summary = medicine.Summary;
        TaxRatePercent = medicine.TaxRatePercent;
    }

    public int MedicineId { get; }

    public string MedicineName { get; }

    /// <summary>Strength and dosage form, shown under the name so look-alike products stay distinguishable.</summary>
    public string Summary { get; }

    public decimal TaxRatePercent { get; }

    [ObservableProperty]
    private decimal quantity = 1m;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriceIsDefaulted))]
    private decimal? unitPrice;

    [ObservableProperty]
    private decimal discountAmount;

    [ObservableProperty]
    private int? preferredBatchId;

    /// <summary>True while no price has been typed, so the UI can show the price that will be used.</summary>
    public bool PriceIsDefaulted => UnitPrice is null;

    /// <summary>What this line would cost at the price last quoted, before tax.</summary>
    [ObservableProperty]
    private decimal quotedLineTotal;

    [ObservableProperty]
    private string? quotedAllocation;

    [ObservableProperty]
    private string? quotedProblem;

    public SaleLineDraft ToDraft() => new()
    {
        MedicineId = MedicineId,
        Quantity = Quantity,
        UnitPrice = UnitPrice,
        DiscountAmount = DiscountAmount,
        TaxRatePercent = TaxRatePercent,
        PreferredBatchId = PreferredBatchId,
    };
}
