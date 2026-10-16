using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Catalogue;
using MediStock.Application.Common;
using MediStock.Application.Sales;
using MediStock.Desktop.Services;
using MediStock.Domain.Sales;
using Microsoft.Extensions.Logging;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// The point of sale. A basket is assembled, priced by <see cref="ISaleCatalog.QuoteAsync"/> and then
/// committed by <see cref="ISaleCatalog.CheckoutAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The screen never computes a total of its own. Every figure shown comes from the quote, so the price
/// on the screen and the price on the invoice are produced by the same code. The quote is also what
/// shows which batches first-expiry-first-out would consume, before any money changes hands.
/// </para>
/// <para>
/// A refused checkout leaves the basket exactly as it was, with the reasons in the error banner, so the
/// operator can fix the offending line rather than re-key the customer's whole order.
/// </para>
/// </remarks>
public sealed partial class NewSaleViewModel : ViewModelBase
{
    private readonly IMedicineCatalog _medicines;
    private readonly ISaleCatalog _sales;
    private readonly IReceiptExporter _receipts;
    private readonly IDialogService _dialogs;
    private readonly ILogger<NewSaleViewModel> _logger;

    private CancellationTokenSource? _quoteCancellation;
    private SaleDto? _lastSale;

    public NewSaleViewModel(
        IMedicineCatalog medicines,
        ISaleCatalog sales,
        IReceiptExporter receipts,
        IDialogService dialogs,
        ILogger<NewSaleViewModel> logger)
        : base("New Sale")
    {
        _medicines = medicines;
        _sales = sales;
        _receipts = receipts;
        _dialogs = dialogs;
        _logger = logger;

        PaymentMethods =
        [
            new(PaymentMethod.Cash, "Cash"),
            new(PaymentMethod.Card, "Card"),
            new(PaymentMethod.Upi, "UPI"),
            new(PaymentMethod.Other, "Other"),
        ];

        SelectedPaymentMethod = PaymentMethods[0];

        // The busy flag lives on the base type, so its changes are observed here rather than by
        // implementing a partial method, which is only possible inside the declaring class.
        PropertyChanged += OnOwnPropertyChanged;
    }

    public ObservableCollection<CartLineViewModel> Cart { get; } = [];

    public ObservableCollection<MedicineDto> SearchResults { get; } = [];

    public ObservableCollection<PaymentDraft> Payments { get; } = [];

    public IReadOnlyList<PaymentMethodOption> PaymentMethods { get; }

    [ObservableProperty]
    private string scanText = string.Empty;

    [ObservableProperty]
    private string soldBy = Environment.UserName;

    [ObservableProperty]
    private string customerName = string.Empty;

    [ObservableProperty]
    private string customerPhone = string.Empty;

    [ObservableProperty]
    private string notes = string.Empty;

    [ObservableProperty]
    private SaleQuote? quote;

    [ObservableProperty]
    private bool isQuoting;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? lastReceiptPath;

    [ObservableProperty]
    private PaymentMethodOption? selectedPaymentMethod;

    /// <summary>Amount for the payment being entered. Defaults to the outstanding balance.</summary>
    [ObservableProperty]
    private decimal paymentAmount;

    [ObservableProperty]
    private string paymentReference = string.Empty;

    [ObservableProperty]
    private bool hasSearched;

    [ObservableProperty]
    private MedicineDto? selectedSearchResult;

    public bool HasNoSearchResults => HasSearched && SearchResults.Count == 0;

    public bool IsCartEmpty => Cart.Count == 0;

    public bool IsNotBusy => !IsBusy;

    public string InvoicePreview => Quote?.InvoicePreview ?? "—";

    public string CartSummary => Cart.Count switch
    {
        0 => "The basket is empty.",
        1 => "1 line in the basket.",
        _ => $"{Cart.Count} lines in the basket.",
    };

    public string SubTotalText => Money(Quote?.SubTotal ?? 0m);

    public string DiscountTotalText => Money(Quote?.DiscountTotal ?? 0m);

    public string TaxableTotalText => Money(Quote?.TaxableTotal ?? 0m);

    public string TaxTotalText => Money(Quote?.TaxTotal ?? 0m);

    public string TotalText => Money(Quote?.Total ?? 0m);

    public string TotalPaidText => Money(TotalPaid);

    public string BalanceText => Money(BalanceDue);

    /// <summary>Sum of the settlement entered so far, including the amount in the payment box.</summary>
    public decimal TotalPaid =>
        Payments.Sum(payment => payment.Amount) + (Payments.Count == 0 ? PendingPaymentAmount : 0m);

    public decimal BalanceDue => Math.Round((Quote?.Total ?? 0m) - TotalPaid, 2, MidpointRounding.AwayFromZero);

