using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Common;
using MediStock.Application.Configuration;
using MediStock.Application.Dashboard;
using MediStock.Application.Diagnostics;
using MediStock.Application.Sales;
using MediStock.Desktop.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Dashboard page. Every figure comes from <see cref="IDashboardQueries"/> or <see cref="ISalesQueries"/> for
/// the current business date, and the database status panel reports the Milestone 0 acceptance check.
/// </summary>
public sealed partial class DashboardViewModel : ViewModelBase
{
    private const int RecentSaleCount = 8;

    private readonly IDashboardQueries _queries;
    private readonly ISalesQueries _sales;
    private readonly ISaleCatalog _salesCatalog;
    private readonly IDatabaseHealthCheck _healthCheck;
    private readonly IClock _clock;
    private readonly INavigationService _navigation;
    private readonly ILogger<DashboardViewModel> _logger;
    private readonly int _nearExpiryDays;

    public DashboardViewModel(
        IDashboardQueries queries,
        ISalesQueries sales,
        ISaleCatalog salesCatalog,
        IDatabaseHealthCheck healthCheck,
        IClock clock,
        INavigationService navigation,
        IOptions<MediStockOptions> options,
        ILogger<DashboardViewModel> logger)
        : base("Dashboard")
    {
        _queries = queries;
        _sales = sales;
        _salesCatalog = salesCatalog;
        _healthCheck = healthCheck;
        _clock = clock;
        _navigation = navigation;
        _logger = logger;
        _nearExpiryDays = options.Value.NearExpiryDays;

        Snapshot = DashboardSnapshot.Empty(clock.BusinessDate);
        Takings = DailySalesSummary.Empty(clock.BusinessDate);
    }

    public ObservableCollection<LowStockItem> LowStock { get; } = [];

    public ObservableCollection<ExpiringBatch> ExpiringBatches { get; } = [];

    public ObservableCollection<SaleDto> RecentSales { get; } = [];

    [ObservableProperty]
    private DashboardSnapshot snapshot;

    /// <summary>Today's trading, kept separate from <see cref="Snapshot"/> so each figure has one owner.</summary>
    [ObservableProperty]
    private DailySalesSummary takings;

    [ObservableProperty]
    private DatabaseHealthReport? health;

    [ObservableProperty]
    private bool hasLoaded;

    [ObservableProperty]
    private bool hasNoLowStock;

    [ObservableProperty]
    private bool hasNoExpiringBatches;

    [ObservableProperty]
    private bool hasNoRecentSales;

    public string BusinessDateText =>
        Snapshot.BusinessDate.ToString("dddd, dd MMMM yyyy", CultureInfo.CurrentCulture);

    public string InventoryValueText =>
        Snapshot.InventoryValueAtCost.ToString("N2", CultureInfo.CurrentCulture);

    public string SellableInventoryValueText =>
        Snapshot.SellableInventoryValueAtCost.ToString("N2", CultureInfo.CurrentCulture);

    public string NonSellableInventoryValueText =>
        Snapshot.NonSellableInventoryValueAtCost.ToString("N2", CultureInfo.CurrentCulture);

    public string NearExpiryWindowText => $"within {_nearExpiryDays} days";

    public string TakingsTotalText => Takings.Total.ToString("N2", CultureInfo.CurrentCulture);

    public string TakingsMarginText => Takings.Margin.ToString("N2", CultureInfo.CurrentCulture);

    /// <summary>
    /// Today's trading. The margin card states its own formula because "margin" is the figure an operator
    /// is most likely to misread as profit.
    /// </summary>
    public IReadOnlyList<DashboardMetric> SalesMetrics =>
    [
        new(
            "Takings today",
            TakingsTotalText,
            $"Sum of the invoice totals of {Takings.SaleCount:N0} completed sale(s) on " +
            $"{Snapshot.BusinessDate:yyyy-MM-dd}. Cancelled sales are excluded."),
        new(
            "Transactions today",
            Takings.SaleCount.ToString("N0", CultureInfo.CurrentCulture),
            $"{Takings.UnitsSold:N0} unit(s) dispensed across {Takings.SaleCount:N0} invoice(s)."),
        new(
            "Gross margin today",
            TakingsMarginText,
            "Takings less tax and less the purchase cost of the batches actually sold. Excludes overheads, " +
            "shrinkage and returns, so it is an estimate, not profit."),
        new(
            "Tax collected today",
            Takings.TaxTotal.ToString("N2", CultureInfo.CurrentCulture),
            "Sum of the tax stored on today's invoice lines. Not a tax filing figure."),
    ];

