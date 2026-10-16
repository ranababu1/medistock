using System.Globalization;
using MediStock.Application.Inventory;
using MediStock.Domain.Inventory;
using MediStock.Domain.Sales;

namespace MediStock.Application.Reporting;

/// <summary>
/// The one place that turns a report cell into text, shared by the on-screen table and the CSV file.
/// </summary>
/// <remarks>
/// A report is built from raw values — dates, decimals, enums — so its figures stay comparable as numbers
/// and can be asserted on. Rendering happens here, once, so that what the operator reads on the screen and
/// what they open in a spreadsheet cannot differ. Values are never rounded here: the rounding that matters
/// happened in the domain, and a report must not be the place a paisa goes missing.
/// </remarks>
public static class ReportText
{
    /// <summary>
    /// Money and quantities, rendered with at least two and at most three decimals. Quantities are stored
    /// to three decimals, so a value like 0.125 survives; money is already rounded to two by the domain.
    /// </summary>
    private const string NumberFormat = "0.00#";

    /// <summary>Renders one cell. Null becomes an empty cell rather than the word "null".</summary>
    public static string Format(object? cell) => cell switch
    {
        null => string.Empty,
        string text => text,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        bool flag => flag ? "Yes" : "No",
        decimal number => number.ToString(NumberFormat, CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        Enum word => Describe(word),
        _ => Convert.ToString(cell, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>Words for the operator, rather than the name of the enum member.</summary>
    public static string Describe(Enum value) => value switch
    {
        PaymentMethod.Cash => "Cash",
        PaymentMethod.Card => "Card",
        PaymentMethod.Upi => "UPI",
        PaymentMethod.Other => "Other",

        StockMovementType.Receipt => "Receipt",
        StockMovementType.Sale => "Sale",
        StockMovementType.SalesReturn => "Customer return",
        StockMovementType.AdjustmentIncrease => "Stock increase",
        StockMovementType.AdjustmentDecrease => "Stock decrease",
        StockMovementType.ExpiryWriteOff => "Expiry write-off",
        StockMovementType.SupplierReturn => "Supplier return",
        StockMovementType.RecallWriteOff => "Recall write-off",
        StockMovementType.ReceiptReversal => "Receipt reversal",

        BatchStatus.Active => "Active",
        BatchStatus.Depleted => "Depleted",
        BatchStatus.Expired => "Expired",
        BatchStatus.WrittenOff => "Written off",
        BatchStatus.Recalled => "Recalled",

        ReturnDisposition.Restock => "Restocked",
        ReturnDisposition.Quarantine => "Quarantined",
        ReturnDisposition.Destroy => "Destroyed",

        QuarantineSource.CustomerReturn => "Customer return",
        QuarantineSource.Recall => "Recall",
        QuarantineSource.WriteOff => "Write-off",

        _ => value.ToString(),
    };
}
