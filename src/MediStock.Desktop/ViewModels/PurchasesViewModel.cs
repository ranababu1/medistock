using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Catalogue;
using MediStock.Application.Common;
using MediStock.Application.Purchasing;
using MediStock.Application.Suppliers;
using MediStock.Desktop.Services;
using MediStock.Domain.Purchasing;
using Microsoft.Extensions.Logging;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Purchases and receiving. A supplier invoice is recorded as a draft, then confirmed once — the
/// confirmation is what puts stock on the shelf, and it happens inside a single database transaction.
/// </summary>
public sealed partial class PurchasesViewModel : ViewModelBase
{
    private readonly IPurchaseCatalog _purchases;
    private readonly ISupplierCatalog _suppliers;
    private readonly IMedicineCatalog _medicines;
    private readonly IDialogService _dialogs;
    private readonly ILogger<PurchasesViewModel> _logger;

    public PurchasesViewModel(
        IPurchaseCatalog purchases,
        ISupplierCatalog suppliers,
        IMedicineCatalog medicines,
        IDialogService dialogs,
        IClock clock,
        ILogger<PurchasesViewModel> logger)
        : base("Purchases")
    {
        _purchases = purchases;
        _suppliers = suppliers;
        _medicines = medicines;
        _dialogs = dialogs;
        _logger = logger;

        StatusFilters =
        [
            new("All statuses", null),
            new("Draft", PurchaseStatus.Draft),
            new("Received", PurchaseStatus.Received),
            new("Reversed", PurchaseStatus.Reversed),
            new("Cancelled", PurchaseStatus.Cancelled),
        ];

        SelectedStatusFilter = StatusFilters[0];
        ReceivedOn = clock.BusinessDate;
        ReceivedBy = Environment.UserName;
        Editor = new PurchaseEditorViewModel(clock);

        // The busy flag lives on the base type, so its changes are observed here rather than by
        // implementing a partial method, which is only possible inside the declaring class.
        PropertyChanged += OnOwnPropertyChanged;
    }

    public ObservableCollection<PurchaseSummaryDto> Purchases { get; } = [];

    public PurchaseEditorViewModel Editor { get; }

    public IReadOnlyList<PurchaseStatusFilter> StatusFilters { get; }

    public ObservableCollection<SupplierFilterOption> SupplierFilters { get; } = [];

    [ObservableProperty]
    private string searchTerm = string.Empty;

    [ObservableProperty]
    private PurchaseStatusFilter? selectedStatusFilter;

    [ObservableProperty]
    private SupplierFilterOption? selectedSupplierFilter;

    [ObservableProperty]
    private DateOnly? invoiceDateFrom;

    [ObservableProperty]
    private DateOnly? invoiceDateTo;

    [ObservableProperty]
    private PurchaseSummaryDto? selectedPurchase;

    [ObservableProperty]
    private PurchaseDto? detail;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? duplicateWarning;

    [ObservableProperty]
    private bool hasSearched;

    [ObservableProperty]
    private bool isLoadingDetail;

    /// <summary>Date the goods physically arrived; the constructor seeds it from the business date.</summary>
    [ObservableProperty]
    private DateOnly receivedOn;

    [ObservableProperty]
    private string receivedBy = Environment.UserName;

    public string ResultSummary => Purchases.Count switch
    {
        0 => "No purchases match the current filters.",
        1 => "1 purchase",
        _ => $"{Purchases.Count} purchases",
    };

    public bool HasNoResults => HasSearched && Purchases.Count == 0;

    public bool HasDuplicateWarning => !string.IsNullOrWhiteSpace(DuplicateWarning);

    public bool IsNotBusy => !IsBusy;

    public string DetailHeading => Detail is { } purchase
        ? $"{purchase.SupplierName} · {purchase.InvoiceNumber}"
        : "No purchase selected";

