using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Common;
using MediStock.Application.Reporting;
using MediStock.Application.Sales;
using MediStock.Desktop.Services;
using MediStock.Domain.Common;
using MediStock.Domain.Inventory;
using MediStock.Domain.Sales;
using Microsoft.Extensions.Logging;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Returns and adjustments: take a customer return against an invoice, and read back what has come back
/// lately.
/// </summary>
/// <remarks>
/// <para>
/// The screen assembles a request and hands it to <see cref="ISaleReturnCatalog"/>. It writes no stock and
/// no money itself. The refund it previews is worked out by <see cref="Money.ShareOfLine"/>, the same split
/// the recorded return performs, so the figure shown to the customer is the figure the return pays.
/// </para>
/// <para>
/// Only quantities the customer actually brought back are part of the return: a row left at zero is
/// dropped before the request is sent. The default disposition is quarantine, never restock, and a batch
/// that has expired since it was sold is not offered for restocking at all.
/// </para>
/// <para>
/// The catalog re-reads the invoice inside its transaction, so a stale screen cannot record a return the
/// data no longer allows. Anything it refuses arrives as an error banner with the operator's next step in
/// it.
/// </para>
/// </remarks>
public sealed partial class ReturnsViewModel : ViewModelBase
{
    private readonly ISaleReturnCatalog _returns;
    private readonly ISaleCatalog _sales;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ReturnsViewModel> _logger;

    public ReturnsViewModel(
        ISaleReturnCatalog returns,
        ISaleCatalog sales,
        IDialogService dialogs,
        IClock clock,
        ILogger<ReturnsViewModel> logger)
        : base("Returns & Adjustments")
    {
        ArgumentNullException.ThrowIfNull(clock);

        _returns = returns;
        _sales = sales;
        _dialogs = dialogs;
        _logger = logger;

        Dispositions =
        [
            new(ReturnDisposition.Restock, "Restock — back on the shelf"),
            new(ReturnDisposition.Quarantine, "Quarantine — hold aside"),
            new(ReturnDisposition.Destroy, "Destroy — write off"),
        ];

        RefundMethods =
        [
            new(PaymentMethod.Cash, "Cash"),
            new(PaymentMethod.Card, "Card"),
            new(PaymentMethod.Upi, "UPI"),
            new(PaymentMethod.Other, "Other"),
        ];

        DispositionFilters =
        [
            new("Any disposition", null),
            new("Restocked", ReturnDisposition.Restock),
            new("Quarantined", ReturnDisposition.Quarantine),
            new("Destroyed", ReturnDisposition.Destroy),
        ];

        SelectedRefundMethod = RefundMethods[0];
        SelectedDispositionFilter = DispositionFilters[0];

        ReturnedBy = Environment.UserName;
        BusinessDate = clock.BusinessDate;

        // Returns are rare and worth reading back over a few days; unlike the till, this list is a
        // history rather than a shift view.
        To = clock.BusinessDate;

        PropertyChanged += OnOwnPropertyChanged;
    }

    public ObservableCollection<ReturnLineViewModel> ReturnLines { get; } = [];

    public ObservableCollection<SalesReturnDto> Returns { get; } = [];

    public IReadOnlyList<ReturnDispositionOption> Dispositions { get; }

    public IReadOnlyList<RefundMethodOption> RefundMethods { get; }

    public IReadOnlyList<ReturnDispositionFilter> DispositionFilters { get; }

    /// <summary>Invoice being returned against, once it has been found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInvoice))]
    [NotifyPropertyChangedFor(nameof(InvoiceHeading))]
    [NotifyPropertyChangedFor(nameof(InvoiceSummary))]
    [NotifyPropertyChangedFor(nameof(CanCancelInvoice))]
    [NotifyPropertyChangedFor(nameof(CanTakeReturn))]
    private SaleDto? invoice;

    [ObservableProperty]
    private string invoiceNumber = string.Empty;

    [ObservableProperty]
    private string returnReason = string.Empty;

    [ObservableProperty]
    private string returnedBy = string.Empty;

