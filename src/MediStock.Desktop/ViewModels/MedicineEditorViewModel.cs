using CommunityToolkit.Mvvm.ComponentModel;
using MediStock.Application.Catalogue;
using MediStock.Domain.Catalogue;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Master-detail editor for a single catalogue record. Validation is delegated to
/// <see cref="MedicineDraft.Validate"/> so the rules live only in the domain entity.
/// </summary>
public sealed partial class MedicineEditorViewModel : ObservableObject
{
    public IReadOnlyList<DosageForm> DosageForms { get; } =
        Enum.GetValues<DosageForm>()
            .Where(form => form != DosageForm.Unknown)
            .OrderBy(form => form == DosageForm.Other ? 1 : 0)
            .ThenBy(form => form.ToString(), StringComparer.CurrentCulture)
            .ToArray();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew))]
    [NotifyPropertyChangedFor(nameof(Heading))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    private int? medicineId;

    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private string? brandName;

    [ObservableProperty]
    private string? genericName;

    [ObservableProperty]
    private string? strength;

    [ObservableProperty]
    private DosageForm dosageForm = DosageForm.Tablet;

    [ObservableProperty]
    private string? manufacturer;

    [ObservableProperty]
    private string? barcode;

    [ObservableProperty]
    private string? packDescription;

    [ObservableProperty]
    private decimal? reorderThreshold;

    /// <summary>
    /// Tax charged on this medicine. Copied onto each invoice line at the moment of sale, so changing
    /// it here never alters an invoice that has already been issued.
    /// </summary>
    [ObservableProperty]
    private decimal taxRatePercent;

    [ObservableProperty]
    private bool isActive = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string? validationMessage;

    public bool IsNew => MedicineId is null;

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public string Heading => IsNew ? "New medicine" : $"Edit {DisplayName}";

    public string SaveButtonText => IsNew ? "Create medicine" : "Save changes";

    /// <summary>Resets the editor to "create new".</summary>
    public void BeginNew()
    {
        MedicineId = null;
        DisplayName = string.Empty;
        BrandName = null;
        GenericName = null;
        Strength = null;
        DosageForm = DosageForm.Tablet;
        Manufacturer = null;
        Barcode = null;
        PackDescription = null;
        ReorderThreshold = null;
        TaxRatePercent = 0m;
        IsActive = true;
        ValidationMessage = null;
    }

    /// <summary>Loads an existing record for editing.</summary>
    public void Load(MedicineDto? dto)
    {
        if (dto is null)
        {
            BeginNew();
            return;
        }

        MedicineId = dto.Id;
        DisplayName = dto.DisplayName;
        BrandName = dto.BrandName;
        GenericName = dto.GenericName;
        Strength = dto.Strength;
        DosageForm = dto.DosageForm == DosageForm.Unknown ? DosageForm.Tablet : dto.DosageForm;
        Manufacturer = dto.Manufacturer;
        Barcode = dto.Barcode;
        PackDescription = dto.PackDescription;
        ReorderThreshold = dto.ReorderThreshold;
        TaxRatePercent = dto.TaxRatePercent;
        IsActive = dto.IsActive;
        ValidationMessage = null;
    }

    public MedicineDraft ToDraft() => new()
    {
        DisplayName = DisplayName?.Trim() ?? string.Empty,
        BrandName = BrandName?.Trim(),
        GenericName = GenericName?.Trim(),
        Strength = Strength?.Trim(),
        DosageForm = DosageForm,
        Manufacturer = Manufacturer?.Trim(),
        Barcode = Barcode?.Trim(),
        PackDescription = PackDescription?.Trim(),
        ReorderThreshold = ReorderThreshold,
        TaxRatePercent = TaxRatePercent,
    };

    /// <summary>Returns the first domain rule violation, or null when the draft is acceptable.</summary>
    public string? Validate()
    {
        var problems = ToDraft().Validate();
        ValidationMessage = problems.Count == 0 ? null : problems[0];
        return ValidationMessage;
    }

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(Heading));
}
