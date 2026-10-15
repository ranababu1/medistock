using MediStock.Domain.Common;

namespace MediStock.Domain.Inventory;

/// <summary>Raised when a stock operation would take a batch or a basket below zero.</summary>
public sealed class InsufficientStockException : DomainException
{
    public InsufficientStockException(string message) : base(message)
    {
    }

    public InsufficientStockException(decimal requested, decimal available)
        : base($"Insufficient stock: {requested:0.###} requested but only {available:0.###} is sellable.")
    {
    }

    public InsufficientStockException(string batchNumber, decimal requested, decimal available)
        : base($"Batch '{batchNumber}' has only {available:0.###} available; {requested:0.###} requested.")
    {
    }
}
