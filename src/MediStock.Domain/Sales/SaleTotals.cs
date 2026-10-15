using MediStock.Domain.Common;

namespace MediStock.Domain.Sales;

/// <summary>
/// The six stored money totals of one invoice.
/// </summary>
/// <remarks>
/// Extracted so that the point-of-sale screen's quote and the committed sale cannot compute totals
/// differently: both go through <see cref="From"/>. Each figure is the sum of the already-rounded
/// line values, never a rounding of an unrounded sum, so an invoice's lines always add up to the
/// totals printed below them.
/// </remarks>
public readonly record struct SaleTotals
{
    public decimal SubTotal { get; init; }

    public decimal DiscountTotal { get; init; }

    public decimal TaxableTotal { get; init; }

    public decimal TaxTotal { get; init; }

    public decimal Total { get; init; }

    public decimal CostTotal { get; init; }

    public decimal Margin => Money.Round(Total - TaxTotal - CostTotal);

    public static SaleTotals From(IReadOnlyList<SaleLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return new SaleTotals
        {
            SubTotal = Money.Round(lines.Sum(line => line.GrossAmount)),
            DiscountTotal = Money.Round(lines.Sum(line => line.DiscountAmount)),
            TaxableTotal = Money.Round(lines.Sum(line => line.TaxableAmount)),
            TaxTotal = Money.Round(lines.Sum(line => line.TaxAmount)),
            Total = Money.Round(lines.Sum(line => line.LineTotal)),
            CostTotal = Money.Round(lines.Sum(line => line.LineCost)),
        };
    }
}