    [ObservableProperty]
    private DateOnly businessDate;

    /// <summary>How the money goes back. Defaults to cash, the usual counter refund.</summary>
    [ObservableProperty]
    private RefundMethodOption? selectedRefundMethod;

    /// <summary>The number the next return will be given. Shown before the goods are taken in.</summary>
    [ObservableProperty]
    private string nextReturnNumber = "…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private bool hasSearched;

    [ObservableProperty]
    private string searchTerm = string.Empty;

    [ObservableProperty]
    private DateOnly? from;

    [ObservableProperty]
    private DateOnly? to;

    [ObservableProperty]
    private ReturnDispositionFilter? selectedDispositionFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(DetailHeading))]
    private SalesReturnDto? selectedReturn;

    [ObservableProperty]
    private string? statusMessage;

    public bool IsNotBusy => !IsBusy;

    public bool HasInvoice => Invoice is not null;

    public bool HasSelection => SelectedReturn is not null;

    public bool HasNoResults => HasSearched && Returns.Count == 0;

    /// <summary>True when the invoice on screen has at least one batch the customer can still hand back.</summary>
    public bool HasReturnableLines => ReturnLines.Count > 0;

    public bool HasNoReturnableLines => HasInvoice && ReturnLines.Count == 0;

    /// <summary>True when there is something to take back: an invoice with at least one unit still out.</summary>
    public bool CanTakeReturn => Invoice is { Status: SaleStatus.Completed } && ReturnLines.Count > 0;

    /// <summary>
    /// Cancelling is offered only once nothing is left out with the customer, which is the rule the
    /// catalog enforces.
    /// </summary>
    public bool CanCancelInvoice => Invoice?.CanCancel == true;

    public string InvoiceHeading => Invoice is null
        ? "Find the invoice"
        : $"Invoice {Invoice.InvoiceNumber} of {Invoice.BusinessDate:yyyy-MM-dd}";

    public string InvoiceSummary
    {
        get
        {
            var invoice = Invoice;

            return invoice switch
            {
                null => "Type or scan the invoice number from the customer's bill to see what can be taken back.",
                { Status: SaleStatus.Cancelled } => $"This invoice was cancelled by {invoice.CancelledBy}. "
                    + $"It cannot take a return: {invoice.CancellationReason}",
                { IsFullyReturned: true } => "Everything on this invoice has already been returned. "
                    + $"{FormatMoney(invoice.ReturnedTotal)} refunded so far.",
                _ => $"{invoice.CustomerName ?? "Walk-in customer"} · {FormatMoney(invoice.Total)} invoiced · "
                    + $"{FormatMoney(invoice.ReturnedTotal)} already refunded · "
                    + $"{invoice.UnitsReturned:0.###} unit(s) back.",
            };
        }
    }

    /// <summary>
    /// What the rows as they stand would refund. Each row is priced on its own, which is exact for the
    /// ordinary case of one entry per invoice line; the recorded return is what actually decides.
    /// </summary>
    public decimal RefundPreviewTotal => Money.Round(ReturnLines.Sum(line => line.RefundPreview));

    public string RefundPreviewText => RefundPreviewTotal.ToString("N2", CultureInfo.CurrentCulture);

    public string RefundPreviewSummary =>
        $"{ReturnLines.Count(line => line.Quantity > 0m)} row(s) to record, refunding {RefundPreviewText}.";

    public string ResultSummary => Returns.Count switch
    {
        0 => "No returns match the current filters.",
        1 => "1 return",
        _ => $"{Returns.Count} returns",
    };

    public string ListedRefundText =>
        Returns.Sum(item => item.RefundTotal).ToString("N2", CultureInfo.CurrentCulture);

    public string DetailHeading => SelectedReturn is null
        ? "Select a return"
        : $"Return {SelectedReturn.ReturnNumber} against invoice {SelectedReturn.InvoiceNumber}";

    public string DetailSummary => SelectedReturn is null
        ? "What was taken back, from which batch, and what was done with the goods."
        : $"{FormatMoney(SelectedReturn.RefundTotal)} refunded "
          + $"{ReportText.Describe(SelectedReturn.RefundMethod).ToLowerInvariant()} "
          + $"by {SelectedReturn.ReturnedBy} · {SelectedReturn.ReturnedQuantity:0.###} unit(s) back, "
          + $"{SelectedReturn.RestockedQuantity:0.###} restocked · {SelectedReturn.Reason}";

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshReturnNumberAsync(cancellationToken).ConfigureAwait(true);
        await SearchAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Looks the invoice up and works out what is left to come back, batch by batch.</summary>
    [RelayCommand]
    private async Task FindInvoiceAsync(CancellationToken cancellationToken)
    {
        ClearError();
        StatusMessage = null;
        ClearReturnLines();
        Invoice = null;

        var number = InvoiceNumber.Trim();

        if (number.Length == 0)
        {
            ErrorMessage = "Type or scan the invoice number printed on the customer's bill.";
            return;
        }

        IsBusy = true;

        try
        {
            var sale = await _sales.FindByInvoiceNumberAsync(number, cancellationToken).ConfigureAwait(true);

            if (sale is null)
            {
                ErrorMessage = $"No invoice numbered '{number}' was found. Check the number, "
                    + "or look it up in Sales History.";
                return;
            }

            Invoice = sale;
            InvoiceNumber = sale.InvoiceNumber;

            if (sale.Status == SaleStatus.Cancelled)
            {
                ErrorMessage = InvoiceSummary;
                return;
            }

            foreach (var line in sale.Lines)
            {
                foreach (var allocation in line.Allocations.Where(item => item.ReturnableQuantity > 0m))
                {
                    var row = new ReturnLineViewModel(line, allocation, sale.BusinessDate, BusinessDate, Dispositions);
                    row.PropertyChanged += OnReturnLineChanged;
                    ReturnLines.Add(row);
                }
            }

            OnPropertyChanged(nameof(RefundPreviewTotal));
            OnPropertyChanged(nameof(RefundPreviewText));
            OnPropertyChanged(nameof(RefundPreviewSummary));
            OnPropertyChanged(nameof(CanTakeReturn));
            OnPropertyChanged(nameof(HasReturnableLines));
            OnPropertyChanged(nameof(HasNoReturnableLines));

            if (ReturnLines.Count == 0)
            {
                StatusMessage = $"Nothing on invoice {sale.InvoiceNumber} can still be returned. "
                    + $"{FormatMoney(sale.ReturnedTotal)} has already been refunded against it.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Looking up invoice {InvoiceNumber} for a return failed.", number);
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Records the return, refunds it and disposes of the goods, in one transaction.</summary>
    [RelayCommand]
    private async Task RecordReturnAsync(CancellationToken cancellationToken)
    {
        ClearError();
        StatusMessage = null;

        var invoice = Invoice;

        if (invoice is null)
        {
            return;
        }

        var requested = ReturnLines.Where(line => line.Quantity > 0m).ToList();

        if (requested.Count == 0)
        {
            ErrorMessage = "No quantity has been filled in, so there is nothing to take back. "
                + "Set a quantity on each pack that came back.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ReturnReason))
        {
            ErrorMessage = "A reason is required on a return. It goes on the return document "
                + "and into the audit trail.";
            return;
        }

        var preview = Money.Round(requested.Sum(line => line.RefundPreview));
        var restocking = requested.Count(line => line.Disposition == ReturnDisposition.Restock);

        var confirmed = _dialogs.Confirm(
            "Record this return",
            $"Return {NextReturnNumber} against invoice {invoice.InvoiceNumber}.{Environment.NewLine}{Environment.NewLine}"
            + $"{requested.Count} row(s), {requested.Sum(line => line.Quantity):0.###} unit(s), "
            + $"refunding {FormatMoney(preview)} "
            + $"{ReportText.Describe(SelectedRefundMethod?.Method ?? PaymentMethod.Cash).ToLowerInvariant()}."
            + Environment.NewLine
            + $"{restocking} row(s) go back on the shelf; the rest stay out of sellable stock."
            + Environment.NewLine + Environment.NewLine
            + "This is written to the stock ledger and cannot be edited afterwards; "
            + "a mistake is corrected by another return or an adjustment.");

        if (!confirmed)
        {
            StatusMessage = "Nothing was recorded.";
            return;
        }

        IsBusy = true;

        try
        {
            var draft = new SalesReturnDraft
            {
                SaleId = invoice.Id,
                ReturnedBy = ReturnedBy,
                Reason = ReturnReason,
                RefundMethod = SelectedRefundMethod?.Method ?? PaymentMethod.Cash,
                BusinessDate = BusinessDate,
                Lines =
                [
                    .. requested.Select(line => new SalesReturnLineDraft
                    {
                        LineNumber = line.LineNumber,
                        MedicineBatchId = line.Allocation.MedicineBatchId,
                        Quantity = line.Quantity,
                        Disposition = line.Disposition,
                        Notes = line.Notes,
                    }),
                ],
            };

            var outcome = await _returns.RecordAsync(draft, cancellationToken).ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                ErrorMessage = outcome.Message;
                return;
            }

            var recorded = outcome.Value!;

            StatusMessage = $"Recorded {recorded.ReturnNumber} against invoice {recorded.InvoiceNumber}: "
                + $"{FormatMoney(recorded.RefundTotal)} refunded, {recorded.ReturnedQuantity:0.###} unit(s) back, "
                + $"{recorded.RestockedQuantity:0.###} restocked.";

            _logger.LogInformation(
                "Recorded return {ReturnNumber} against invoice {InvoiceNumber}: {Refund} refunded.",
                recorded.ReturnNumber,
                recorded.InvoiceNumber,
                recorded.RefundTotal);

            // The form is emptied rather than left showing quantities that have already been taken back,
            // so a second click cannot become a second return of the same packs.
            ClearReturnLines();
            Invoice = null;
            InvoiceNumber = string.Empty;
            ReturnReason = string.Empty;

            await RefreshReturnNumberAsync(cancellationToken).ConfigureAwait(true);
            await SearchAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recording a return against invoice {InvoiceId} failed.", invoice.Id);
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Cancels an invoice once every unit on it has come back. The document keeps its number and its
    /// lines; cancelling records who decided and why, and the stock was put back by the return itself.
    /// </summary>
    [RelayCommand]
    private async Task CancelInvoiceAsync(CancellationToken cancellationToken)
    {
        ClearError();
        StatusMessage = null;

        var invoice = Invoice;

        if (invoice is null || !invoice.CanCancel)
        {
            return;
        }

        var reason = _dialogs.Prompt(
            "Cancel this invoice",
            $"Invoice {invoice.InvoiceNumber} will be marked cancelled. Everything on it has already been "
            + "returned, so no stock moves. Give the reason, which is recorded against the invoice:",
            "Customer returned the whole order");

        if (string.IsNullOrWhiteSpace(reason))
        {
            StatusMessage = "Nothing was cancelled.";
            return;
        }

        IsBusy = true;

        try
        {
            var outcome = await _returns
                .CancelSaleAsync(invoice.Id, ReturnedBy, reason.Trim(), cancellationToken)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                ErrorMessage = outcome.Message;
                return;
            }

            var cancelled = outcome.Value!;
            StatusMessage = $"Invoice {cancelled.InvoiceNumber} was cancelled by {cancelled.CancelledBy}: "
                + $"{cancelled.CancellationReason}";

            _logger.LogInformation(
                "Cancelled invoice {InvoiceNumber}: {Reason}",
                cancelled.InvoiceNumber,
                cancelled.CancellationReason);

            ClearReturnLines();
            Invoice = cancelled;
            await SearchAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cancelling invoice {InvoiceId} failed.", invoice.Id);
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        ClearError();
        IsBusy = true;

        try
        {
            var results = await _returns
                .SearchAsync(
                    new SalesReturnQuery
                    {
                        Term = SearchTerm,
                        FromBusinessDate = From,
                        ToBusinessDate = To,
                        Disposition = SelectedDispositionFilter?.Disposition,
                        Take = 500,
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            Returns.Clear();
            foreach (var item in results)
            {
                Returns.Add(item);
            }

            HasSearched = true;
            NotifyHistoryChanged();

            if (SelectedReturn is not null)
            {
                SelectedReturn = Returns.FirstOrDefault(item => item.Id == SelectedReturn.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the returns history failed.");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchTerm = string.Empty;
        From = null;
        To = null;
        SelectedDispositionFilter = DispositionFilters[0];
        ClearError();
    }

    /// <summary>Empties the return form without touching the history.</summary>
    [RelayCommand]
    private void ClearForm()
    {
        ClearReturnLines();
        Invoice = null;
        InvoiceNumber = string.Empty;
        ReturnReason = string.Empty;
        ClearError();
        StatusMessage = null;
    }

    /// <summary>Puts a return document aside on screen so the same figures can be read again.</summary>
    [RelayCommand]
    private void SelectReturn(SalesReturnDto? item) => SelectedReturn = item;

    private async Task RefreshReturnNumberAsync(CancellationToken cancellationToken)
    {
        try
        {
            NextReturnNumber = await _returns.PreviewReturnNumberAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The number is a courtesy: the return itself allocates it inside its transaction. A preview
            // that cannot be read is not worth an error banner.
            NextReturnNumber = "—";
            _logger.LogWarning(ex, "Previewing the next return number failed.");
        }
    }

    /// <summary>
    /// The history the screen shows is replaced as a whole, so the figures derived from it have to be read
    /// again rather than left as they were when the screen was still empty.
    /// </summary>
    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(ListedRefundText));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(DetailHeading));
        OnPropertyChanged(nameof(DetailSummary));
    }

    private void ClearReturnLines()
    {
        foreach (var line in ReturnLines)
        {
            line.PropertyChanged -= OnReturnLineChanged;
        }

        ReturnLines.Clear();
        OnPropertyChanged(nameof(RefundPreviewTotal));
        OnPropertyChanged(nameof(RefundPreviewText));
        OnPropertyChanged(nameof(RefundPreviewSummary));
        OnPropertyChanged(nameof(CanTakeReturn));
        OnPropertyChanged(nameof(HasReturnableLines));
        OnPropertyChanged(nameof(HasNoReturnableLines));
    }

    private void OnReturnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ReturnLineViewModel.Quantity))
        {
            return;
        }

        OnPropertyChanged(nameof(RefundPreviewTotal));
        OnPropertyChanged(nameof(RefundPreviewText));
        OnPropertyChanged(nameof(RefundPreviewSummary));
    }

    partial void OnSelectedReturnChanged(SalesReturnDto? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(DetailHeading));
        OnPropertyChanged(nameof(DetailSummary));
    }

    partial void OnHasSearchedChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));

    partial void OnBusinessDateChanged(DateOnly value)
    {
        // An expiry hint is about today, so it moves with the date the operator is working on.
        foreach (var line in ReturnLines)
        {
            line.SetBusinessDate(value);
        }
    }

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(IsBusy))
        {
            return;
        }

        OnPropertyChanged(nameof(IsNotBusy));
    }

    private static string FormatMoney(decimal amount) => amount.ToString("N2", CultureInfo.CurrentCulture);
}

