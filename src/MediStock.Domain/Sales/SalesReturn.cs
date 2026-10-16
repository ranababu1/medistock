using MediStock.Domain.Common;
using MediStock.Domain.Inventory;

namespace MediStock.Domain.Sales;

/// <summary>
/// A customer return: how much of which invoice line came back, which batch it went back into, what was
/// refunded, and the operator's stated reason.
/// </summary>
/// <remarks>
/// <para>
/// A return is the only way stock and money go back the other way. Nothing on a completed invoice is
/// ever edited or deleted, so a mistake is corrected by adding a return that references it — the
/// history stays whole and the refund is traceable to the line that charged it.
/// </para>
/// <para>
/// Three rules are enforced here rather than in the screen:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Nothing can come back that did not go out. Every requested quantity is checked against the line it
/// credits and against the specific batch that line sold, including quantities already returned by
/// earlier returns.
/// </description></item>
/// <item><description>
/// A refunded line adds back up. The entry that clears an invoice line takes the line's exact
/// remainder, so a fully returned line refunds precisely what it charged, never a paisa more or less.
/// </description></item>
/// <item><description>
/// Restocking is an explicit decision. Only <see cref="ReturnDisposition.Restock"/> lines move a batch
/// balance; <see cref="ReturnDisposition.Quarantine"/> and <see cref="ReturnDisposition.Destroy"/>
/// record the quantity and leave the stock ledger untouched.
/// </description></item>
/// </list>
/// <para>
/// See <c>docs/decisions/0008-sales-returns-and-stock-disposition.md</c>.
/// </para>
/// </remarks>
public class SalesReturn
{
    public const int ReturnNumberMaxLength = 32;
    public const int ReasonMaxLength = 300;
    public const int NotesMaxLength = 500;
    public const int OperatorMaxLength = 150;

    /// <summary>Required by Entity Framework Core.</summary>
    private SalesReturn()
    {
    }

    public int Id { get; private set; }

    /// <summary>Return document number, unique across the database and never reused.</summary>
    public string ReturnNumber { get; private set; } = string.Empty;

    /// <summary>Local business date the refund counts towards in daily reports.</summary>
    public DateOnly BusinessDate { get; private set; }

    public DateTimeOffset ReturnedAtUtc { get; private set; }

    /// <summary>Operator who took the return, until named users arrive in Milestone 5.</summary>
    public string ReturnedBy { get; private set; } = string.Empty;

    public int SaleId { get; private set; }

    public Sale Sale { get; private set; } = null!;

    /// <summary>Why the customer brought the medicine back. Required, and shown on the return document.</summary>
    public string Reason { get; private set; } = string.Empty;

    public string? Notes { get; private set; }

    /// <summary>
    /// How the money was handed back. Recorded because a refund paid out in cash reduces the day's cash
    /// takings, and reporting that ignored it would not reconcile with the till.
    /// </summary>
    public PaymentMethod RefundMethod { get; private set; } = PaymentMethod.Cash;

