using MediStock.Domain.Common;
using MediStock.Domain.Suppliers;

namespace MediStock.Application.Suppliers;

/// <summary>
/// User-supplied values for creating or editing a supplier. Validation delegates to the domain entity
/// so the rules exist in exactly one place.
/// </summary>
public sealed record SupplierDraft
{
    public static SupplierDraft Empty { get; } = new();

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

    public static SupplierDraft FromDto(SupplierDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new SupplierDraft
        {
            Name = dto.Name,
            ContactPerson = dto.ContactPerson,
            Phone = dto.Phone,
            Email = dto.Email,
            AddressLine = dto.AddressLine,
            City = dto.City,
            State = dto.State,
            PostalCode = dto.PostalCode,
            Gstin = dto.Gstin,
            DrugLicenseNumber = dto.DrugLicenseNumber,
            Notes = dto.Notes,
        };
    }

    /// <summary>Returns every domain rule violation, so the editor can show them all at once.</summary>
    public IReadOnlyList<string> Validate()
    {
        try
        {
            Supplier.Create(
                Name,
                ContactPerson,
                Phone,
                Email,
                AddressLine,
                City,
                State,
                PostalCode,
                Gstin,
                DrugLicenseNumber,
                Notes);

            return [];
        }
        catch (DomainValidationException ex)
        {
            return [ex.Message];
        }
    }

    /// <summary>Builds a validated entity. Throws <see cref="DomainValidationException"/> when invalid.</summary>
    public Supplier ToNewSupplier(DateTimeOffset nowUtc) => Supplier.Create(
        Name,
        ContactPerson,
        Phone,
        Email,
        AddressLine,
        City,
        State,
        PostalCode,
        Gstin,
        DrugLicenseNumber,
        Notes,
        nowUtc);

    public void ApplyTo(Supplier supplier, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(supplier);

        supplier.Update(
            Name,
            ContactPerson,
            Phone,
            Email,
            AddressLine,
            City,
            State,
            PostalCode,
            Gstin,
            DrugLicenseNumber,
            Notes,
            nowUtc);
    }
}
