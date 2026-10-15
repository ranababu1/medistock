using CommunityToolkit.Mvvm.ComponentModel;
using MediStock.Application.Suppliers;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Master-detail editor for a single supplier. Validation is delegated to
/// <see cref="SupplierDraft.Validate"/> so the rules live only in the domain entity.
/// </summary>
public sealed partial class SupplierEditorViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew))]
    [NotifyPropertyChangedFor(nameof(Heading))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    private int? supplierId;

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private string? contactPerson;

    [ObservableProperty]
    private string? phone;

    [ObservableProperty]
    private string? email;

    [ObservableProperty]
    private string? addressLine;

    [ObservableProperty]
    private string? city;

    [ObservableProperty]
    private string? state;

    [ObservableProperty]
    private string? postalCode;

    [ObservableProperty]
    private string? gstin;

    [ObservableProperty]
    private string? drugLicenseNumber;

    [ObservableProperty]
    private string? notes;

    [ObservableProperty]
    private bool isActive = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string? validationMessage;

    public bool IsNew => SupplierId is null;

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public string Heading => IsNew ? "New supplier" : $"Edit {Name}";

    public string SaveButtonText => IsNew ? "Create supplier" : "Save changes";

    /// <summary>Resets the editor to "create new".</summary>
    public void BeginNew()
    {
        SupplierId = null;
        Name = string.Empty;
        ContactPerson = null;
        Phone = null;
        Email = null;
        AddressLine = null;
        City = null;
        State = null;
        PostalCode = null;
        Gstin = null;
        DrugLicenseNumber = null;
        Notes = null;
        IsActive = true;
        ValidationMessage = null;
    }

    /// <summary>Loads an existing record for editing.</summary>
    public void Load(SupplierDto? dto)
    {
        if (dto is null)
        {
            BeginNew();
            return;
        }

        SupplierId = dto.Id;
        Name = dto.Name;
        ContactPerson = dto.ContactPerson;
        Phone = dto.Phone;
        Email = dto.Email;
        AddressLine = dto.AddressLine;
        City = dto.City;
        State = dto.State;
        PostalCode = dto.PostalCode;
        Gstin = dto.Gstin;
        DrugLicenseNumber = dto.DrugLicenseNumber;
        Notes = dto.Notes;
        IsActive = dto.IsActive;
        ValidationMessage = null;
    }

    public SupplierDraft ToDraft() => new()
    {
        Name = Name?.Trim() ?? string.Empty,
        ContactPerson = ContactPerson?.Trim(),
        Phone = Phone?.Trim(),
        Email = Email?.Trim(),
        AddressLine = AddressLine?.Trim(),
        City = City?.Trim(),
        State = State?.Trim(),
        PostalCode = PostalCode?.Trim(),
        Gstin = Gstin?.Trim(),
        DrugLicenseNumber = DrugLicenseNumber?.Trim(),
        Notes = Notes?.Trim(),
    };

    /// <summary>Returns the first domain rule violation, or null when the draft is acceptable.</summary>
    public string? Validate()
    {
        var problems = ToDraft().Validate();
        ValidationMessage = problems.Count == 0 ? null : problems[0];
        return ValidationMessage;
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Heading));
}
