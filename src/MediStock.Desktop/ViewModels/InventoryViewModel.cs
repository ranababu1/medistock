using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Common;
using MediStock.Application.Inventory;
using MediStock.Domain.Inventory;
using Microsoft.Extensions.Logging;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Inventory and batches. The batch list is the shelf as it stands today; the movement panel is the
/// ledger that produced it. The reconciliation check compares the two, because a cached balance that
/// disagrees with its movements is a defect worth surfacing rather than hiding.
/// </summary>
public sealed partial class InventoryViewModel : ViewModelBase
{
    private readonly IStockQueries _stock;
    private readonly IClock _clock;
    private readonly ILogger<InventoryViewModel> _logger;

    public InventoryViewModel(
        IStockQueries stock,
        IClock clock,
        ILogger<InventoryViewModel> logger)
        : base("Inventory & Batches")
    {
        _stock = stock;
        _clock = clock;
        _logger = logger;

        StatusFilters =
        [
            new("All statuses", null),
            new("Active", BatchStatus.Active),
            new("Depleted", BatchStatus.Depleted),
            new("Expired", BatchStatus.Expired),
            new("Written off", BatchStatus.WrittenOff),
            new("Recalled", BatchStatus.Recalled),
        ];

        var today = _clock.BusinessDate;

        ExpiryFilters =
        [
            new("Any expiry date", null),
            new("Expiring within 30 days", today.AddDays(30)),
            new("Expiring within 90 days", today.AddDays(90)),
            new("Expired already", today.AddDays(-1)),
        ];

        SelectedStatusFilter = StatusFilters[0];
        SelectedExpiryFilter = ExpiryFilters[0];

        PropertyChanged += OnOwnPropertyChanged;
    }

    public ObservableCollection<BatchStockDto> Batches { get; } = [];

    public ObservableCollection<StockMovementDto> Movements { get; } = [];

    public IReadOnlyList<BatchStatusFilterOption> StatusFilters { get; }

    public IReadOnlyList<ExpiryFilterOption> ExpiryFilters { get; }

    [ObservableProperty]
    private string searchTerm = string.Empty;

    [ObservableProperty]
    private BatchStatusFilterOption? selectedStatusFilter;

    [ObservableProperty]
    private ExpiryFilterOption? selectedExpiryFilter;

    [ObservableProperty]
    private bool inStockOnly = true;

    [ObservableProperty]
    private BatchStockDto? selectedBatch;

    [ObservableProperty]
    private bool hasSearched;

    [ObservableProperty]
    private string? reconciliationMessage;

    [ObservableProperty]
    private bool? isReconciled;

    public bool IsNotBusy => !IsBusy;

    public bool HasNoResults => HasSearched && Batches.Count == 0;

    public bool HasReconciliationRun => IsReconciled is not null;

    public string ResultSummary => Batches.Count switch
    {
        0 => "No batches match the current filters.",
        1 => "1 batch",
        _ => $"{Batches.Count} batches",
    };

    public string BatchCountText => Batches.Count.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Everything on the shelf at cost, sellable or not.</summary>
    public string StockValueText => Sum(batch => batch.ValueAtCost);

    /// <summary>Only the stock that may actually be dispensed today.</summary>
    public string SellableValueText => Sum(batch => batch.IsSellable ? batch.ValueAtCost : 0m);

    public string ExpiringSoonText => Batches
        .Count(batch => batch.IsSellable && batch.DaysUntilExpiry <= 90)
        .ToString("N0", CultureInfo.CurrentCulture);

    public string UnavailableText => Batches
        .Count(batch => batch.QuantityOnHand > 0m && !batch.IsSellable)
        .ToString("N0", CultureInfo.CurrentCulture);

    public string MovementHeading => SelectedBatch is { } batch
        ? $"Movements for {batch.MedicineName} · {batch.BatchNumber}"
        : "Recent movements across every batch";

    public string MovementSummary => Movements.Count switch
    {
        0 => "No movements recorded for this selection.",
        1 => "1 movement",
        _ => $"{Movements.Count} movements",
    };

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await SearchAsync(cancellationToken).ConfigureAwait(true);
        await RunReconciliationAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ClearError();

        try
        {
            var results = await _stock
                .ListBatchesAsync(
                    new BatchStockQuery
                    {
                        Term = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim(),
                        Status = SelectedStatusFilter?.Status,
                        ExpiringOnOrBefore = SelectedExpiryFilter?.ExpiringOnOrBefore,
                        InStockOnly = InStockOnly,
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            var previousBatchId = SelectedBatch?.BatchId;

            Batches.Clear();
            foreach (var batch in results)
            {
                Batches.Add(batch);
            }

            HasSearched = true;
            RaiseSummary();

            SelectedBatch = previousBatchId is null
                ? Batches.FirstOrDefault()
                : Batches.FirstOrDefault(b => b.BatchId == previousBatchId) ?? Batches.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the batch list failed.");
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
        SelectedExpiryFilter = ExpiryFilters[0];
        InStockOnly = true;
        return SearchAsync(CancellationToken.None);
    }

    /// <summary>Compares every cached batch balance with the sum of its ledger entries.</summary>
    [RelayCommand]
    private async Task RunReconciliationAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ClearError();

        try
        {
            var summary = await _stock.CheckReconciliationAsync(cancellationToken).ConfigureAwait(true);

            IsReconciled = summary.IsReconciled;
            ReconciliationMessage = summary.IsReconciled
                ? $"OK — all {summary.BatchesChecked} batches agree with their movement history " +
                  $"({summary.LedgerQuantity:N3} units on the ledger)."
                : $"{summary.BatchesMismatched} of {summary.BatchesChecked} batches disagree with their " +
                  $"movement history. Ledger {summary.LedgerQuantity:N3} vs cached {summary.CachedQuantity:N3} " +
                  "units. Investigate before trusting these balances.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconciling stock balances failed.");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedBatchChanged(BatchStockDto? value)
    {
        OnPropertyChanged(nameof(MovementHeading));
        _ = LoadMovementsAsync(value);
    }

    partial void OnIsReconciledChanged(bool? value)
    {
        OnPropertyChanged(nameof(HasReconciliationRun));
    }

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy))
        {
            OnPropertyChanged(nameof(IsNotBusy));
        }
    }

    private async Task LoadMovementsAsync(BatchStockDto? batch)
    {
        try
        {
            var movements = await _stock
                .ListMovementsAsync(
                    new StockMovementQuery
                    {
                        MedicineBatchId = batch?.BatchId,
                        Take = batch is null ? 100 : 200,
                    })
                .ConfigureAwait(true);

            Movements.Clear();
            foreach (var movement in movements)
            {
                Movements.Add(movement);
            }

            OnPropertyChanged(nameof(MovementSummary));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading stock movements failed.");
            SetError(ex);
        }
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(BatchCountText));
        OnPropertyChanged(nameof(StockValueText));
        OnPropertyChanged(nameof(SellableValueText));
        OnPropertyChanged(nameof(ExpiringSoonText));
        OnPropertyChanged(nameof(UnavailableText));
    }

    private string Sum(Func<BatchStockDto, decimal> selector) =>
        Batches.Sum(selector).ToString("N2", CultureInfo.CurrentCulture);
}

/// <summary>One option in the batch status filter.</summary>
public sealed record BatchStatusFilterOption(string Title, BatchStatus? Status)
{
    public override string ToString() => Title;
}

/// <summary>One option in the expiry filter. The date is the last expiry date still included.</summary>
public sealed record ExpiryFilterOption(string Title, DateOnly? ExpiringOnOrBefore)
{
    public override string ToString() => Title;
}
