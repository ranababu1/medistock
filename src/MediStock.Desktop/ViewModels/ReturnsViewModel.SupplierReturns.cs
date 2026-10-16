using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Common;
using MediStock.Application.Identity;
using MediStock.Application.Inventory;
using MediStock.Application.Reporting;
using MediStock.Application.Suppliers;
using MediStock.Domain.Common;
using MediStock.Domain.Identity;
using Microsoft.Extensions.Logging;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Supplier returns and quarantine, the second half of the Returns &amp; Adjustments screen.
/// </summary>
/// <remarks>
/// <para>
/// The draft the operator assembles is checked twice: this view model keeps its own lines within what
/// the batches are recorded as holding, and <see cref="ISupplierReturnCatalog"/> re-reads each batch
/// inside the transaction before anything is booked. A stale screen cannot record a return the data
/// no longer allows.
/// </para>
/// <para>
/// The credit shown is the quantity at the purchase cost frozen on each batch, which is the figure the
/// recorded return credits — the same rule, so the number on screen is the number the ledger takes.
/// </para>
/// </remarks>
public sealed partial class ReturnsViewModel
{
    private readonly ISupplierReturnCatalog _supplierReturns;
    private readonly ISupplierCatalog _suppliers;
    private readonly IStockQueries _stock;
    private readonly IUserSession _session;

    /// <summary>Set once a batch search has run, so an empty result is a finding rather than silence.</summary>
    private bool _batchSearchRun;

    private bool _supplierReturnSearchRun;

    private bool _quarantineSearchRun;

    public ObservableCollection<SupplierDto> SupplierOptions { get; } = [];

    /// <summary>Batches offered for the draft's next line, already narrowed to the supplier being credited.</summary>
    public ObservableCollection<BatchStockDto> BatchChoices { get; } = [];

    public ObservableCollection<SupplierReturnLineViewModel> SupplierReturnLines { get; } = [];

    public ObservableCollection<SupplierReturnSummaryDto> SupplierReturns { get; } = [];

