using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Catalogue;
using MediStock.Application.Common;
using MediStock.Application.Purchasing;
using MediStock.Application.Suppliers;
using MediStock.Domain.Common;
using MediStock.Domain.Purchasing;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// The purchase editor: header fields plus the line items being ordered. Lines are only editable while
/// the purchase is a draft, because receiving is the point at which stock is committed.
/// </summary>
public sealed partial class PurchaseEditorViewModel : ObservableObject
{
    private readonly IClock _clock;

    /// <summary>
    /// The clock is what makes "today" mean the pharmacy's business date rather than the host's local
    /// date: the invoice date is persisted as a business date and must not slip a day when the machine's
    /// zone differs from <c>MediStock:TimeZoneId</c>.
    /// </summary>
    public PurchaseEditorViewModel(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        InvoiceDate = clock.BusinessDate;
        NewLineExpiryDate = clock.BusinessDate.AddYears(1);

        Lines.CollectionChanged += (_, _) => RaiseTotals();
    }

    public ObservableCollection<PurchaseLineEditorViewModel> Lines { get; } = [];

    /// <summary>Active suppliers offered in the header picker.</summary>
    public ObservableCollection<SupplierDto> Suppliers { get; } = [];

    /// <summary>Active medicines offered in the line picker.</summary>
    public ObservableCollection<MedicineDto> Medicines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew))]
    [NotifyPropertyChangedFor(nameof(Heading))]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    private int? purchaseId;

    [ObservableProperty]
    private int supplierId;

    [ObservableProperty]
    private string invoiceNumber = string.Empty;

    [ObservableProperty]
    private DateOnly invoiceDate;

    [ObservableProperty]
    private string? notes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private PurchaseStatus? status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string? validationMessage;

    // Fields for the line about to be added.
    [ObservableProperty]
    private MedicineDto? newLineMedicine;

    [ObservableProperty]
    private string newLineBatchNumber = string.Empty;

    [ObservableProperty]
    private DateOnly newLineExpiryDate;

    [ObservableProperty]
    private decimal newLineQuantity = 1m;

    [ObservableProperty]
    private decimal newLineUnitCost;

    [ObservableProperty]
    private decimal newLineSellingPrice;

    public bool IsNew => PurchaseId is null;

    /// <summary>Only a draft may be edited; received and closed purchases are historical records.</summary>
    public bool IsEditable => IsNew || Status == PurchaseStatus.Draft;

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public string Heading => IsNew ? "New purchase" : $"Purchase {InvoiceNumber}";

    public string SaveButtonText => IsNew ? "Create purchase" : "Save changes";

    public string StatusText => Status is { } status ? status.ToString() : "Not saved yet";

    public int LineCount => Lines.Count;

    public decimal LinesTotal => Money.Round(Lines.Sum(line => line.LineTotal));

    public string LinesTotalText => LinesTotal.ToString("N2", System.Globalization.CultureInfo.CurrentCulture);

    public string LineSummary => Lines.Count switch
    {
        0 => "No lines yet. Add at least one medicine before the purchase can be received.",
        1 => "1 line",
        _ => $"{Lines.Count} lines",
    };

    /// <summary>Resets the editor to "create new".</summary>
    public void BeginNew()
    {
        PurchaseId = null;
        SupplierId = Suppliers.FirstOrDefault()?.Id ?? 0;
        InvoiceNumber = string.Empty;
        InvoiceDate = _clock.BusinessDate;
        Notes = null;
        Status = null;
        ValidationMessage = null;

        DetachLines();
        Lines.Clear();
        ResetNewLineFields();
        RaiseTotals();
    }

    /// <summary>Loads an existing purchase for editing or review.</summary>
    public void Load(PurchaseDto? purchase)
    {
        if (purchase is null)
        {
            BeginNew();
            return;
        }

        PurchaseId = purchase.Id;
        SupplierId = purchase.SupplierId;
        InvoiceNumber = purchase.InvoiceNumber;
        InvoiceDate = purchase.InvoiceDate;
        Notes = purchase.Notes;
        Status = purchase.Status;
        ValidationMessage = null;

        DetachLines();
        Lines.Clear();

        foreach (var line in purchase.Lines)
        {
            Add(PurchaseLineEditorViewModel.FromDto(line));
        }

        ResetNewLineFields();
        RaiseTotals();
    }

    public PurchaseDraft ToDraft() => new()
    {
        SupplierId = SupplierId,
        InvoiceNumber = InvoiceNumber?.Trim() ?? string.Empty,
        InvoiceDate = InvoiceDate,
        Notes = Notes?.Trim(),
        Lines = [.. Lines.Select(line => line.ToDraft())],
    };

    /// <summary>Returns the first domain rule violation, or null when the draft is acceptable.</summary>
    public string? Validate()
    {
        var problems = ToDraft().Validate();
        ValidationMessage = problems.Count == 0 ? null : problems[0];
        return ValidationMessage;
    }

    /// <summary>Adds the line described by the "new line" fields and clears them for the next entry.</summary>
    [RelayCommand]
    private void AddLine()
    {
        if (NewLineMedicine is not { } medicine)
        {
            ValidationMessage = "Choose a medicine for the new line first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewLineBatchNumber))
        {
            ValidationMessage = "Enter the batch number printed on the pack.";
            return;
        }

        if (Lines.Any(line =>
                line.MedicineId == medicine.Id &&
                string.Equals(line.BatchNumber, NewLineBatchNumber.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ValidationMessage =
                $"Batch '{NewLineBatchNumber.Trim()}' is already on this purchase for {medicine.DisplayName}. " +
                "Combine the quantities into one line instead.";
            return;
        }

        Add(new PurchaseLineEditorViewModel
        {
            MedicineId = medicine.Id,
            MedicineName = medicine.DisplayName,
            BatchNumber = NewLineBatchNumber.Trim(),
            ExpiryDate = NewLineExpiryDate,
            Quantity = NewLineQuantity,
            UnitCost = NewLineUnitCost,
            SellingPrice = NewLineSellingPrice,
        });

        ValidationMessage = null;
        ResetNewLineFields();
        RaiseTotals();
    }

    [RelayCommand]
    private void RemoveLine(PurchaseLineEditorViewModel? line)
    {
        if (line is null || !Lines.Remove(line))
        {
            return;
        }

        line.PropertyChanged -= OnLineChanged;
        RaiseTotals();
    }

    private void Add(PurchaseLineEditorViewModel line)
    {
        line.PropertyChanged += OnLineChanged;
        Lines.Add(line);
    }

    private void DetachLines()
    {
        foreach (var line in Lines)
        {
            line.PropertyChanged -= OnLineChanged;
        }
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PurchaseLineEditorViewModel.Quantity)
            or nameof(PurchaseLineEditorViewModel.UnitCost))
        {
            RaiseTotals();
        }
    }

    private void ResetNewLineFields()
    {
        NewLineMedicine = null;
        NewLineBatchNumber = string.Empty;
        NewLineExpiryDate = _clock.BusinessDate.AddYears(1);
        NewLineQuantity = 1m;
        NewLineUnitCost = 0m;
        NewLineSellingPrice = 0m;
    }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(LineCount));
        OnPropertyChanged(nameof(LinesTotal));
        OnPropertyChanged(nameof(LinesTotalText));
        OnPropertyChanged(nameof(LineSummary));
    }
}
