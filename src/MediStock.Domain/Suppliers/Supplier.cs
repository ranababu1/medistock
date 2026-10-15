using MediStock.Domain.Common;

namespace MediStock.Domain.Suppliers;

/// <summary>
/// A party medicines are purchased from. Suppliers are deactivated rather than deleted so that
/// historical purchases and batch receipts keep resolving to a real record.
/// </summary>
public class Supplier
{
    public const int NameMaxLength = 200;
    public const int PhoneMaxLength = 32;
    public const int EmailMaxLength = 256;
    public const int AddressMaxLength = 400;
    public const int CityMaxLength = 100;
    public const int StateMaxLength = 100;
    public const int PostalCodeMaxLength = 16;
    public const int GstinMaxLength = 15;
    public const int DrugLicenseMaxLength = 40;
    public const int ContactPersonMaxLength = 150;
    public const int NotesMaxLength = 1000;

    /// <summary>Required by Entity Framework Core.</summary>
    private Supplier()
    {
    }

    private Supplier(string name)
    {
        Name = name;
    }

    public int Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? ContactPerson { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? AddressLine { get; private set; }

    public string? City { get; private set; }

    public string? State { get; private set; }

    public string? PostalCode { get; private set; }

    /// <summary>Indian GST identification number of the supplier.</summary>
    public string? Gstin { get; private set; }

    public string? DrugLicenseNumber { get; private set; }

    public string? Notes { get; private set; }

    public bool IsActive { get; private set; } = true;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static Supplier Create(
        string name,
        string? contactPerson = null,
        string? phone = null,
        string? email = null,
        string? addressLine = null,
        string? city = null,
        string? state = null,
        string? postalCode = null,
        string? gstin = null,
        string? drugLicenseNumber = null,
        string? notes = null,
        DateTimeOffset? nowUtc = null)
    {
        var timestamp = nowUtc ?? DateTimeOffset.UtcNow;
        var supplier = new Supplier(Guard.Required(name, NameMaxLength, "Supplier name"))
        {
            CreatedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
            IsActive = true,
        };

        supplier.ApplyDetails(
            contactPerson, phone, email, addressLine, city, state, postalCode, gstin, drugLicenseNumber, notes);

        return supplier;
    }

    public void Update(
        string name,
        string? contactPerson,
        string? phone,
        string? email,
        string? addressLine,
        string? city,
        string? state,
        string? postalCode,
        string? gstin,
        string? drugLicenseNumber,
        string? notes,
        DateTimeOffset? nowUtc = null)
    {
        Name = Guard.Required(name, NameMaxLength, "Supplier name");
        ApplyDetails(contactPerson, phone, email, addressLine, city, state, postalCode, gstin, drugLicenseNumber, notes);
        UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
    }

    public void Deactivate(DateTimeOffset? nowUtc = null)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
    }

    public void Reactivate(DateTimeOffset? nowUtc = null)
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
    }

    private void ApplyDetails(
        string? contactPerson,
        string? phone,
        string? email,
        string? addressLine,
        string? city,
        string? state,
        string? postalCode,
        string? gstin,
        string? drugLicenseNumber,
        string? notes)
    {
        ContactPerson = Guard.Optional(contactPerson, ContactPersonMaxLength, "Contact person");
        Phone = Guard.Optional(phone, PhoneMaxLength, "Phone");
        Email = Guard.Optional(email, EmailMaxLength, "Email");
        AddressLine = Guard.Optional(addressLine, AddressMaxLength, "Address");
        City = Guard.Optional(city, CityMaxLength, "City");
        State = Guard.Optional(state, StateMaxLength, "State");
        PostalCode = Guard.Optional(postalCode, PostalCodeMaxLength, "Postal code");
        Gstin = Guard.Optional(gstin, GstinMaxLength, "GSTIN");
        DrugLicenseNumber = Guard.Optional(drugLicenseNumber, DrugLicenseMaxLength, "Drug licence number");
        Notes = Guard.Optional(notes, NotesMaxLength, "Notes");
    }
}