    /// <summary>Plain-language reasons the basket cannot be settled, from the quote plus the till's own checks.</summary>
    public IReadOnlyList<string> Blockers =>
    [
        .. Quote?.Blockers ?? [],
        .. SettlementBlockers,
    ];

    private IReadOnlyList<string> SettlementBlockers
    {
        get
        {
            var blockers = new List<string>();

            if (Cart.Count == 0)
            {
                blockers.Add("Add at least one medicine to the basket.");
            }

            if (string.IsNullOrWhiteSpace(SoldBy))
            {
                blockers.Add("Enter the name of the operator making the sale.");
            }

            if (TotalPaid <= 0m)
            {
                blockers.Add("Enter how the customer is paying.");
            }
            else if (BalanceDue != 0m)
            {
                // Tender-and-change is a till concern; the invoice must record exactly what was charged.
                blockers.Add(BalanceDue > 0m
                    ? $"The settlement is short by {Money(BalanceDue)}."
                    : $"The settlement exceeds the total by {Money(-BalanceDue)}.");
            }

            return blockers;
        }
    }

    public bool CanSettle => !IsBusy && Blockers.Count == 0;

    public override Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Adds the first match to the basket. A barcode scanner types a code and presses Enter, so this
    /// path has to work without the operator ever looking at the search results.
    /// </summary>
    [RelayCommand]
    private async Task AddByCodeOrTermAsync(CancellationToken cancellationToken)
    {
        var term = ScanText.Trim();
        ClearError();

        if (term.Length == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var matches = await _medicines
                .SearchAsync(new MedicineSearchQuery { Term = term, Take = 50 }, cancellationToken)
                .ConfigureAwait(true);

            SearchResults.Clear();
            foreach (var match in matches)
            {
                SearchResults.Add(match);
            }

            HasSearched = true;

            if (matches.Count == 1)
            {
                Add(matches[0]);
                ScanText = string.Empty;
                return;
            }

            var exact = matches.FirstOrDefault(match =>
                string.Equals(match.Barcode, term, StringComparison.OrdinalIgnoreCase));

            if (exact is not null)
            {
                Add(exact);
                ScanText = string.Empty;
                return;
            }

            ErrorMessage = matches.Count == 0
                ? $"No medicine matched '{term}'. Check the spelling, or add it to the catalogue first."
                : $"{matches.Count} medicines match '{term}'. Pick the right one from the list.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Searching the catalogue for {Term} failed.", term);
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AddSelected()
    {
        if (SelectedSearchResult is null)
        {
            return;
        }

        ClearError();
        Add(SelectedSearchResult);
        SelectedSearchResult = null;
        ScanText = string.Empty;
        SearchResults.Clear();
        HasSearched = false;
    }

    [RelayCommand]
    private void RemoveLine(CartLineViewModel? line)
    {
        if (line is null || !Cart.Remove(line))
        {
            return;
        }

        Detach(line);
        QueueQuoteRefresh();
    }

    [RelayCommand]
    private void ClearCart()
    {
        if (Cart.Count == 0)
        {
            return;
        }

        if (!_dialogs.Confirm("Empty the basket", $"Remove all {Cart.Count} line(s) from the basket?"))
        {
            return;
        }

        foreach (var line in Cart)
        {
            Detach(line);
        }

        Cart.Clear();
        ClearError();
        QueueQuoteRefresh();
    }

    [RelayCommand]
    private void AddPayment()
    {
        ClearError();

        var amount = PendingPaymentAmount;

        if (amount <= 0m)
        {
            ErrorMessage = "Enter the amount being paid before adding it to the settlement.";
            return;
        }

        Payments.Add(new PaymentDraft
        {
            Method = SelectedPaymentMethod?.Method ?? PaymentMethod.Cash,
            Amount = amount,
            Reference = string.IsNullOrWhiteSpace(PaymentReference) ? null : PaymentReference.Trim(),
        });

        // Clearing the box is what stops the next keystroke from being added twice by accident.
        PaymentAmount = 0m;
        PaymentReference = string.Empty;
        NotifySettlementChanged();
    }

    [RelayCommand]
    private void RemovePayment(PaymentDraft? payment)
    {
        if (payment is null || !Payments.Remove(payment))
        {
            return;
        }

        ClearError();
        NotifySettlementChanged();
    }

    /// <summary>
    /// Commits the sale. On any refusal the basket, the customer details and the settlement are left
    /// untouched, because the alternative is re-keying an order the customer is standing over.
    /// </summary>
    [RelayCommand]
    private async Task SettleAsync(CancellationToken cancellationToken)
    {
        ClearError();
        StatusMessage = null;

        if (Cart.Count == 0)
        {
            ErrorMessage = "Add at least one medicine to the basket.";
            return;
        }

        // Re-price immediately before asking, so the confirmation shows a figure that is still true.
        await RefreshQuoteAsync(cancellationToken).ConfigureAwait(true);

        if (Blockers.Count > 0)
        {
            ErrorMessage = string.Join(Environment.NewLine, Blockers);
            return;
        }

        var total = Quote?.Total ?? 0m;

        var confirmed = _dialogs.Confirm(
            "Take payment",
            $"Charge {Money(total)} for {Cart.Count} line(s) and complete the sale?" +
            $"{Environment.NewLine}{Environment.NewLine}" +
            "This writes the invoice, reduces the batch quantities and cannot be undone from this screen. " +
            "A correction is a recorded return, not a deletion.");

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var draft = ToDraft();
            var result = await _sales.CheckoutAsync(draft, cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                await RefreshQuoteAsync(cancellationToken).ConfigureAwait(true);
                return;
            }

            var sale = result.Value!;
            _lastSale = sale;
            StatusMessage =
                $"Invoice {sale.InvoiceNumber} completed for {Money(sale.Total)}.";

            await ExportReceiptAsync(sale, cancellationToken).ConfigureAwait(true);

            ResetAfterSale();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking out the basket failed.");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReprintLastAsync(CancellationToken cancellationToken)
    {
        if (_lastSale is null)
        {
            ErrorMessage = "No sale has been completed in this session yet.";
            return;
        }

        ClearError();
        await ExportReceiptAsync(_lastSale, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private void NewBasket()
    {
        if (Cart.Count > 0 && !_dialogs.Confirm("Start a new basket", "Discard the basket in progress?"))
        {
            return;
        }

        foreach (var line in Cart)
        {
            Detach(line);
        }

        Cart.Clear();
        ResetAfterSale();
    }

    partial void OnScanTextChanged(string value) => ClearError();

    partial void OnSelectedPaymentMethodChanged(PaymentMethodOption? value) => NotifySettlementChanged();

    partial void OnPaymentReferenceChanged(string value) => NotifySettlementChanged();

    partial void OnPaymentAmountChanged(decimal value) => NotifySettlementChanged();

    partial void OnQuoteChanged(SaleQuote? value)
    {
        OnPropertyChanged(nameof(InvoicePreview));
        OnPropertyChanged(nameof(SubTotalText));
        OnPropertyChanged(nameof(DiscountTotalText));
        OnPropertyChanged(nameof(TaxableTotalText));
        OnPropertyChanged(nameof(TaxTotalText));
        OnPropertyChanged(nameof(TotalText));
        NotifySettlementChanged();
    }

    partial void OnHasSearchedChanged(bool value) => OnPropertyChanged(nameof(HasNoSearchResults));

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(IsBusy))
        {
            return;
        }

        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanSettle));
    }

