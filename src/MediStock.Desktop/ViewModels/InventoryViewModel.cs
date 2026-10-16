using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Common;
using MediStock.Application.Identity;
using MediStock.Application.Inventory;
using MediStock.Application.Reporting;
using MediStock.Desktop.Services;
using MediStock.Domain.Identity;
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
    private readonly IInventoryOperations _corrections;
    private readonly IDialogService _dialogs;
    private readonly IUserSession _session;
    private readonly IClock _clock;
    private readonly ILogger<InventoryViewModel> _logger;

    public InventoryViewModel(
        IStockQueries stock,
        IInventoryOperations corrections,
        IDialogService dialogs,
        IUserSession session,
        IClock clock,
        ILogger<InventoryViewModel> logger)
        : base("Inventory & Batches")
    {
        _stock = stock;
        _corrections = corrections;
        _dialogs = dialogs;
        _session = session;
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

    // ---------------------------------------------------------------- manual corrections

    /// <summary>What the last correction did, so the operator sees it landed without hunting for the row.</summary>
    [ObservableProperty]
    private string? correctionMessage;

    /// <summary>True when this operator may adjust, write off or quarantine stock.</summary>
    public bool CanCorrectStock => _session.Can(Permission.AdjustStock);

    /// <summary>True when the corrections strip must explain itself instead of showing its buttons.</summary>
    public bool CannotCorrectStock => !CanCorrectStock;

    /// <summary>The note shown instead of the correction buttons when the operator may not act.</summary>
    public string CorrectionRefusalNote =>
        PermissionSet.Explain(Permission.AdjustStock, "correct batch counts, write stock off or hold it back");

    /// <summary>
    /// Adjusting needs a batch that can still move stock: one with nothing on it can take an increase, but
    /// recalled and written-off batches are frozen by the domain, so their counts cannot be corrected.
    /// </summary>
    public bool CanAdjustSelectedBatch =>
        CanCorrectStock && SelectedBatch is { Status: not (BatchStatus.Recalled or BatchStatus.WrittenOff) };

    /// <summary>Disposing or holding back needs a batch that still has quantity on it.</summary>
    public bool CanDisposeSelectedBatch => CanCorrectStock && SelectedBatch is { QuantityOnHand: > 0m };

    /// <summary>The batch the correction buttons would act on, named for the strip's heading.</summary>
    public string SelectedBatchCorrectionHeading => SelectedBatch is { } batch
        ? $"Correct {batch.MedicineName} · {batch.BatchNumber}"
        : "Select a batch to correct";

    /// <summary>Where the selected batch stands, so the operator types the difference against a stated figure.</summary>
    public string SelectedBatchCorrectionSummary => SelectedBatch switch
    {
        null => "Pick a batch above, then adjust its count, write it off, or quarantine it here.",
        { Status: BatchStatus.Recalled } batch =>
            $"{batch.QuantityOnHand:0.###} on hand · {batch.PurchaseCost:N2} cost · {batch.Status}. "
            + "The stock is held out of every sale; write the batch off once the goods are actually destroyed.",
        { Status: BatchStatus.WrittenOff } batch =>
            $"{batch.QuantityOnHand:0.###} on hand · {batch.PurchaseCost:N2} cost · {batch.Status}. "
            + "The batch is disposed; its record stays for the audit trail and nothing on it can move.",
        { } batch =>
            $"{batch.QuantityOnHand:0.###} on hand · {batch.PurchaseCost:N2} cost · {batch.Status}. "
            + "Adjust the count when the shelf disagrees with the ledger; write stock off to dispose of the "
            + "whole batch; quarantine to hold it out of sale pending disposal.",
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
        OnPropertyChanged(nameof(SelectedBatchCorrectionHeading));
        OnPropertyChanged(nameof(SelectedBatchCorrectionSummary));
        OnPropertyChanged(nameof(CanAdjustSelectedBatch));
        OnPropertyChanged(nameof(CanDisposeSelectedBatch));
        _ = LoadMovementsAsync(value);
    }

    // ---------------------------------------------------------------- corrections: adjust

    /// <summary>
    /// Asks for the difference between the counted shelf and the ledger, then books it as one adjustment
    /// carrying the reason. The catalog re-reads the batch inside its transaction, so a stale screen cannot
    /// adjust a batch that has changed since it was listed.
    /// </summary>
    [RelayCommand]
    private async Task AdjustCountAsync(CancellationToken cancellationToken)
    {
        ClearError();
        CorrectionMessage = null;

        var batch = SelectedBatch;

        if (batch is null)
        {
            ErrorMessage = "Choose the batch whose count is being corrected first.";
            return;
        }

        var typed = _dialogs.Prompt(
            "Adjust the count",
            $"{batch.MedicineName} · {batch.BatchNumber} — the ledger says {batch.QuantityOnHand:0.###} on hand.{Environment.NewLine}{Environment.NewLine}"
                + "Enter the difference between the count and the ledger: 12 to add stock the ledger never "
                + "recorded, or -4 to remove stock it still claims.",
            string.Empty);

        if (typed is null)
        {
            return;
        }

        if (!TryReadSignedQuantity(typed, out var delta) || delta == 0m)
        {
            ErrorMessage = $"'{typed}' is not a quantity change. Enter a number such as 12 to add or -4 to remove.";
            return;
        }

        var reason = _dialogs.Prompt(
            "Reason for the adjustment",
            "Why does the ledger disagree with the shelf? The reason is recorded on the movement and in the audit trail.",
            string.Empty);

        if (string.IsNullOrWhiteSpace(reason))
        {
            ErrorMessage = "An adjustment needs a reason - it is what the audit trail will show. Nothing was adjusted.";
            return;
        }

        var confirmed = _dialogs.Confirm(
            "Apply this adjustment",
            $"{batch.MedicineName} · {batch.BatchNumber}: {batch.QuantityOnHand:0.###} on hand changes by "
                + $"{delta:+0.###;-0.###} unit(s) to {(batch.QuantityOnHand + delta):0.###}.{Environment.NewLine}{Environment.NewLine}"
                + $"Reason: {reason}");

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var outcome = await _corrections
                .AdjustAsync(
                    new StockAdjustmentDraft
                    {
                        MedicineBatchId = batch.BatchId,
                        IsIncrease = delta > 0m,
                        Quantity = Math.Abs(delta),
                        Reason = reason,
                        AdjustedBy = OperatorName(),
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                ErrorMessage = outcome.Message;
                return;
            }

            var done = outcome.Value!;
            CorrectionMessage = $"Adjusted {batch.MedicineName} · {batch.BatchNumber} by "
                + $"{done.QuantityDelta:+0.###;-0.###} to {done.QuantityAfter:0.###} on {done.BusinessDate:yyyy-MM-dd}. "
                + "The movement is in the ledger below.";

            _logger.LogInformation(
                "Adjusted batch {BatchId} by {Delta}: {Reason}",
                batch.BatchId,
                done.QuantityDelta,
                reason);

            await SearchAsync(cancellationToken).ConfigureAwait(true);
            await RunReconciliationAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adjusting batch {BatchId} failed.", batch.BatchId);
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- corrections: dispose

    [RelayCommand]
    private Task WriteOffExpiryAsync(CancellationToken cancellationToken) =>
        WriteOffBatchAsync(isRecall: false, cancellationToken);

    [RelayCommand]
    private Task WriteOffRecallAsync(CancellationToken cancellationToken) =>
        WriteOffBatchAsync(isRecall: true, cancellationToken);

    /// <summary>
    /// Disposes of the selected batch's entire remaining quantity. The stock leaves the sellable shelf, the
    /// ledger gains a write-off movement, and the batch stays blocked with the reason recorded on it.
    /// </summary>
    private async Task WriteOffBatchAsync(bool isRecall, CancellationToken cancellationToken)
    {
        ClearError();
        CorrectionMessage = null;

        var batch = SelectedBatch;

        if (batch is not { QuantityOnHand: > 0m })
        {
            ErrorMessage = "Choose a batch with stock still on it to write off.";
            return;
        }

        var reason = _dialogs.Prompt(
            isRecall ? "Write off recalled stock" : "Write off expired or damaged stock",
            $"{batch.MedicineName} · {batch.BatchNumber} — all {batch.QuantityOnHand:0.###} unit(s) on it leave for good.{Environment.NewLine}{Environment.NewLine}"
                + (isRecall
                    ? "Give the recall notice this disposal is acting on."
                    : "Give the expiry decision or the damage report this disposal is acting on."),
            string.Empty);

        if (string.IsNullOrWhiteSpace(reason))
        {
            ErrorMessage = "A write-off needs a reason - the recall notice, the expiry decision or the "
                + "damage report it acts on. Nothing was written off.";
            return;
        }

        var confirmed = _dialogs.Confirm(
            "Write off the whole batch",
            $"Dispose of all {batch.QuantityOnHand:0.###} unit(s) of {batch.MedicineName} · {batch.BatchNumber}?{Environment.NewLine}{Environment.NewLine}"
                + "Nothing from this batch can be sold, restocked or returned to the supplier afterwards. "
                + $"The batch is marked {(isRecall ? "recalled" : "written off")} with the reason on it.{Environment.NewLine}{Environment.NewLine}"
                + $"Reason: {reason}");

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var outcome = await _corrections
                .WriteOffAsync(
                    new StockWriteOffDraft
                    {
                        MedicineBatchId = batch.BatchId,
                        IsRecall = isRecall,
                        Reason = reason,
                        WrittenOffBy = OperatorName(),
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                ErrorMessage = outcome.Message;
                return;
            }

            var done = outcome.Value!;
            CorrectionMessage = $"Wrote off {Math.Abs(done.QuantityDelta):0.###} unit(s) of "
                + $"{batch.MedicineName} · {batch.BatchNumber} on {done.BusinessDate:yyyy-MM-dd}; "
                + $"the batch is marked {(isRecall ? "recalled" : "written off")} and nothing on it can be sold.";

            _logger.LogInformation(
                "Wrote off batch {BatchId} ({Kind}): {Reason}",
                batch.BatchId,
                isRecall ? "recall" : "expiry",
                reason);

            await SearchAsync(cancellationToken).ConfigureAwait(true);
            await RunReconciliationAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Writing off batch {BatchId} failed.", batch.BatchId);
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- corrections: quarantine

    [RelayCommand]
    private Task QuarantineRecallAsync(CancellationToken cancellationToken) =>
        QuarantineBatchAsync(isRecall: true, cancellationToken);

    [RelayCommand]
    private Task QuarantineCondemnAsync(CancellationToken cancellationToken) =>
        QuarantineBatchAsync(isRecall: false, cancellationToken);

    /// <summary>
    /// Blocks the selected batch where it sits until disposal is a deliberate second step. No quantity
    /// moves and no stock leaves; the batch simply cannot be sold, adjusted or sent back to the supplier
    /// while the hold is on it.
    /// </summary>
    private async Task QuarantineBatchAsync(bool isRecall, CancellationToken cancellationToken)
    {
        ClearError();
        CorrectionMessage = null;

        var batch = SelectedBatch;

        if (batch is not { QuantityOnHand: > 0m })
        {
            ErrorMessage = "Choose a batch with stock still on it to quarantine.";
            return;
        }

        var reason = _dialogs.Prompt(
            isRecall ? "Hold back recalled stock" : "Hold back condemned stock",
            $"{batch.MedicineName} · {batch.BatchNumber} — {batch.QuantityOnHand:0.###} unit(s) stay on the shelf, "
                + "held out of every sale and movement until they are written off."
                + $"{Environment.NewLine}{Environment.NewLine}"
                + (isRecall
                    ? "Give the recall notice this hold is acting on."
                    : "Give the condemnation or the damage report this hold is acting on."),
            string.Empty);

        if (string.IsNullOrWhiteSpace(reason))
        {
            ErrorMessage = "A quarantine needs a reason - the recall notice, the condemnation or the damage "
                + "report it acts on. Nothing was held back.";
            return;
        }

        var confirmed = _dialogs.Confirm(
            "Hold this stock back",
            $"Block all {batch.QuantityOnHand:0.###} unit(s) of {batch.MedicineName} · {batch.BatchNumber} from sale?{Environment.NewLine}{Environment.NewLine}"
                + "The stock stays where it is; nothing is destroyed yet. Disposal is a separate write-off "
                + $"once the goods actually leave.{Environment.NewLine}{Environment.NewLine}"
                + $"Reason: {reason}");

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var outcome = await _corrections
                .QuarantineAsync(
                    new StockQuarantineDraft
                    {
                        MedicineBatchId = batch.BatchId,
                        IsRecall = isRecall,
                        Reason = reason,
                        QuarantinedBy = OperatorName(),
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                ErrorMessage = outcome.Message;
                return;
            }

            var done = outcome.Value!;
            CorrectionMessage = $"Held back {done.QuantityHeld:0.###} unit(s) of "
                + $"{batch.MedicineName} · {batch.BatchNumber}; the batch is marked "
                + $"{ReportText.Describe(done.Status).ToLowerInvariant()} pending disposal. "
                + "It is listed under Returns & Adjustments - Stock held in quarantine.";

            _logger.LogInformation(
                "Quarantined batch {BatchId} ({Kind}): {Reason}",
                batch.BatchId,
                isRecall ? "recall" : "condemned",
                reason);

            await SearchAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quarantining batch {BatchId} failed.", batch.BatchId);
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reads a signed count change, tolerating a leading plus and either decimal separator.</summary>
    private static bool TryReadSignedQuantity(string typed, out decimal quantity)
    {
        quantity = 0m;

        var text = typed.Trim().TrimStart('+');

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out quantity)
            || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out quantity);
    }

    /// <summary>Who signs a correction. Falls back to the Windows account so the audit row is never blank.</summary>
    private string OperatorName() => _session.CurrentUser?.FriendlyName ?? Environment.UserName;

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
