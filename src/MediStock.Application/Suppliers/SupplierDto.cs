namespace MediStock.Application.Suppliers;

/// <summary>Read model for a supplier, safe to bind directly to the UI.</summary>
public sealed record SupplierDto
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? ContactPerson { get; init; }

    public string? Phone { get; init; }

    public string? Email { get; init; }

    public string? AddressLine { get; init; }

    public string? City { get; init; }

    public string? State { get; init; }

    public string? PostalCode { get; init; }

    public string? Gstin { get; init; }

    public string? DrugLicenseNumber { get; init; }

    public string? Notes { get; init; }

    public bool IsActive { get; init; } = true;

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>Single-line address for list columns and invoice headers.</summary>
    public string AddressSummary => string.Join(
        ", ",
        new[] { AddressLine, City, State, PostalCode }.Where(part => !string.IsNullOrWhiteSpace(part)));
}
