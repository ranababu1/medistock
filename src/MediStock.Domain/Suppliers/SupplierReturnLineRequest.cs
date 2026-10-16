namespace MediStock.Domain.Suppliers;

/// <summary>
/// One quantity the operator is sending back: which batch, how much, and anything worth noting about
/// the line. The batch carries the supplier and the cost, so a line needs nothing else to be credited.
/// </summary>
public sealed record SupplierReturnLineRequest(
    int MedicineBatchId,
    decimal Quantity,
    string? Notes = null);