    /// <summary>
    /// Headline cards. Each hint states where the number came from so an operator can trace a
    /// figure back to its query and date range.
    /// </summary>
    public IReadOnlyList<DashboardMetric> SnapshotMetrics =>
    [
        new(
            "Inventory value at cost",
            InventoryValueText,
            $"Sum of batch quantity on hand x purchase cost (specific identification). {SellableInventoryValueText} " +
            $"is sellable today; {NonSellableInventoryValueText} is expired, recalled, written off, or depleted. All batches."),
        new(
            "Active medicines",
            Snapshot.ActiveMedicineCount.ToString("N0", CultureInfo.CurrentCulture),
            $"{Snapshot.MedicineCount:N0} catalogue record(s) including inactive."),
        new(
            "Active batches",
            Snapshot.ActiveBatchCount.ToString("N0", CultureInfo.CurrentCulture),
            "Batches currently available to dispense."),
        new(
            "Low stock",
            Snapshot.LowStockCount.ToString("N0", CultureInfo.CurrentCulture),
            "Medicines at or below their reorder threshold."),
        new(
            "Near expiry",
            Snapshot.NearExpiryBatchCount.ToString("N0", CultureInfo.CurrentCulture),
            $"Batches expiring {NearExpiryWindowText}."),
        new(
            "Expired",
            Snapshot.ExpiredBatchCount.ToString("N0", CultureInfo.CurrentCulture),
            "Batches past their expiry date that still hold stock."),
    ];

    public string DatabaseStatusSummary
    {
        get
        {
            if (Health is null)
            {
                return "The database has not been probed yet.";
            }

            if (!Health.IsReachable)
            {
                return $"MediStock cannot reach the database.{(string.IsNullOrWhiteSpace(Health.Error) ? string.Empty : " " + Health.Error)}";
            }

            var summary = $"Connected to {Health.DatabaseName} on {Health.Provider} in {Health.Elapsed.TotalMilliseconds:F0} ms.";

            return Health.HasPendingMigrations
                ? $"{summary} {Health.PendingMigrations.Count} migration(s) are pending."
                : $"{summary} The schema is up to date.";
        }
    }

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ClearError();

        try
        {
            Snapshot = await _queries.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);

            var lowStock = await _queries
                .GetLowStockAsync(25, cancellationToken)
                .ConfigureAwait(true);

            LowStock.Clear();
            foreach (var item in lowStock)
            {
                LowStock.Add(item);
            }

            HasNoLowStock = LowStock.Count == 0;

            var expiring = await _queries
                .GetExpiringBatchesAsync(_nearExpiryDays, includeExpired: true, take: 25, cancellationToken)
                .ConfigureAwait(true);

            ExpiringBatches.Clear();
            foreach (var batch in expiring)
            {
                ExpiringBatches.Add(batch);
            }

            HasNoExpiringBatches = ExpiringBatches.Count == 0;

            Takings = await _sales.GetDailySummaryAsync(Snapshot.BusinessDate, cancellationToken)
                .ConfigureAwait(true);

            var recent = await _salesCatalog
                .SearchAsync(new SaleQuery { Take = RecentSaleCount }, cancellationToken)
                .ConfigureAwait(true);

            RecentSales.Clear();
            foreach (var sale in recent)
            {
                RecentSales.Add(sale);
            }

            HasNoRecentSales = RecentSales.Count == 0;

            Health = await _healthCheck.CheckAsync(cancellationToken).ConfigureAwait(true);
            HasLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the dashboard failed.");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenMedicines() => _navigation.NavigateTo(NavigationRoutes.Medicines);

    [RelayCommand]
    private void OpenInventory() => _navigation.NavigateTo(NavigationRoutes.Inventory);

    [RelayCommand]
    private void OpenReports() => _navigation.NavigateTo(NavigationRoutes.Reports);

    [RelayCommand]
    private void OpenNewSale() => _navigation.NavigateTo(NavigationRoutes.NewSale);

    [RelayCommand]
    private void OpenPurchases() => _navigation.NavigateTo(NavigationRoutes.Purchases);

    [RelayCommand]
    private void OpenSalesHistory() => _navigation.NavigateTo(NavigationRoutes.SalesHistory);

    partial void OnSnapshotChanged(DashboardSnapshot value)
    {
        OnPropertyChanged(nameof(BusinessDateText));
        OnPropertyChanged(nameof(InventoryValueText));
        OnPropertyChanged(nameof(SnapshotMetrics));
        OnPropertyChanged(nameof(SalesMetrics));
    }

    partial void OnTakingsChanged(DailySalesSummary value)
    {
        OnPropertyChanged(nameof(TakingsTotalText));
        OnPropertyChanged(nameof(TakingsMarginText));
        OnPropertyChanged(nameof(SalesMetrics));
    }

    partial void OnHealthChanged(DatabaseHealthReport? value) => OnPropertyChanged(nameof(DatabaseStatusSummary));
}

/// <summary>One headline figure on the dashboard, with a traceable explanation.</summary>
public sealed record DashboardMetric(string Label, string Value, string Hint);