/// <summary>One choice of what happens to returned goods.</summary>
public sealed record ReturnDispositionOption(ReturnDisposition Disposition, string Label);

/// <summary>One choice of how a refund goes back.</summary>
public sealed record RefundMethodOption(PaymentMethod Method, string Label);

/// <summary>One choice in the returns-history disposition filter.</summary>
public sealed record ReturnDispositionFilter(string Label, ReturnDisposition? Disposition);

/// <summary>
/// One pack coming back: an invoice line, the batch it was dispensed from, and what is to be done with it.
/// </summary>
/// <remarks>
/// A row exists only for stock the customer could still bring back, so a line that has already been
/// returned in full has no row at all. The refund shown is the split the domain will perform, so the
/// figure on screen is the figure that gets paid.
/// </remarks>
public sealed partial class ReturnLineViewModel : ObservableObject
{
    private readonly DateOnly _invoiceBusinessDate;

    public ReturnLineViewModel(
        SaleLineDto line,
        SaleAllocationDto allocation,
        DateOnly invoiceBusinessDate,
        DateOnly businessDate,
        IReadOnlyList<ReturnDispositionOption> dispositions)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(dispositions);

        Line = line;
        Allocation = allocation;
        _invoiceBusinessDate = invoiceBusinessDate;

        // Restocking has to be chosen, and quarantine is what a counter does with a pack whose fate has
        // not been decided yet.
        Dispositions = [.. dispositions.Where(option => option.Disposition != ReturnDisposition.Restock)];
        SetBusinessDate(businessDate);
        SelectedDisposition = Dispositions.First(option => option.Disposition == ReturnDisposition.Quarantine);

