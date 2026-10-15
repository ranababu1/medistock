namespace MediStock.Domain.Inventory;

/// <summary>Lifecycle state of a received batch.</summary>
public enum BatchStatus
{
    /// <summary>On the shelf and eligible for sale while it has quantity and is not expired.</summary>
    Active = 1,

    /// <summary>All quantity has been sold or otherwise issued.</summary>
    Depleted = 2,

    /// <summary>Past its expiry date.</summary>
    Expired = 3,

    /// <summary>Damaged, unsellable, or written off with a recorded reason.</summary>
    WrittenOff = 4,

    /// <summary>Blocked by a manufacturer recall.</summary>
    Recalled = 5,
}