    public ObservableCollection<QuarantinedStockDto> Quarantined { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddSupplierReturnLine))]
    private BatchStockDto? selectedBatchChoice;

    [ObservableProperty]
    private string batchSearchTerm = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRecordSupplierReturn))]
    [NotifyPropertyChangedFor(nameof(BatchChoicesSummary))]
    private SupplierDto? selectedSupplier;

    [ObservableProperty]
    private string supplierReturnReason = string.Empty;

    [ObservableProperty]
    private string supplierCreditNote = string.Empty;

    [ObservableProperty]
    private string supplierReturnedBy = string.Empty;

    [ObservableProperty]
    private string nextSupplierReturnNumber = "…";

    [ObservableProperty]
    private string supplierReturnSearchTerm = string.Empty;

    [ObservableProperty]
    private DateOnly? supplierReturnFrom;

    [ObservableProperty]
    private DateOnly? supplierReturnTo;

    [ObservableProperty]
    private SupplierReturnSummaryDto? selectedSupplierReturn;

    /// <summary>The selected history row opened in full, once the operator asks for its lines.</summary>
    [ObservableProperty]
    private SupplierReturnDto? supplierReturnDetail;

    [ObservableProperty]
    private string quarantineSearchTerm = string.Empty;

    public bool CanReturnToSupplier => _session.Can(Permission.ReturnToSupplier);

    public bool CannotReturnToSupplier => !CanReturnToSupplier;

    public string SupplierReturnRefusalNote =>
        PermissionSet.Explain(Permission.ReturnToSupplier, "send stock back to a supplier");

    public bool CanAddSupplierReturnLine => CanReturnToSupplier && SelectedBatchChoice is not null;

    public bool HasSupplierReturnLines => SupplierReturnLines.Count > 0;

    public decimal UnitsInSupplierReturnDraft => SupplierReturnLines.Sum(line => line.Quantity);

    /// <summary>What the supplier is expected to credit: the draft's quantity at each batch's frozen cost.</summary>
    public decimal ExpectedSupplierCredit => Money.Round(SupplierReturnLines.Sum(line => line.CreditValue));

    public string ExpectedSupplierCreditText => FormatMoney(ExpectedSupplierCredit);

    public string SupplierReturnDraftSummary =>
        $"{SupplierReturnLines.Count} line(s) · {UnitsInSupplierReturnDraft:0.###} unit(s) going back · "
        + $"{ExpectedSupplierCreditText} credit expected";

    public bool CanRecordSupplierReturn =>
        CanReturnToSupplier && SelectedSupplier is not null && SupplierReturnLines.Count > 0;

    public bool CanClearSupplierReturnDraft =>
        SupplierReturnLines.Count > 0
        || SelectedSupplier is not null
        || !string.IsNullOrWhiteSpace(SupplierReturnReason)
        || !string.IsNullOrWhiteSpace(SupplierCreditNote);

    public string BatchChoicesSummary => SelectedSupplier is null
        ? $"{BatchChoices.Count} batch(es) with stock on hand. Choose the supplier first; only their batches may go back to them."
        : $"{BatchChoices.Count} batch(es) bought from {SelectedSupplier.Name} with stock on hand.";

    public bool HasNoBatchChoices => _batchSearchRun && BatchChoices.Count == 0;

    public bool HasNoSupplierReturns => _supplierReturnSearchRun && SupplierReturns.Count == 0;

    public string SupplierReturnsResultSummary => SupplierReturns.Count == 1
        ? "1 supplier return"
        : $"{SupplierReturns.Count} supplier returns";

    public string SupplierReturnDetailHeading => SelectedSupplierReturn is null
        ? "No supplier return is selected"
        : $"{SelectedSupplierReturn.ReturnNumber} · {SelectedSupplierReturn.SupplierName}";

    public string SupplierReturnDetailSummary => SelectedSupplierReturn is null
        ? "Pick a return from the list above to read its lines here."
        : $"Booked {SelectedSupplierReturn.BusinessDate:yyyy-MM-dd} by {SelectedSupplierReturn.ReturnedBy} · "
          + $"{SelectedSupplierReturn.ReturnedQuantity:0.###} unit(s) back · "
          + $"{FormatMoney(SelectedSupplierReturn.TotalCostValue)} credit expected · "
          + $"reason: {SelectedSupplierReturn.Reason}";

    public bool CanViewSupplierReturnDetail => SelectedSupplierReturn is not null;

    public bool HasNoQuarantined => _quarantineSearchRun && Quarantined.Count == 0;

    public string QuarantinedResultSummary =>
        $"{Quarantined.Count} batch(es) held in quarantine · {Quarantined.Sum(item => item.Quantity):0.###} unit(s) "
        + "waiting to be dealt with";

    /// <summary>Seed values the constructor needs before anything loads. The session has not started yet.</summary>
    private void SetupSupplierReturns()
    {
        // Who physically hands the goods back defaults to the signed-in operator; the Windows
        // account name is only a fallback for a session that has nobody in it yet.
        SupplierReturnedBy = OperatorName();
    }

    /// <summary>Everything the supplier-return and quarantine cards need on their first visit.</summary>
    private async Task InitializeSupplierReturnsAsync(CancellationToken cancellationToken)
    {
        await LoadSupplierOptionsAsync(cancellationToken).ConfigureAwait(true);
        await RefreshSupplierReturnNumberAsync(cancellationToken).ConfigureAwait(true);
        await SearchSupplierReturnsAsync(cancellationToken).ConfigureAwait(true);
        await LoadQuarantinedAsync(cancellationToken).ConfigureAwait(true);
    }

    partial void OnSelectedSupplierChanged(SupplierDto? value)
    {
        // A new supplier invalidates a list built for the old one: the picker must not quietly offer
        // somebody else's batches. The draft lines already taken stay — the catalog checks them again.
        _batchSearchRun = false;
        BatchChoices.Clear();
        SelectedBatchChoice = null;
    }

    partial void OnSelectedSupplierReturnChanged(SupplierReturnSummaryDto? value)
    {
        SupplierReturnDetail = null;
        OnPropertyChanged(nameof(SupplierReturnDetailHeading));
        OnPropertyChanged(nameof(SupplierReturnDetailSummary));
        OnPropertyChanged(nameof(CanViewSupplierReturnDetail));
    }

    /// <summary>Opens the selected history row in full, so its lines can be read without re-searching.</summary>
    [RelayCommand]
    private async Task ViewSupplierReturnDetailAsync(CancellationToken cancellationToken)
    {
        if (SelectedSupplierReturn is null)
        {
            ErrorMessage = "Pick a supplier return from the list before opening it.";
            return;
        }

        ClearError();
        IsBusy = true;

        try
        {
            var detail = await _supplierReturns
                .FindAsync(SelectedSupplierReturn.Id, cancellationToken)
                .ConfigureAwait(true);

            if (detail is null)
            {
                ErrorMessage = $"{SelectedSupplierReturn.ReturnNumber} could not be found. Refresh the history and pick it again.";
                return;
            }

            SupplierReturnDetail = detail;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Supplier return {ReturnId} could not be opened.", SelectedSupplierReturn.Id);
            ErrorMessage = "The return could not be opened. Try again in a moment.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadSupplierOptionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Inactive suppliers stay in the list: stock bought while they were active can still go back to
            // them, and a return is history before it is anything else.
            var suppliers = await _suppliers
                .SearchAsync(new SupplierSearchQuery { IncludeInactive = true, Take = SupplierSearchQuery.MaxResults },
                    cancellationToken)
                .ConfigureAwait(true);

            var chosen = SelectedSupplier;

            SupplierOptions.Clear();
            foreach (var supplier in suppliers)
            {
                SupplierOptions.Add(supplier);
            }

            if (chosen is not null)
            {
                SelectedSupplier = SupplierOptions.FirstOrDefault(s => s.Id == chosen.Id);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The supplier list for the returns screen could not be loaded.");
            ErrorMessage = "The supplier list could not be loaded. Try the page again in a moment.";
        }
    }

    private async Task RefreshSupplierReturnNumberAsync(CancellationToken cancellationToken)
    {
        try
        {
            NextSupplierReturnNumber = await _supplierReturns
                .PreviewReturnNumberAsync(cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The next supplier return number could not be previewed.");
        }
    }

    /// <summary>Offers the batches the chosen supplier was actually paid for, with stock still on hand.</summary>
    [RelayCommand]
    private async Task SearchBatchChoicesAsync(CancellationToken cancellationToken)
    {
        ClearError();

        if (SelectedSupplier is null)
        {
            ErrorMessage = "Choose the supplier the goods are going back to, then search their batches.";
            return;
        }

        IsBusy = true;

        try
        {
            var batches = await _stock
                .ListBatchesAsync(
                    new BatchStockQuery
                    {
                        Term = string.IsNullOrWhiteSpace(BatchSearchTerm) ? null : BatchSearchTerm.Trim(),
                        SupplierId = SelectedSupplier.Id,
                        InStockOnly = true,
                        Take = BatchStockQuery.MaxResults,
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            BatchChoices.Clear();
            foreach (var batch in batches)
            {
                BatchChoices.Add(batch);
            }

            _batchSearchRun = true;
            OnPropertyChanged(nameof(BatchChoicesSummary));
            OnPropertyChanged(nameof(HasNoBatchChoices));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch choices for the supplier return could not be loaded.");
            ErrorMessage = "The batch list could not be loaded. Try the search again in a moment.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddSupplierReturnLineAsync(CancellationToken cancellationToken)
    {
        ClearError();
        StatusMessage = null;

        if (SelectedBatchChoice is null)
        {
            ErrorMessage = "Pick a batch from the results below the search box first.";
            return;
        }

        var batch = SelectedBatchChoice;
        var answer = _dialogs.Prompt(
            $"Return {batch.MedicineName} to supplier",
            $"Batch {batch.BatchNumber} has {batch.QuantityOnHand:0.###} unit(s) on hand."
            + $"{Environment.NewLine}{Environment.NewLine}How many of {batch.MedicineName} are going back?",
            batch.QuantityOnHand.ToString("0.###", CultureInfo.CurrentCulture));

        if (answer is null)
        {
            return;
        }

        if (!TryReadQuantity(answer, out var quantity) || quantity <= 0m)
        {
            ErrorMessage = $"'{answer}' is not a quantity. Type how many units are going back, such as 10 or 2.5.";
            return;
        }

        var alreadyTaken = SupplierReturnLines
            .Where(line => line.MedicineBatchId == batch.BatchId)
            .Sum(line => line.Quantity);

        if (alreadyTaken + quantity > batch.QuantityOnHand)
        {
            ErrorMessage = $"Batch {batch.BatchNumber} has {batch.QuantityOnHand:0.###} unit(s) on hand, and the draft "
                + $"already takes {alreadyTaken:0.###}. A return cannot take more than the shelf holds.";
            return;
        }

        var line = SupplierReturnLines.FirstOrDefault(existing => existing.MedicineBatchId == batch.BatchId);
        if (line is null)
        {
            var fresh = new SupplierReturnLineViewModel(batch);
            fresh.PropertyChanged += OnSupplierReturnLinePropertyChanged;
            fresh.Quantity = quantity;
            SupplierReturnLines.Add(fresh);
        }
        else
        {
            line.Quantity += quantity;
        }

        SelectedBatchChoice = null;
        RefreshSupplierReturnPreview();
        _logger.LogInformation(
            "Supplier return draft now takes {Quantity} unit(s) of batch {BatchId}.",
            SupplierReturnLines.Where(l => l.MedicineBatchId == batch.BatchId).Sum(l => l.Quantity),
            batch.BatchId);
    }

    [RelayCommand]
    private void RemoveSupplierReturnLine(SupplierReturnLineViewModel? line)
    {
        if (line is null || !SupplierReturnLines.Remove(line))
        {
            return;
        }

        line.PropertyChanged -= OnSupplierReturnLinePropertyChanged;
        RefreshSupplierReturnPreview();
    }

    /// <summary>Books the draft. Everything the operator has been shown is re-checked before anything moves.</summary>
    [RelayCommand]
    private async Task RecordSupplierReturnAsync(CancellationToken cancellationToken)
    {
        ClearError();
        StatusMessage = null;

        if (!CanReturnToSupplier)
        {
            ErrorMessage = SupplierReturnRefusalNote;
            return;
        }

        if (SelectedSupplier is null)
        {
            ErrorMessage = "Choose the supplier the goods are going back to before recording the return.";
            return;
        }

        if (SupplierReturnLines.Count == 0)
        {
            ErrorMessage = "Add at least one batch to the return before recording it.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SupplierReturnReason))
        {
            ErrorMessage = "Write a reason for the return. It is printed on the return document and kept with it.";
            return;
        }

        var units = UnitsInSupplierReturnDraft;
        var credit = ExpectedSupplierCredit;
        var confirmed = _dialogs.Confirm(
            "Record supplier return?",
            $"Return to {SelectedSupplier.Name}{Environment.NewLine}{Environment.NewLine}"
            + $"{SupplierReturnLines.Count} line(s) · {units:0.###} unit(s) going back, "
            + $"credit {FormatMoney(credit)} expected at cost.{Environment.NewLine}{Environment.NewLine}"
            + $"Number {NextSupplierReturnNumber}{Environment.NewLine}"
            + $"Reason: {SupplierReturnReason.Trim()}{Environment.NewLine}{Environment.NewLine}"
            + "Once recorded, the return is history: it is corrected by a further return or a write-off, "
            + "never by editing or deleting it.");

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;

        try
        {
            var draft = new SupplierReturnDraft
            {
                SupplierId = SelectedSupplier.Id,
                ReturnedBy = string.IsNullOrWhiteSpace(SupplierReturnedBy)
                    ? OperatorName()
                    : SupplierReturnedBy.Trim(),
                Reason = SupplierReturnReason.Trim(),
                Notes = null,
                SupplierCreditNoteNumber = string.IsNullOrWhiteSpace(SupplierCreditNote)
                    ? null
                    : SupplierCreditNote.Trim(),
                Lines = SupplierReturnLines
                    .Select(line => new SupplierReturnLineDraft
                    {
                        MedicineBatchId = line.MedicineBatchId,
                        Quantity = line.Quantity,
                        Notes = line.Notes,
                    })
                    .ToList(),
            };

            var outcome = await _supplierReturns.RecordAsync(draft, cancellationToken).ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                ErrorMessage = outcome.Message;
                return;
            }

            var done = outcome.Value;
            if (done is null)
            {
                ErrorMessage = "The return could not be read back after recording. Check the history below before trying again.";
                return;
            }

            var bookedUnits = done.Lines.Sum(line => line.Quantity);
            StatusMessage = $"{done.ReturnNumber} recorded: {bookedUnits:0.###} unit(s) back to {done.SupplierName}, "
                + $"credit {FormatMoney(done.TotalCostValue)} expected at cost.";

            _logger.LogInformation(
                "Supplier return {ReturnNumber} recorded by {Operator}: {Lines} line(s), {Units} unit(s).",
                done.ReturnNumber,
                done.ReturnedBy,
                done.Lines.Count,
                bookedUnits);

            ClearSupplierReturnDraft();
            await RefreshSupplierReturnNumberAsync(cancellationToken).ConfigureAwait(true);
            await SearchSupplierReturnsAsync(cancellationToken).ConfigureAwait(true);
            await LoadQuarantinedAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The supplier return could not be recorded.");
            ErrorMessage = "The return could not be recorded. Nothing has been booked; try again in a moment.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearSupplierReturnDraft()
    {
        ClearSupplierReturnLines();
        SelectedSupplier = null;
        SupplierReturnReason = string.Empty;
        SupplierCreditNote = string.Empty;
        SupplierReturnedBy = OperatorName();
        ClearError();
    }

    private void ClearSupplierReturnLines()
    {
        foreach (var line in SupplierReturnLines)
        {
            line.PropertyChanged -= OnSupplierReturnLinePropertyChanged;
        }

        SupplierReturnLines.Clear();
        RefreshSupplierReturnPreview();
    }

    /// <summary>Reads the draft back over the whole screen, called after any line changes.</summary>
    private void RefreshSupplierReturnPreview()
    {
        OnPropertyChanged(nameof(HasSupplierReturnLines));
        OnPropertyChanged(nameof(UnitsInSupplierReturnDraft));
        OnPropertyChanged(nameof(ExpectedSupplierCredit));
        OnPropertyChanged(nameof(ExpectedSupplierCreditText));
        OnPropertyChanged(nameof(SupplierReturnDraftSummary));
        OnPropertyChanged(nameof(CanRecordSupplierReturn));
        OnPropertyChanged(nameof(CanClearSupplierReturnDraft));
    }

    private void OnSupplierReturnLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SupplierReturnLineViewModel.Quantity))
        {
            RefreshSupplierReturnPreview();
        }
    }

    [RelayCommand]
    private async Task SearchSupplierReturnsAsync(CancellationToken cancellationToken)
    {
        ClearError();

        IsBusy = true;

        try
        {
            var results = await _supplierReturns
                .SearchAsync(
                    new SupplierReturnQuery
                    {
                        Term = string.IsNullOrWhiteSpace(SupplierReturnSearchTerm)
                            ? null
                            : SupplierReturnSearchTerm.Trim(),
                        FromBusinessDate = SupplierReturnFrom,
                        ToBusinessDate = SupplierReturnTo,
                        Take = SupplierReturnQuery.MaxResults,
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            SelectedSupplierReturn = null;
            SupplierReturns.Clear();
            foreach (var result in results)
            {
                SupplierReturns.Add(result);
            }

            _supplierReturnSearchRun = true;
            OnPropertyChanged(nameof(HasNoSupplierReturns));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The supplier returns history could not be loaded.");
            ErrorMessage = "The supplier returns history could not be loaded. Try the search again in a moment.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearSupplierReturnFiltersAsync(CancellationToken cancellationToken)
    {
        SupplierReturnSearchTerm = string.Empty;
        SupplierReturnFrom = null;
        SupplierReturnTo = null;
        await SearchSupplierReturnsAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// The stock that must not leave the shelf: kept-back customer returns, batches under recall, and
    /// written-off batches. One list, because it is one question.
    /// </summary>
    [RelayCommand]
    private async Task LoadQuarantinedAsync(CancellationToken cancellationToken)
    {
        ClearError();

        IsBusy = true;

        try
        {
            var held = await _stock
                .ListQuarantinedAsync(
                    new QuarantinedStockQuery
                    {
                        Term = string.IsNullOrWhiteSpace(QuarantineSearchTerm)
                            ? null
                            : QuarantineSearchTerm.Trim(),
                        Take = QuarantinedStockQuery.MaxResults,
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            Quarantined.Clear();
            foreach (var item in held)
            {
                Quarantined.Add(item);
            }

            _quarantineSearchRun = true;
            OnPropertyChanged(nameof(HasNoQuarantined));
            OnPropertyChanged(nameof(QuarantinedResultSummary));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The quarantined stock list could not be loaded.");
            ErrorMessage = "The quarantined stock list could not be loaded. Try the search again in a moment.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearQuarantineSearchAsync(CancellationToken cancellationToken)
    {
        QuarantineSearchTerm = string.Empty;
        await LoadQuarantinedAsync(cancellationToken).ConfigureAwait(true);
    }

    private string OperatorName() => _session.CurrentUser?.FriendlyName ?? Environment.UserName;

    /// <summary>Parses a count the operator typed, in their own culture first, as the prompt suggests.</summary>
    private static bool TryReadQuantity(string? text, out decimal quantity)
    {
        quantity = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out quantity)
            || decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out quantity);
    }
}

/// <summary>
/// One batch quantity going back to a supplier: the batch, how much of it, and what that quantity is
/// worth at the cost the pharmacy paid.
/// </summary>
public sealed partial class SupplierReturnLineViewModel : ObservableObject
{
    private decimal _quantity;
    private string? _notes;

    public SupplierReturnLineViewModel(BatchStockDto batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        MedicineBatchId = batch.BatchId;
        MedicineName = batch.MedicineName;
        BatchNumber = batch.BatchNumber;
        ExpiryDate = batch.ExpiryDate;
        QuantityOnHand = batch.QuantityOnHand;
        UnitCost = batch.PurchaseCost;
    }

    public int MedicineBatchId { get; }

    public string MedicineName { get; }

    public string BatchNumber { get; }

    public DateOnly ExpiryDate { get; }

    public decimal QuantityOnHand { get; }

    /// <summary>What the pharmacy paid for one unit, frozen on the batch; the credit is expected at this cost.</summary>
    public decimal UnitCost { get; }

    /// <summary>How many units of this batch are going back.</summary>
    public decimal Quantity
    {
        get => _quantity;
        set
        {
            if (SetProperty(ref _quantity, decimal.Round(Math.Max(value, 0m), 3)))
            {
                OnPropertyChanged(nameof(CreditValue));
                OnPropertyChanged(nameof(CreditValueText));
            }
        }
    }

    public string? Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public decimal CreditValue => Money.Round(Quantity * UnitCost);

    public string CreditValueText => CreditValue.ToString("N2", CultureInfo.CurrentCulture);
}