        // Everything the customer brought back is the common case; the operator adjusts a part return down.
        Quantity = allocation.ReturnableQuantity;

        if (CanRestock)
        {
            Dispositions =
            [
                dispositions.First(option => option.Disposition == ReturnDisposition.Restock),
                .. Dispositions,
            ];
        }
    }

    public SaleLineDto Line { get; }

    public SaleAllocationDto Allocation { get; }

    public int LineNumber => Line.LineNumber;

    public string MedicineName => Line.MedicineName;

    public string BatchNumber => Allocation.BatchNumber;

    public DateOnly ExpiryDate => Allocation.ExpiryDate;

    /// <summary>How much of this batch the invoice sold, which is what a return is measured against.</summary>
    public decimal SoldQuantity => Allocation.Quantity;

    public decimal ReturnableQuantity => Allocation.ReturnableQuantity;

    public string ReturnableText => $"{ReturnableQuantity:0.###} of {SoldQuantity:0.###}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefundPreview))]
    private decimal quantity;

    [ObservableProperty]
    private string? notes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DispositionText))]
    private IReadOnlyList<ReturnDispositionOption> dispositions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Disposition))]
    private ReturnDispositionOption? selectedDisposition;

    public ReturnDisposition Disposition => SelectedDisposition?.Disposition ?? ReturnDisposition.Quarantine;

    public string DispositionText => SelectedDisposition?.Label ?? "Quarantine";

    /// <summary>True while this batch could take the stock back onto its shelf.</summary>
    public bool CanRestock => !ExpiryPolicy.IsExpired(ExpiryDate, BusinessDate);

    /// <summary>True once the batch has expired, so returning it can only remove it from circulation.</summary>
    public bool IsExpired => !CanRestock;

    public DateOnly BusinessDate { get; private set; }

    public string ExpiryText => IsExpired
        ? $"{ExpiryDate:yyyy-MM-dd} · expired"
        : $"{ExpiryDate:yyyy-MM-dd}";

    /// <summary>
    /// What this row would refund if it were recorded now. Exact for the ordinary case of one entry per
    /// invoice line; the recorded return recomputes it and is the authority.
    /// </summary>
    public decimal RefundPreview => Quantity <= 0m || _invoiceBusinessDate > BusinessDate
        ? 0m
        : Money.ShareOfLine(
            Line.LineTotal,
            Line.ReturnedAmount,
            Quantity,
            Line.Quantity,
            Line.ReturnableQuantity);

    public string RefundPreviewText => RefundPreview.ToString("N2", CultureInfo.CurrentCulture);

    /// <summary>Moves the expiry judgement onto the date the operator is working on.</summary>
    public void SetBusinessDate(DateOnly businessDate)
    {
        BusinessDate = businessDate;
        OnPropertyChanged(nameof(BusinessDate));
        OnPropertyChanged(nameof(CanRestock));
        OnPropertyChanged(nameof(IsExpired));
        OnPropertyChanged(nameof(ExpiryText));
    }

    partial void OnQuantityChanged(decimal value)
    {
        if (value > ReturnableQuantity)
        {
            Quantity = ReturnableQuantity;
            return;
        }

        if (value < 0m)
        {
            Quantity = 0m;
        }
    }
}