    /// <summary>Money handed back across all lines. Equals the sum of the line refunds.</summary>
    public decimal RefundTotal { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public ICollection<SalesReturnLine> Lines { get; private set; } = new List<SalesReturnLine>();

    public decimal ReturnedQuantity => Lines.Sum(l => l.Quantity);

    /// <summary>How much of the return went back onto the shelf.</summary>
    public decimal RestockedQuantity =>
        Lines.Where(l => l.Disposition == ReturnDisposition.Restock).Sum(l => l.Quantity);

    /// <summary>
    /// Records the return against a completed invoice and applies it: line quantities and refunds are
    /// updated and restocked quantities are put back into their batches.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The request names a line or batch the invoice did not sell, asks for more than is left to
    /// return, or repeats a quantity that has already come back.
    /// </exception>
    public static SalesReturn Record(
        string returnNumber,
        DateOnly businessDate,
        string returnedBy,
        Sale sale,
        string reason,
        IReadOnlyList<SalesReturnLineRequest> lines,
        IReadOnlyDictionary<int, MedicineBatch> batches,
        string? notes = null,
        PaymentMethod refundMethod = PaymentMethod.Cash,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(batches);

        if (lines.Count == 0)
        {
            throw new DomainValidationException("A return must return at least one quantity.");
        }

        if (sale.Status == SaleStatus.Cancelled)
        {
            throw new DomainValidationException(
                $"Invoice {sale.InvoiceNumber} was cancelled, so it cannot take a return.");
        }

        if (businessDate < sale.BusinessDate)
        {
            throw new DomainValidationException(
                $"A return cannot be dated {businessDate:yyyy-MM-dd}, before the invoice's business date of {sale.BusinessDate:yyyy-MM-dd}.");
        }

        if (!Enum.IsDefined(refundMethod))
        {
            throw new DomainValidationException($"'{refundMethod}' is not a valid way to pay a refund back.");
        }

        var timestamp = nowUtc ?? DateTimeOffset.UtcNow;

        var salesReturn = new SalesReturn
        {
            ReturnNumber = Guard.Required(returnNumber, ReturnNumberMaxLength, "Return number"),
            BusinessDate = businessDate,
            ReturnedAtUtc = timestamp,
            ReturnedBy = Guard.Required(returnedBy, OperatorMaxLength, "Operator"),
            SaleId = sale.Id,
            Sale = sale,
            Reason = Guard.Required(reason, ReasonMaxLength, "Reason"),
            Notes = Guard.Optional(notes, NotesMaxLength, "Return notes"),
            RefundMethod = refundMethod,
            CreatedAtUtc = timestamp,
        };

        // Pass one: reject the whole request before anything moves. Two bad apples in one basket must
        // not leave half a return applied to the invoice, because a partly applied return cannot be
        // reconstructed by the operator afterwards.
        var planned = new List<PlannedLine>(lines.Count);
        var perLine = new Dictionary<SaleLine, decimal>();
        var perAllocation = new Dictionary<SaleLineBatchAllocation, decimal>();

        foreach (var request in lines)
        {
            var saleLine = sale.Lines.FirstOrDefault(line => line.LineNumber == request.LineNumber)
                ?? throw new DomainValidationException(
                    $"Invoice {sale.InvoiceNumber} has no line {request.LineNumber} to return.");

            var allocation = saleLine.Allocations
                .FirstOrDefault(candidate => candidate.MedicineBatchId == request.MedicineBatchId)
                ?? throw new DomainValidationException(
                    $"Line {saleLine.LineNumber} of invoice {sale.InvoiceNumber} did not sell batch {request.MedicineBatchId}, so nothing can be returned from it.");

            if (!batches.ContainsKey(request.MedicineBatchId))
            {
                throw new DomainValidationException(
                    $"Batch {request.MedicineBatchId} could not be loaded, so the return cannot be costed.");
            }

            var quantity = Guard.Positive(request.Quantity, "Returned quantity");

            var askedOnThisLine = perLine.GetValueOrDefault(saleLine) + quantity;
            if (askedOnThisLine > saleLine.ReturnableQuantity)
            {
                throw new DomainValidationException(
                    $"Line {saleLine.LineNumber} ('{saleLine.MedicineName}') sold {saleLine.Quantity:0.###} and {saleLine.ReturnedQuantity:0.###} has already come back, so at most {saleLine.ReturnableQuantity:0.###} can be returned; this return asks for {askedOnThisLine:0.###}.");
            }

            var askedOnThisBatch = perAllocation.GetValueOrDefault(allocation) + quantity;
            if (askedOnThisBatch > allocation.ReturnableQuantity)
            {
                throw new DomainValidationException(
                    $"Batch '{allocation.BatchNumber}' accounted for {allocation.Quantity:0.###} of line {saleLine.LineNumber} and {allocation.ReturnedQuantity:0.###} has already come back, so at most {allocation.ReturnableQuantity:0.###} can be returned from it; this return asks for {askedOnThisBatch:0.###}.");
            }

            perLine[saleLine] = askedOnThisLine;
            perAllocation[allocation] = askedOnThisBatch;

            var batch = batches[request.MedicineBatchId];

            if (request.Disposition == ReturnDisposition.Restock && !batch.CanBeRestocked(businessDate))
            {
                throw new DomainValidationException(
                    $"Batch '{batch.BatchNumber}' cannot be restocked: {batch.RestockBlocker(businessDate)}. Quarantine or destroy the returned medicine instead.");
            }

            planned.Add(new PlannedLine(saleLine, allocation, quantity, request.Disposition, request.Notes));
        }

        // Pass two: apply. Each money figure charged on the line is split across the returned quantity,
        // and the entry that clears the line receives the exact remainder. That keeps a fully returned
        // line refunding precisely what it charged — refund, tax and cost alike — with no drifting paisa.
        // Cost is split from the line's stored cost rather than read from the batch, so the credit and
        // the charge it reverses are the same number and the reports net out exactly.
        var lineNumber = 1;
        foreach (var plan in planned)
        {
            var saleLine = plan.SaleLine;

            var refund = ShareWithin(saleLine, plan.Quantity, saleLine.LineTotal, saleLine.ReturnedAmount);
            var tax = ShareWithin(saleLine, plan.Quantity, saleLine.TaxAmount, saleLine.ReturnedTax);
            var cost = ShareWithin(saleLine, plan.Quantity, saleLine.LineCost, saleLine.ReturnedCost);

            saleLine.RegisterReturn(plan.Allocation, plan.Quantity, refund, tax, cost);

            salesReturn.Lines.Add(SalesReturnLine.Create(
                lineNumber,
                saleLine.Id,
                plan.Allocation.MedicineBatchId,
                plan.Quantity,
                refund,
                tax,
                cost,
                plan.Disposition,
                plan.Notes));

            lineNumber++;
        }

        salesReturn.RefundTotal = Money.Round(salesReturn.Lines.Sum(line => line.RefundAmount));

        sale.ApplyReturn(salesReturn);

        return salesReturn;
    }

    /// <summary>
    /// What a return of <paramref name="quantity"/> units of one line would refund if it were recorded
    /// now, so the counter can show the customer a figure before taking the goods back.
    /// </summary>
    /// <remarks>
    /// This is the same split the recorded return performs, not a second copy of the rule, so a preview
    /// cannot drift from the refund it predicts. It is exact for the ordinary case of one entry per line;
    /// a basket that returns the same line in two entries moves at most a paisa between them, because the
    /// entry that clears the line is the one that takes the rounding remainder.
    /// </remarks>
    public static decimal EstimateRefund(SaleLine saleLine, decimal quantity)
    {
        ArgumentNullException.ThrowIfNull(saleLine);

        return ShareWithin(saleLine, quantity, saleLine.LineTotal, saleLine.ReturnedAmount);
    }

    /// <summary>
    /// Splits one money figure charged on a line across a returned quantity, using the shared rule in
    /// <see cref="Money.ShareOfLine"/> so a preview and a recorded return cannot disagree.
    /// </summary>
    private static decimal ShareWithin(
        SaleLine saleLine,
        decimal quantity,
        decimal total,
        decimal alreadyReturned)
        => Money.ShareOfLine(
            total,
            alreadyReturned,
            quantity,
            saleLine.Quantity,
            saleLine.ReturnableQuantity);

    private readonly record struct PlannedLine(
        SaleLine SaleLine,
        SaleLineBatchAllocation Allocation,
        decimal Quantity,
        ReturnDisposition Disposition,
        string? Notes);
}
