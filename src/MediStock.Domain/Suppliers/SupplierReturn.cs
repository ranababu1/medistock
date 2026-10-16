using MediStock.Domain.Common;
using MediStock.Domain.Inventory;

namespace MediStock.Domain.Suppliers;

/// <summary>
/// Medicine sent back to its supplier: which batches, how much, why, and what the supplier is expected
/// to credit for it.
/// </summary>
/// <remarks>
/// <para>
/// A supplier return is one transaction. Every batch named on it gives up its quantity through the
/// stock ledger in the same unit of work that writes the document, so a return that fails leaves the
/// shelf and the supplier history exactly as they were. There is deliberately no reversal: a return
/// that went out in error is corrected by receiving the goods again, and the correction is traceable
/// in the movement history.
/// </para>
/// <para>
/// Three rules are enforced here rather than in the screen:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Medicine can only go back to the supplier it came from — a line naming a batch bought elsewhere is
/// refused, because the credit has to land on the account that sold it.
/// </description></item>
/// <item><description>
/// Recalled and written-off stock cannot be returned. Recalled stock follows the recall notice, and
/// destroyed stock no longer exists; both are disposed of through the write-off workflow instead.
/// </description></item>
/// <item><description>
/// The quantity must be on hand, counting repeated lines against the same batch together, so two lines
/// cannot each take the last unit.
/// </description></item>
/// </list>
/// <para>
/// See <c>docs/decisions/0011-stock-corrections.md</c>.
/// </para>
/// </remarks>
public class SupplierReturn
{
    public const int ReturnNumberMaxLength = 32;
    public const int ReasonMaxLength = 300;
    public const int NotesMaxLength = 500;
    public const int OperatorMaxLength = 150;
    public const int CreditNoteMaxLength = 60;

    /// <summary>Required by Entity Framework Core. Kept private; see <see cref="Record"/>.</summary>
    private SupplierReturn()
    {
    }

    public int Id { get; private set; }

    /// <summary>Return document number, unique across the database and never reused.</summary>
    public string ReturnNumber { get; private set; } = string.Empty;

    /// <summary>Local business date the return counts towards in daily reports.</summary>
    public DateOnly BusinessDate { get; private set; }

    public DateTimeOffset ReturnedAtUtc { get; private set; }

    /// <summary>Operator who sent the goods back.</summary>
    public string ReturnedBy { get; private set; } = string.Empty;

    /// <summary>
    /// The supplier the goods go back to, and the account the credit lands on. Kept as an identifier
    /// only, like the batch's own supplier reference: the name is needed to tell the operator whose
    /// batch it was, and the domain takes that by parameter for the messages it composes.
    /// </summary>
    public int SupplierId { get; private set; }

    /// <summary>Why the medicine is going back. Required, and shown on the return document.</summary>
    public string Reason { get; private set; } = string.Empty;

    public string? Notes { get; private set; }

    /// <summary>The credit note the supplier issued against this return, when it has arrived.</summary>
    public string? SupplierCreditNoteNumber { get; private set; }

    /// <summary>The value of everything on the return at cost. Equals the sum of the line values.</summary>
    public decimal TotalCostValue { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public ICollection<SupplierReturnLine> Lines { get; private set; } = new List<SupplierReturnLine>();

    public decimal ReturnedQuantity => Lines.Sum(line => line.Quantity);

    /// <summary>
    /// Records the return and applies it: each batch gives up its quantity through the ledger and the
    /// document keeps the lines and the credit value.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The request names a batch that was not bought from this supplier, asks for more than a batch is
    /// holding, or names a batch that is blocked from being moved.
    /// </exception>
    public static SupplierReturn Record(
        string returnNumber,
        DateOnly businessDate,
        string returnedBy,
        int supplierId,
        string supplierName,
        string reason,
        IReadOnlyList<SupplierReturnLineRequest> lines,
        IReadOnlyDictionary<int, MedicineBatch> batches,
        string? notes = null,
        string? supplierCreditNoteNumber = null,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(batches);

        if (supplierId <= 0)
        {
            throw new DomainValidationException("A supplier return must name the supplier it goes back to.");
        }

        var supplier = Guard.Required(supplierName, Supplier.NameMaxLength, "Supplier name");

        if (lines.Count == 0)
        {
            throw new DomainValidationException("A supplier return must send back at least one quantity.");
        }

        var timestamp = nowUtc ?? DateTimeOffset.UtcNow;

        var supplierReturn = new SupplierReturn
        {
            ReturnNumber = Guard.Required(returnNumber, ReturnNumberMaxLength, "Return number"),
            BusinessDate = businessDate,
            ReturnedAtUtc = timestamp,
            ReturnedBy = Guard.Required(returnedBy, OperatorMaxLength, "Operator"),
            SupplierId = supplierId,
            Reason = Guard.Required(reason, ReasonMaxLength, "Reason"),
            Notes = Guard.Optional(notes, NotesMaxLength, "Return notes"),
            SupplierCreditNoteNumber = Guard.Optional(
                supplierCreditNoteNumber, CreditNoteMaxLength, "Supplier credit note number"),
            CreatedAtUtc = timestamp,
        };

        // Pass one: reject the whole request before anything moves. Lines are counted per batch so two
        // lines against the same batch cannot together take more than it holds.
        var asked = new Dictionary<int, decimal>();

        foreach (var request in lines)
        {
            if (!batches.TryGetValue(request.MedicineBatchId, out var batch))
            {
                throw new DomainValidationException(
                    $"Batch {request.MedicineBatchId} could not be loaded, so it cannot be returned.");
            }

            if (batch.SupplierId != supplierId)
            {
                throw new DomainValidationException(
                    $"Batch '{batch.BatchNumber}' was not bought from {supplier}, so it cannot be returned to them.");
            }

            if (batch.Status is BatchStatus.Recalled or BatchStatus.WrittenOff)
            {
                throw new DomainValidationException(
                    $"Batch '{batch.BatchNumber}' is {(batch.Status == BatchStatus.Recalled ? "recalled" : "written off")} "
                    + "and cannot be returned to the supplier. Dispose of it through the write-off workflow instead.");
            }

            var quantity = Guard.Positive(request.Quantity, "Returned quantity");
            var onThisBatch = asked.GetValueOrDefault(request.MedicineBatchId) + quantity;

            if (onThisBatch > batch.QuantityOnHand)
            {
                throw new DomainValidationException(
                    $"Batch '{batch.BatchNumber}' is holding {batch.QuantityOnHand:0.###}, so it cannot return "
                    + $"{onThisBatch:0.###}. Reduce the quantity or write the extra off.");
            }

            asked[request.MedicineBatchId] = onThisBatch;
        }

        // Pass two: apply. The batch gives up its quantity through the same guard every outward
        // movement uses, so the cached balance and the movement the caller records cannot disagree.
        var lineNumber = 1;

        foreach (var request in lines)
        {
            var batch = batches[request.MedicineBatchId];

            batch.DecreaseQuantity(request.Quantity, timestamp);

            supplierReturn.Lines.Add(SupplierReturnLine.Create(
                lineNumber,
                request.MedicineBatchId,
                request.Quantity,
                batch.PurchaseCost,
                request.Notes));

            lineNumber++;
        }

        supplierReturn.TotalCostValue = Money.Round(supplierReturn.Lines.Sum(line => line.CostValue));

        return supplierReturn;
    }
}