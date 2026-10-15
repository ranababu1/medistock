using MediStock.Domain.Catalogue;
using MediStock.Domain.Common;
using MediStock.Domain.Inventory;
using MediStock.Domain.Suppliers;

namespace MediStock.Domain.Purchasing;

/// <summary>
/// A supplier invoice being prepared, received into stock, or reversed. Aggregate root: every change
/// to its lines goes through <see cref="Purchase"/> so the header status can never contradict them.
/// </summary>
/// <remarks>
/// Receiving is the only operation in the application that creates stock from outside the pharmacy,
/// so the lifecycle is deliberately one-way:
/// Draft -> Received -> (Reversed | nothing).
/// Nothing is deleted. A received purchase is corrected by reversing it, which leaves the original
/// receipt movements and adds matching <see cref="StockMovementType.ReceiptReversal"/> entries.
/// </remarks>
public sealed class Purchase
{
    public const int InvoiceNumberMaxLength = 64;
    public const int NotesMaxLength = 1000;
    public const int ReceivedByMaxLength = StockMovement.PerformedByMaxLength;

    private readonly List<PurchaseLine> _lines = [];

    // EF Core materialisation only.
    private Purchase()
    {
        InvoiceNumber = string.Empty;
    }

    private Purchase(int supplierId, string invoiceNumber, DateOnly invoiceDate, string? notes, DateTimeOffset nowUtc)
    {
        SupplierId = supplierId;
        InvoiceNumber = invoiceNumber;
        InvoiceDate = invoiceDate;
        Notes = notes;
        Status = PurchaseStatus.Draft;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public int Id { get; private set; }

    public int SupplierId { get; private set; }

    public Supplier? Supplier { get; private set; }

    /// <summary>Supplier's own invoice number. Not unique — a warning is raised on likely duplicates.</summary>
    public string InvoiceNumber { get; private set; }

    public DateOnly InvoiceDate { get; private set; }

    public string? Notes { get; private set; }

    public PurchaseStatus Status { get; private set; }

    /// <summary>Ordered value of this purchase at cost.</summary>
    public decimal TotalValue => Money.Round(_lines.Sum(line => line.LineTotal));

    /// <summary>Business date the goods were taken into stock. Null until received.</summary>
    public DateOnly? ReceivedOn { get; private set; }

    public DateTimeOffset? ReceivedAtUtc { get; private set; }

    public string? ReceivedBy { get; private set; }

    /// <summary>When the purchase was cancelled or reversed. Null while it is a draft or received.</summary>
    public DateTimeOffset? ClosedAtUtc { get; private set; }

    /// <summary>Who cancelled or reversed the purchase.</summary>
    public string? ClosedBy { get; private set; }

    /// <summary>Why the purchase was cancelled or reversed. Null until then.</summary>
    public string? StatusReason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public IReadOnlyCollection<PurchaseLine> Lines => _lines;

    /// <summary>True once the purchase has been received and has not been reversed.</summary>
    public bool HasStockOnHand => Status == PurchaseStatus.Received;

    /// <summary>True while the header and lines may still be edited.</summary>
    public bool IsEditable => Status == PurchaseStatus.Draft;

    /// <summary>True while the purchase can still be taken into stock.</summary>
    public bool CanReceive => Status == PurchaseStatus.Draft && _lines.Count > 0;

    /// <summary>True while the receipt can still be undone.</summary>
    public bool CanReverse => Status == PurchaseStatus.Received;

    public static Purchase Create(
        int supplierId,
        string invoiceNumber,
        DateOnly invoiceDate,
        string? notes = null,
        DateTimeOffset? nowUtc = null)
    {
        if (supplierId <= 0)
        {
            throw new DomainValidationException("A purchase must reference an existing supplier.");
        }

        return new Purchase(
            supplierId,
            Guard.Required(invoiceNumber, InvoiceNumberMaxLength, "Invoice number"),
            invoiceDate,
            Guard.Optional(notes, NotesMaxLength, "Notes"),
            nowUtc ?? DateTimeOffset.UtcNow);
    }

    public void UpdateHeader(
        int supplierId,
        string invoiceNumber,
        DateOnly invoiceDate,
        string? notes,
        DateTimeOffset? nowUtc = null)
    {
        EnsureEditable();

        if (supplierId <= 0)
        {
            throw new DomainValidationException("A purchase must reference an existing supplier.");
        }

        // Validate everything before assigning anything: a rejected edit must leave the header untouched.
        var invoice = Guard.Required(invoiceNumber, InvoiceNumberMaxLength, "Invoice number");
        var trimmedNotes = Guard.Optional(notes, NotesMaxLength, "Notes");

        SupplierId = supplierId;
        InvoiceNumber = invoice;
        InvoiceDate = invoiceDate;
        Notes = trimmedNotes;
        Touch(nowUtc);
    }

    /// <summary>
    /// Adds a line. Rejects a second line with the same medicine and batch number, because that is
    /// almost always a double entry of the same physical batch.
    /// </summary>
    public PurchaseLine AddLine(
        int medicineId,
        string batchNumber,
        DateOnly expiryDate,
        decimal quantity,
        decimal unitCost,
        decimal sellingPrice,
        DateTimeOffset? nowUtc = null)
    {
        EnsureEditable();

        var line = PurchaseLine.Create(medicineId, batchNumber, expiryDate, quantity, unitCost, sellingPrice);

        if (_lines.Any(existing =>
                existing.MedicineId == line.MedicineId &&
                string.Equals(existing.BatchNumber, line.BatchNumber, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainValidationException(
                $"Batch '{line.BatchNumber}' is already on this purchase for the same medicine. " +
                "Combine the quantities into one line instead.");
        }

        _lines.Add(line);
        Touch(nowUtc);

        return line;
    }

    public void RemoveLine(int lineId, DateTimeOffset? nowUtc = null)
    {
        EnsureEditable();

        var line = _lines.FirstOrDefault(existing => existing.Id == lineId)
            ?? throw new DomainValidationException("That line is no longer on this purchase.");

        _lines.Remove(line);
        Touch(nowUtc);
    }

    /// <summary>
    /// Confirms the purchase and reports what must be taken into stock. Every line then becomes one
    /// batch and one receipt movement; the caller does that work inside a single transaction.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// When the purchase is not a draft, has no lines, or has a line whose expiry date is not after
    /// the receiving date.
    /// </exception>
    public IReadOnlyList<PurchaseLineReceipt> Receive(
        DateOnly receivedOn,
        string receivedBy,
        DateTimeOffset? nowUtc = null)
    {
        if (!CanReceive)
        {
            throw new DomainValidationException(
                $"Only a draft purchase with at least one line can be received. This purchase is {Status} " +
                $"and has {_lines.Count} line(s).");
        }

        var expiredOnReceipt = _lines
            .Where(line => line.ExpiryDate <= receivedOn)
            .Select(line => $"'{line.BatchNumber}' expires {line.ExpiryDate:yyyy-MM-dd}")
            .ToList();

        if (expiredOnReceipt.Count > 0)
        {
            throw new DomainValidationException(
                $"Cannot receive stock that expires on or before {receivedOn:yyyy-MM-dd}: " +
                string.Join("; ", expiredOnReceipt) + ".");
        }

        var operatorName = Guard.Required(receivedBy, ReceivedByMaxLength, "Operator");
        var timestamp = nowUtc ?? DateTimeOffset.UtcNow;

        foreach (var line in _lines)
        {
            line.MarkReceived();
        }

        Status = PurchaseStatus.Received;
        ReceivedOn = receivedOn;
        ReceivedAtUtc = timestamp;
        ReceivedBy = operatorName;
        UpdatedAtUtc = timestamp;

        return [.. _lines.Select(line => new PurchaseLineReceipt(
            line.Id,
            line.MedicineId,
            line.BatchNumber,
            line.ExpiryDate,
            line.Quantity,
            line.UnitCost,
            line.SellingPrice))];
    }

    /// <summary>Links a line to the batch created for it. Called after the batches have been saved.</summary>
    public void AttachBatch(int lineId, MedicineBatch batch)
    {
        var line = _lines.FirstOrDefault(existing => existing.Id == lineId)
            ?? throw new DomainValidationException("That line is no longer on this purchase.");

        line.AttachBatch(batch);
    }

    /// <summary>Abandons a draft that never reached stock.</summary>
    public void Cancel(string reason, string cancelledBy, DateTimeOffset? nowUtc = null)
    {
        EnsureEditable();

        // Validate first: a cancellation without a reason must not leave the purchase half-cancelled.
        var validatedReason = Guard.Required(reason, StockMovement.ReasonMaxLength, "Cancellation reason");
        var validatedBy = Guard.Required(cancelledBy, ReceivedByMaxLength, "Operator");
        var timestamp = nowUtc ?? DateTimeOffset.UtcNow;

        Status = PurchaseStatus.Cancelled;
        StatusReason = validatedReason;
        ClosedBy = validatedBy;
        ClosedAtUtc = timestamp;
        UpdatedAtUtc = timestamp;
    }

    /// <summary>
    /// Undoes a receipt. Does not touch batches or quantities: the caller must first confirm that no
    /// stock from the linked batches has been consumed, then remove that stock with matching
    /// <see cref="StockMovementType.ReceiptReversal"/> movements so the ledger still reconciles.
    /// </summary>
    public void Reverse(string reason, string reversedBy, DateTimeOffset? nowUtc = null)
    {
        if (!CanReverse)
        {
            throw new DomainValidationException(
                $"Only a received purchase can be reversed. This purchase is {Status}.");
        }

        // Validate first: a reversal without a reason must not leave the purchase half-reversed.
        var validatedReason = Guard.Required(reason, StockMovement.ReasonMaxLength, "Reversal reason");
        var validatedBy = Guard.Required(reversedBy, ReceivedByMaxLength, "Operator");
        var timestamp = nowUtc ?? DateTimeOffset.UtcNow;

        Status = PurchaseStatus.Reversed;
        StatusReason = validatedReason;
        ClosedBy = validatedBy;
        ClosedAtUtc = timestamp;
        UpdatedAtUtc = timestamp;
    }

    /// <summary>The batches this purchase put into stock, in line order.</summary>
    public IReadOnlyList<MedicineBatch> LinkedBatches() =>
        [.. _lines.Where(line => line.MedicineBatch is not null).Select(line => line.MedicineBatch!)];

    private void EnsureEditable()
    {
        if (!IsEditable)
        {
            throw new DomainValidationException(
                $"This purchase is {Status} and can no longer be edited. " +
                "Correct a received purchase by reversing it.");
        }
    }

    private void Touch(DateTimeOffset? nowUtc) => UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
}
