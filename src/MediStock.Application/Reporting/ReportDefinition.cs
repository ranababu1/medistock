namespace MediStock.Application.Reporting;

/// <summary>The reports MediStock can produce.</summary>
/// <remarks>
/// Values are stored explicitly so a saved report choice keeps its meaning if reports are added.
/// </remarks>
public enum ReportKind
{
    /// <summary>Takings, refunds and margin for each business date in a range.</summary>
    DailySales = 1,

    /// <summary>What each medicine sold and earned over a range.</summary>
    SalesByMedicine = 2,

    /// <summary>Takings and refunds grouped by how the money moved.</summary>
    SalesByPaymentMethod = 3,

    /// <summary>What was bought, from whom, and what it cost.</summary>
    PurchaseHistory = 4,

    /// <summary>Every batch still holding stock, with its expiry date.</summary>
    StockByBatch = 5,

    /// <summary>Medicines at or below their reorder threshold.</summary>
    LowStock = 6,

    /// <summary>Stock expiring within a horizon, and stock that already has.</summary>
    ExpiringStock = 7,

    /// <summary>The stock ledger over a range: every receipt, sale, return and write-off.</summary>
    StockMovements = 8,

    /// <summary>What the stock on the shelf cost.</summary>
    InventoryValuation = 9,

    /// <summary>Customer returns and what happened to the goods.</summary>
    SalesReturns = 10,
}

/// <summary>
/// What a report needs before it can run, which also tells the Reports screen which controls to offer.
/// </summary>
public sealed record ReportDefinition(
    ReportKind Kind,
    string Title,
    string Description,
    bool UsesDateRange,
    bool UsesHorizonDays = false,
    bool UsesTake = false);

/// <summary>
/// One run of one report: which report, and the values that describe it.
/// </summary>
public sealed record ReportRequest
{
    /// <summary>Default horizon for expiry and replenishment reports.</summary>
    public const int DefaultHorizonDays = 90;

    /// <summary>Default number of rows for the reports that can be arbitrarily long.</summary>
    public const int DefaultTake = 500;

    public const int MaxTake = 10_000;

    public ReportKind Kind { get; init; }

    /// <summary>First business date included. Null means "as far back as the data goes".</summary>
    public DateOnly? From { get; init; }

    /// <summary>Last business date included. Null means today.</summary>
    public DateOnly? To { get; init; }

    /// <summary>How far ahead to look, for the reports that are about the future rather than the past.</summary>
    public int? HorizonDays { get; init; }

    public int? Take { get; init; }

    /// <summary>
    /// Fills in the defaults and refuses a range that runs backwards. Called before a report is run so
    /// every report sees the same, sane parameters.
    /// </summary>
    public ReportRequest Normalise(DateOnly today)
    {
        var to = To ?? today;
        var from = From ?? DateOnly.MinValue;

        if (from > to)
        {
            throw new ArgumentException(
                $"The range ends on {to:yyyy-MM-dd} but starts on {from:yyyy-MM-dd}.", nameof(From));
        }

        var horizon = HorizonDays ?? DefaultHorizonDays;
        if (horizon < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HorizonDays), horizon, "A horizon cannot be negative.");
        }

        var take = Take ?? DefaultTake;
        if (take <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Take), take, "A report must be allowed at least one row.");
        }

        return this with
        {
            From = from,
            To = to,
            HorizonDays = horizon,
            Take = Math.Min(take, MaxTake),
        };
    }

    /// <summary>The range as it should appear on the report and in the exported file.</summary>
    public string Describe()
    {
        var from = From is null ? "the beginning" : From.Value.ToString("yyyy-MM-dd");
        var to = To is null ? "today" : To.Value.ToString("yyyy-MM-dd");
        return Kind is ReportKind.StockByBatch or ReportKind.InventoryValuation or ReportKind.LowStock
            ? $"Stock as of {to}"
            : $"{from} to {to}";
    }
}

/// <summary>The catalogue of reports, shared by the queries that run them and the screen that offers them.</summary>
public static class ReportDefinitions
{
    /// <summary>In the order the Reports screen lists them.</summary>
    public static IReadOnlyList<ReportDefinition> All { get; } =
    [
        new(ReportKind.DailySales, "Daily sales",
            "Invoices, units, tax, refunds and margin for each business date.", UsesDateRange: true),
        new(ReportKind.SalesByMedicine, "Sales by medicine",
            "Units sold, discount, tax, revenue and margin per medicine.", UsesDateRange: true),
        new(ReportKind.SalesByPaymentMethod, "Sales by payment method",
            "What was taken and refunded in cash, by card, by UPI and otherwise.", UsesDateRange: true),
        new(ReportKind.PurchaseHistory, "Purchase history",
            "Every batch received, with supplier, invoice, cost and quantity.", UsesDateRange: true),
        new(ReportKind.StockByBatch, "Stock by batch",
            "Every batch still holding stock, with its expiry date and value.", UsesDateRange: false),
        new(ReportKind.LowStock, "Low stock",
            "Medicines at or below their reorder threshold, with what to reorder.", UsesDateRange: false),
        new(ReportKind.ExpiringStock, "Expiring stock",
            "Stock expiring within the horizon, and stock that already has.", UsesDateRange: false,
            UsesHorizonDays: true),
        new(ReportKind.StockMovements, "Stock movements",
            "The ledger over the range: receipts, sales, returns, adjustments and write-offs.",
            UsesDateRange: true, UsesTake: true),
        new(ReportKind.InventoryValuation, "Inventory valuation",
            "What the stock on hand cost and what it would sell for, by medicine.", UsesDateRange: false),
        new(ReportKind.SalesReturns, "Sales returns",
            "Customer returns with the refund, the batch and what happened to the goods.",
            UsesDateRange: true),
    ];

    public static ReportDefinition For(ReportKind kind)
        => All.FirstOrDefault(definition => definition.Kind == kind)
           ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown report.");
}