    private void Add(MedicineDto medicine)
    {
        if (Cart.Count >= SaleLineDraft.MaxLines)
        {
            ErrorMessage = $"A basket can hold at most {SaleLineDraft.MaxLines} lines.";
            return;
        }

        // Ringing up the same medicine twice is normal — a customer can buy two strengths — so this
        // appends a separate line rather than merging, which would silently average two prices.
        var line = new CartLineViewModel(medicine);
        line.PropertyChanged += OnCartLineChanged;
        Cart.Add(line);

        OnPropertyChanged(nameof(IsCartEmpty));
        OnPropertyChanged(nameof(CartSummary));
        QueueQuoteRefresh();
    }

    private void Detach(CartLineViewModel line) => line.PropertyChanged -= OnCartLineChanged;

    private void OnCartLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CartLineViewModel.QuotedAllocation)
            or nameof(CartLineViewModel.QuotedLineTotal)
            or nameof(CartLineViewModel.QuotedProblem))
        {
            return;
        }

        QueueQuoteRefresh();
    }

    /// <summary>
    /// Re-prices the basket. A newer request cancels the one in flight, so a fast typist cannot be shown
    /// the totals of a basket they have already changed.
    /// </summary>
    private void QueueQuoteRefresh()
    {
        _quoteCancellation?.Cancel();
        _quoteCancellation?.Dispose();

        var cancellation = new CancellationTokenSource();
        _quoteCancellation = cancellation;

        _ = RefreshQuoteAsync(cancellation.Token);
    }

    private async Task RefreshQuoteAsync(CancellationToken cancellationToken)
    {
        if (Cart.Count == 0)
        {
            // Nothing there is nothing to quote: showing an invoice number or a "basket is empty"
            // blocker for an empty cart is noise, and the quote round trip is a waste.
            Quote = null;
            OnPropertyChanged(nameof(IsCartEmpty));
            OnPropertyChanged(nameof(CartSummary));
            return;
        }

        IsQuoting = true;

        try
        {
            var quote = await _sales.QuoteAsync(ToDraft(), cancellationToken).ConfigureAwait(true);

            cancellationToken.ThrowIfCancellationRequested();

            Quote = quote;
            ApplyQuoteToCart(quote);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer basket; the newer request owns the screen.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pricing the basket failed.");
            SetError(ex);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsQuoting = false;
            }
        }
    }

    /// <summary>
    /// Shows each line what it will cost and which batch it will consume. A line the quote could not
    /// price keeps its own message, so a single bad line does not hide the rest of the basket.
    /// </summary>
    private void ApplyQuoteToCart(SaleQuote quote)
    {
        foreach (var line in Cart)
        {
            var priced = quote.Lines.FirstOrDefault(candidate =>
                candidate.MedicineId == line.MedicineId
                && Math.Abs(candidate.Quantity - line.Quantity) < 0.0005m);

            if (priced is null)
            {
                var blocked = quote.Blockers.FirstOrDefault(blocker =>
                    blocker.Contains(line.MedicineName, StringComparison.OrdinalIgnoreCase));

                line.QuotedLineTotal = 0m;
                line.QuotedAllocation = null;
                line.QuotedProblem = blocked ?? "This line could not be priced.";
                continue;
            }

            line.QuotedLineTotal = priced.LineTotal;
            line.QuotedAllocation = string.Join(
                ", ",
                priced.Allocations.Select(allocation =>
                    $"{allocation.BatchNumber} x{allocation.Quantity:0.###} (exp {allocation.ExpiryDate:MM/yyyy})"));
            line.QuotedProblem = null;
        }

        NotifySettlementChanged();
    }

    private SaleDraft ToDraft()
    {
        var draft = new SaleDraft
        {
            SoldBy = SoldBy.Trim(),
            CustomerName = CustomerName,
            CustomerPhone = CustomerPhone,
            Notes = Notes,
            Lines = [.. Cart.Select(line => line.ToDraft())],
            Payments = Payments.Count > 0
                ? [.. Payments]
                : PendingPaymentAmount > 0m
                    ? [new PaymentDraft
                    {
                        Method = SelectedPaymentMethod?.Method ?? PaymentMethod.Cash,
                        Amount = PendingPaymentAmount,
                        Reference = string.IsNullOrWhiteSpace(PaymentReference) ? null : PaymentReference.Trim(),
                    }]
                    : [],
        };

        return draft.Normalise();
    }

    /// <summary>
    /// The payment shown in the entry box. Before anything is added to the settlement this is the amount
    /// the operator typed, or the whole balance when they typed nothing — the common case.
    /// </summary>
    private decimal PendingPaymentAmount => PaymentAmount > 0m ? PaymentAmount : Math.Max(BalanceDueExceptPending, 0m);

    private decimal BalanceDueExceptPending
        => Math.Round(
            (Quote?.Total ?? 0m) - Payments.Sum(payment => payment.Amount),
            2,
            MidpointRounding.AwayFromZero);

    private async Task ExportReceiptAsync(SaleDto sale, CancellationToken cancellationToken)
    {
        try
        {
            var export = await _receipts.ExportAsync(sale, cancellationToken).ConfigureAwait(true);
            LastReceiptPath = export.FilePath;
            StatusMessage = $"{StatusMessage} Receipt written to {export.FilePath}.";
        }
        catch (Exception ex)
        {
            // The sale is committed; a failed print must not look like a failed sale.
            _logger.LogError(ex, "Writing the receipt for invoice {Invoice} failed.", sale.InvoiceNumber);
            ErrorMessage =
                $"Invoice {sale.InvoiceNumber} was completed, but its receipt could not be written: {ex.Message}";
        }
    }

    private void ResetAfterSale()
    {
        foreach (var line in Cart)
        {
            Detach(line);
        }

        Cart.Clear();
        Payments.Clear();
        Quote = null;
        CustomerName = string.Empty;
        CustomerPhone = string.Empty;
        Notes = string.Empty;
        PaymentAmount = 0m;
        PaymentReference = string.Empty;
        SearchResults.Clear();
        HasSearched = false;

        OnPropertyChanged(nameof(IsCartEmpty));
        OnPropertyChanged(nameof(CartSummary));
        NotifySettlementChanged();
    }

    private void NotifySettlementChanged()
    {
        OnPropertyChanged(nameof(TotalPaid));
        OnPropertyChanged(nameof(TotalPaidText));
        OnPropertyChanged(nameof(BalanceText));
        OnPropertyChanged(nameof(Blockers));
        OnPropertyChanged(nameof(CanSettle));
    }

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);
}

/// <summary>One choice in the payment-method picker.</summary>
public sealed record PaymentMethodOption(PaymentMethod Method, string Label);