    public string DetailStatusText => Detail switch
    {
        null => "Select a purchase to review it.",
        { Status: PurchaseStatus.Draft } => "Draft — not yet in stock.",
        { Status: PurchaseStatus.Received } purchase =>
            $"Received {purchase.ReceivedOn:yyyy-MM-dd} by {purchase.ReceivedBy}.",
        { Status: PurchaseStatus.Reversed } purchase =>
            $"Reversed {purchase.ClosedAtUtc:yyyy-MM-dd} by {purchase.ClosedBy}: {purchase.StatusReason}",
        { Status: PurchaseStatus.Cancelled } purchase =>
            $"Cancelled {purchase.ClosedAtUtc:yyyy-MM-dd} by {purchase.ClosedBy}: {purchase.StatusReason}",
        _ => string.Empty,
    };

    /// <summary>Receiving is only offered for a saved draft that has at least one line.</summary>
    public bool CanReceive => Detail is { Status: PurchaseStatus.Draft } detail
        && detail.Lines.Count > 0
        && !IsBusy;

    public bool CanReverse => Detail is { Status: PurchaseStatus.Received } && !IsBusy;

    public bool CanCancel => Detail is { Status: PurchaseStatus.Draft } && !IsBusy;

    public bool CanCheckDuplicate => !IsBusy
        && !string.IsNullOrWhiteSpace(Editor.InvoiceNumber)
        && Editor.SupplierId > 0;

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadLookupsAsync(cancellationToken).ConfigureAwait(true);
        await SearchAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ClearError();
        StatusMessage = null;

