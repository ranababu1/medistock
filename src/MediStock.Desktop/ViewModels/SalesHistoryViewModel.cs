using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Common;
using MediStock.Application.Sales;
using MediStock.Desktop.Services;
using MediStock.Domain.Sales;
using Microsoft.Extensions.Logging;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Sales history. Completed invoices are historical records: this screen reads them, reprints them and
/// never changes them.
/// </summary>
public sealed partial class SalesHistoryViewModel : ViewModelBase
{
    private readonly ISaleCatalog _sales;
    private readonly IReceiptExporter _receipts;
    private readonly IClock _clock;
    private readonly ILogger<SalesHistoryViewModel> _logger;

    /// <summary>Whether the operator has chosen a range of their own, which must survive re-visits.</summary>
    private bool _operatorPinnedDates;

    private bool _refreshingDefaultDates;

    public SalesHistoryViewModel(
        ISaleCatalog sales,
        IReceiptExporter receipts,
        IClock clock,
        ILogger<SalesHistoryViewModel> logger)
        : base("Sales History")
    {
        ArgumentNullException.ThrowIfNull(clock);

        _sales = sales;
        _receipts = receipts;
        _clock = clock;
        _logger = logger;

        StatusFilters =
        [
            new("All statuses", null),
            new("Completed", SaleStatus.Completed),
            new("Returned", SaleStatus.Returned),
            new("Cancelled", SaleStatus.Cancelled),
        ];

        PaymentFilters =
        [
            new("Any payment method", null),
            new("Cash", PaymentMethod.Cash),
            new("Card", PaymentMethod.Card),
            new("UPI", PaymentMethod.Upi),
            new("Other", PaymentMethod.Other),
        ];

        SelectedStatusFilter = StatusFilters[0];
        SelectedPaymentFilter = PaymentFilters[0];
        From = clock.BusinessDate;
        To = clock.BusinessDate;

        PropertyChanged += OnOwnPropertyChanged;
    }

    public ObservableCollection<SaleDto> Sales { get; } = [];

    public IReadOnlyList<SaleStatusFilter> StatusFilters { get; }

    public IReadOnlyList<PaymentMethodFilter> PaymentFilters { get; }

    [ObservableProperty]
    private string searchTerm = string.Empty;

    [ObservableProperty]
    private DateOnly? from;

    [ObservableProperty]
    private DateOnly? to;

    [ObservableProperty]
    private SaleStatusFilter? selectedStatusFilter;

    [ObservableProperty]
    private PaymentMethodFilter? selectedPaymentFilter;

    [ObservableProperty]
    private SaleDto? selectedSale;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private bool hasSearched;

    public bool IsNotBusy => !IsBusy;

    public bool HasNoResults => HasSearched && Sales.Count == 0;

    public bool HasSelection => SelectedSale is not null;

    public string ResultSummary => Sales.Count switch
    {
        0 => "No invoices match the current filters.",
        1 => "1 invoice",
        _ => $"{Sales.Count} invoices",
    };

    public string ListedTotalText =>
        Sales.Where(sale => sale.Status != SaleStatus.Cancelled)
            .Sum(sale => sale.Total)
            .ToString("N2", CultureInfo.CurrentCulture);

    public string DetailHeading =>
        SelectedSale is null ? "Select an invoice" : $"Invoice {SelectedSale.InvoiceNumber}";

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        RefreshDefaultDates();
        await SearchAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Restores the "today" defaults before a search, unless the operator has pinned a range. The page
    /// lives for the whole session, so a range left overnight must not keep reporting yesterday as
    /// today — but a range the operator chose is theirs and survives a trip to another page.
    /// </summary>
    private void RefreshDefaultDates()
    {
        if (_operatorPinnedDates)
        {
            return;
        }

        _refreshingDefaultDates = true;
        try
        {
            From = _clock.BusinessDate;
            To = _clock.BusinessDate;
        }
        finally
        {
            _refreshingDefaultDates = false;
        }
    }

    partial void OnFromChanged(DateOnly? value) => PinDates();

    partial void OnToChanged(DateOnly? value) => PinDates();

    private void PinDates()
    {
        if (!_refreshingDefaultDates)
        {
            _operatorPinnedDates = true;
        }
    }

    /// <summary>Runs the search behind the filters and replaces the list on screen.</summary>
    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        ClearError();
        IsBusy = true;

        try
        {
            var results = await _sales
                .SearchAsync(
                    new SaleQuery
                    {
                        Term = SearchTerm,
                        FromBusinessDate = From,
                        ToBusinessDate = To,
                        Status = SelectedStatusFilter?.Status,
                        PaymentMethod = SelectedPaymentFilter?.Method,
                        Take = 500,
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            Sales.Clear();
            foreach (var sale in results)
            {
                Sales.Add(sale);
            }

            HasSearched = true;
            NotifyHistoryChanged();

            if (SelectedSale is not null)
            {
                SelectedSale = Sales.FirstOrDefault(sale => sale.Id == SelectedSale.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Searching sales history failed.");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes a fresh copy of the stored invoice. Nothing about the sale changes.</summary>
    [RelayCommand]
    private async Task ReprintAsync(CancellationToken cancellationToken)
    {
        if (SelectedSale is null)
        {
            return;
        }

        ClearError();
        StatusMessage = null;

        var sale = SelectedSale;

        try
        {
            var export = await _receipts.ExportAsync(sale, cancellationToken).ConfigureAwait(true);
            StatusMessage = $"Reprinted {sale.InvoiceNumber} to {export.FilePath}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reprinting invoice {Invoice} failed.", sale.InvoiceNumber);
            SetError(ex);
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchTerm = string.Empty;
        From = null;
        To = null;
        SelectedStatusFilter = StatusFilters[0];
        SelectedPaymentFilter = PaymentFilters[0];
        ClearError();
    }

    partial void OnSelectedSaleChanged(SaleDto? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(DetailHeading));
    }

    partial void OnHasSearchedChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));

    /// <summary>
    /// The list is replaced as a whole, so the figures derived from it have to be read again rather than
    /// left as they were when the screen was still empty.
    /// </summary>
    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(ListedTotalText));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(DetailHeading));
    }

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(IsBusy))
        {
            return;
        }

        OnPropertyChanged(nameof(IsNotBusy));
    }
}

/// <summary>One choice in the sales-history status filter.</summary>
public sealed record SaleStatusFilter(string Label, SaleStatus? Status);

/// <summary>One choice in the sales-history payment filter.</summary>
public sealed record PaymentMethodFilter(string Label, PaymentMethod? Method);