        try
        {
            var results = await _purchases
                .SearchAsync(
                    new PurchaseSearchQuery
                    {
                        Term = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim(),
                        SupplierId = SelectedSupplierFilter?.SupplierId,
                        Status = SelectedStatusFilter?.Status,
                        InvoiceDateFrom = InvoiceDateFrom,
                        InvoiceDateTo = InvoiceDateTo,
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            var previousId = SelectedPurchase?.Id;

            Purchases.Clear();
            foreach (var purchase in results)
            {
                Purchases.Add(purchase);
            }

            HasSearched = true;
            OnPropertyChanged(nameof(ResultSummary));
            OnPropertyChanged(nameof(HasNoResults));

            SelectedPurchase = previousId is null
                ? Purchases.FirstOrDefault()
                : Purchases.FirstOrDefault(p => p.Id == previousId) ?? Purchases.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Purchase search failed.");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task ClearFiltersAsync()
    {
        SearchTerm = string.Empty;
        SelectedStatusFilter = StatusFilters[0];
        SelectedSupplierFilter = SupplierFilters.FirstOrDefault();
        InvoiceDateFrom = null;
        InvoiceDateTo = null;
        return SearchAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void NewPurchase()
    {
        SelectedPurchase = null;
        Detail = null;
        DuplicateWarning = null;
        StatusMessage = null;
        ClearError();
        Editor.BeginNew();
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        ClearError();
        StatusMessage = null;

        if (Editor.Validate() is { } problem)
        {
            ErrorMessage = problem;
            return;
        }

        var draft = Editor.ToDraft();

        if (!await ConfirmDuplicateInvoiceAsync(Editor.PurchaseId, cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = Editor.PurchaseId is { } id
                ? await _purchases.UpdateAsync(id, draft, cancellationToken).ConfigureAwait(true)
                : await _purchases.CreateAsync(draft, cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                return;
            }

            StatusMessage = Editor.IsNew
                ? $"Created purchase {result.Value!.InvoiceNumber}."
                : $"Saved changes to {result.Value!.InvoiceNumber}.";

            var savedId = result.Value!.Id;
            await SearchAsync(cancellationToken).ConfigureAwait(true);
            SelectedPurchase = Purchases.FirstOrDefault(p => p.Id == savedId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the purchase failed.");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reports earlier purchases with the same supplier and invoice number.</summary>
    [RelayCommand]
    private async Task CheckInvoiceNumberAsync(CancellationToken cancellationToken)
    {
        ClearError();

        try
        {
            await ConfirmDuplicateInvoiceAsync(Editor.PurchaseId, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking for a duplicate invoice number failed.");
            SetError(ex);
        }
    }

    /// <summary>
    /// Confirms the purchase. Returns false when the operator chooses to back out, so a repeated
    /// submission cannot receive the same invoice twice by accident.
    /// </summary>
    [RelayCommand]
    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        if (Detail is not { Status: PurchaseStatus.Draft } purchase)
        {
            return;
        }

        ClearError();
        StatusMessage = null;

        if (purchase.Lines.Count == 0)
        {
            ErrorMessage = "A purchase with no lines cannot be received into stock.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ReceivedBy))
        {
            ErrorMessage = "Enter the name of the operator receiving the goods.";
            return;
        }

        var confirmed = _dialogs.Confirm(
            "Receive into stock",
            $"Receive {purchase.InvoiceNumber} from {purchase.SupplierName} on {ReceivedOn:yyyy-MM-dd}?" +
            $"{Environment.NewLine}{Environment.NewLine}" +
            $"This creates {purchase.Lines.Count} batch(es) and their receipt movements. " +
            "It cannot be undone once any of that stock has been sold.");

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _purchases
                .ReceiveAsync(purchase.Id, ReceivedOn, ReceivedBy.Trim(), cancellationToken)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                return;
            }

            StatusMessage =
                $"Received {result.Value!.InvoiceNumber} into stock: {result.Value.Lines.Count} batch(es) created.";

            var id = purchase.Id;
            await SearchAsync(cancellationToken).ConfigureAwait(true);
            SelectedPurchase = Purchases.FirstOrDefault(p => p.Id == id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receiving the purchase failed.");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Undoes a receipt. Requires a printed reason and is refused once stock has moved on.</summary>
    [RelayCommand]
    private async Task ReverseAsync(CancellationToken cancellationToken)
    {
        if (Detail is not { Status: PurchaseStatus.Received } purchase)
        {
            return;
        }

        ClearError();
        StatusMessage = null;

        var reason = _dialogs.Prompt(
            "Reverse this receipt",
            $"Undoing {purchase.InvoiceNumber} removes the batches it created and writes reversal " +
            $"movements.{Environment.NewLine}{Environment.NewLine}" +
            "This is refused if any of that stock has already been sold, adjusted, or written off. " +
            "Record why the receipt is being reversed.");

        if (reason is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _purchases
                .ReverseAsync(purchase.Id, reason, OperatorName, cancellationToken)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                return;
            }

            StatusMessage = $"Reversed {purchase.InvoiceNumber}. The batches were removed from stock.";
            await ReloadAsync(purchase.Id, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reversing the receipt failed.");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Abandons a draft that never reached stock. History is kept, so nothing is deleted.</summary>
    [RelayCommand]
    private async Task CancelPurchaseAsync(CancellationToken cancellationToken)
    {
        if (Detail is not { Status: PurchaseStatus.Draft } purchase)
        {
            return;
        }

        ClearError();
        StatusMessage = null;

        var reason = _dialogs.Prompt(
            "Cancel this draft",
            $"Abandon purchase {purchase.InvoiceNumber} from {purchase.SupplierName}? It never reached " +
            $"stock, so nothing is removed from inventory. Record why it is being abandoned.");

        if (reason is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _purchases
                .CancelAsync(purchase.Id, reason, OperatorName, cancellationToken)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                return;
            }

            StatusMessage = $"Cancelled {purchase.InvoiceNumber}.";
            await ReloadAsync(purchase.Id, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancelling the purchase failed.");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ResetEditor()
    {
        Editor.Load(Detail ?? null);
        ErrorMessage = null;
        StatusMessage = null;
        DuplicateWarning = null;
    }

    partial void OnSelectedPurchaseChanged(PurchaseSummaryDto? value)
    {
        OnPropertyChanged(nameof(DetailHeading));
        _ = LoadDetailAsync(value);
    }

    partial void OnDetailChanged(PurchaseDto? value)
    {
        OnPropertyChanged(nameof(DetailHeading));
        OnPropertyChanged(nameof(DetailStatusText));
        OnPropertyChanged(nameof(CanReceive));
        OnPropertyChanged(nameof(CanReverse));
        OnPropertyChanged(nameof(CanCancel));
    }

    partial void OnDuplicateWarningChanged(string? value) => OnPropertyChanged(nameof(HasDuplicateWarning));

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(IsBusy))
        {
            return;
        }

        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanReceive));
        OnPropertyChanged(nameof(CanReverse));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanCheckDuplicate));
    }

    private string OperatorName => string.IsNullOrWhiteSpace(ReceivedBy) ? Environment.UserName : ReceivedBy.Trim();

    private async Task LoadLookupsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var suppliers = await _suppliers
                .SearchAsync(new SupplierSearchQuery { Take = SupplierSearchQuery.MaxResults }, cancellationToken)
                .ConfigureAwait(true);

            SupplierFilters.Clear();
            SupplierFilters.Add(new SupplierFilterOption(null, "All suppliers"));

            Editor.Suppliers.Clear();
            foreach (var supplier in suppliers)
            {
                SupplierFilters.Add(new SupplierFilterOption(supplier.Id, supplier.Name));
                Editor.Suppliers.Add(supplier);
            }

            SelectedSupplierFilter = SupplierFilters.FirstOrDefault();

            var medicines = await _medicines
                .SearchAsync(new MedicineSearchQuery { Take = MedicineSearchQuery.MaxResults }, cancellationToken)
                .ConfigureAwait(true);

            Editor.Medicines.Clear();
            foreach (var medicine in medicines)
            {
                Editor.Medicines.Add(medicine);
            }

            Editor.BeginNew();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading supplier and medicine pickers failed.");
            SetError(ex);
        }
    }

    private async Task LoadDetailAsync(PurchaseSummaryDto? summary)
    {
        if (summary is null)
        {
            Detail = null;
            Editor.BeginNew();
            return;
        }

        IsLoadingDetail = true;

        try
        {
            var purchase = await _purchases.FindAsync(summary.Id, CancellationToken.None).ConfigureAwait(true);

            Detail = purchase;
            Editor.Load(purchase);
            DuplicateWarning = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading purchase {Invoice} failed.", summary.InvoiceNumber);
            SetError(ex);
        }
        finally
        {
            IsLoadingDetail = false;
        }
    }

    private async Task ReloadAsync(int purchaseId, CancellationToken cancellationToken)
    {
        var previousFilters = (
            SelectedSupplierFilter,
            SelectedStatusFilter,
            SearchTerm,
            InvoiceDateFrom,
            InvoiceDateTo);

        await SearchAsync(cancellationToken).ConfigureAwait(true);

        (SelectedSupplierFilter, SelectedStatusFilter, SearchTerm, InvoiceDateFrom, InvoiceDateTo) = previousFilters;

        SelectedPurchase = Purchases.FirstOrDefault(p => p.Id == purchaseId);
    }

    /// <summary>
    /// Looks for an existing purchase with the same supplier and invoice number. A repeated invoice is
    /// warned about rather than blocked, because a supplier can legitimately reissue a corrected
    /// invoice on the same number. Returns false only when the operator backs out.
    /// </summary>
    private async Task<bool> ConfirmDuplicateInvoiceAsync(int? excludePurchaseId, CancellationToken cancellationToken)
    {
        DuplicateWarning = null;

        var matches = await _purchases
            .FindByInvoiceAsync(Editor.SupplierId, Editor.InvoiceNumber.Trim(), excludePurchaseId, cancellationToken)
            .ConfigureAwait(true);

        if (matches.Count == 0)
        {
            return true;
        }

        var lines = string.Join(
            Environment.NewLine,
            matches.Take(5).Select(m =>
                $"• {m.InvoiceNumber} on {m.InvoiceDate:yyyy-MM-dd} — {m.Status}, {m.TotalValue:N2}"));

        DuplicateWarning =
            $"This invoice number is already recorded against the same supplier:{Environment.NewLine}{lines}";

        return _dialogs.Confirm(
            "Possible duplicate invoice",
            $"{DuplicateWarning}{Environment.NewLine}{Environment.NewLine}" +
            "Receiving the same invoice twice would double-count stock. Continue anyway?");
    }
}

/// <summary>One option in the purchase status filter.</summary>
public sealed record PurchaseStatusFilter(string Title, PurchaseStatus? Status)
{
    public override string ToString() => Title;
}

/// <summary>One option in the purchase supplier filter.</summary>
public sealed record SupplierFilterOption(int? SupplierId, string Name)
{
    public override string ToString() => Name;
}
