using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Purchasing;
using MediStock.Application.Suppliers;
using MediStock.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Suppliers: searchable list on the left, editor for the selected record on the right. Suppliers are
/// deactivated rather than deleted, because purchase history has to keep resolving their names.
/// </summary>
public sealed partial class SuppliersViewModel : ViewModelBase
{
    private readonly ISupplierCatalog _catalog;
    private readonly IPurchaseCatalog _purchases;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _navigation;
    private readonly ILogger<SuppliersViewModel> _logger;

    public SuppliersViewModel(
        ISupplierCatalog catalog,
        IPurchaseCatalog purchases,
        IDialogService dialogs,
        INavigationService navigation,
        ILogger<SuppliersViewModel> logger)
        : base("Suppliers")
    {
        _catalog = catalog;
        _purchases = purchases;
        _dialogs = dialogs;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<SupplierDto> Suppliers { get; } = [];

    /// <summary>Purchases raised against the selected supplier, so its history is one click away.</summary>
    public ObservableCollection<PurchaseSummaryDto> PurchaseHistory { get; } = [];

    public SupplierEditorViewModel Editor { get; } = new();

    [ObservableProperty]
    private string searchTerm = string.Empty;

    [ObservableProperty]
    private bool includeInactive;

    [ObservableProperty]
    private SupplierDto? selectedSupplier;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private bool hasSearched;

    [ObservableProperty]
    private bool isLoadingHistory;

    public string ResultSummary => Suppliers.Count switch
    {
        0 => "No suppliers match the current filters.",
        1 => "1 supplier",
        _ => $"{Suppliers.Count} suppliers",
    };

    public bool HasNoResults => HasSearched && Suppliers.Count == 0;

    public string SelectionSummary => SelectedSupplier is { } supplier
        ? supplier.IsActive ? "Active" : "Inactive"
        : "No record selected";

    public string HistorySummary => SelectedSupplier is not { } supplier
        ? "Select a supplier to see its purchase history."
        : PurchaseHistory.Count switch
        {
            0 => $"No purchases recorded for {supplier.Name} yet.",
            1 => "1 purchase",
            _ => $"{PurchaseHistory.Count} purchases",
        };

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        SearchAsync(cancellationToken);

    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        ClearError();
        StatusMessage = null;

        try
        {
            var results = await _catalog
                .SearchAsync(
                    new SupplierSearchQuery
                    {
                        Term = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim(),
                        IncludeInactive = IncludeInactive,
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            var previousId = SelectedSupplier?.Id;

            Suppliers.Clear();
            foreach (var supplier in results)
            {
                Suppliers.Add(supplier);
            }

            HasSearched = true;
            OnPropertyChanged(nameof(ResultSummary));
            OnPropertyChanged(nameof(HasNoResults));

            SelectedSupplier = previousId is null
                ? Suppliers.FirstOrDefault()
                : Suppliers.FirstOrDefault(s => s.Id == previousId) ?? Suppliers.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Supplier search failed.");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task ClearSearchAsync()
    {
        SearchTerm = string.Empty;
        IncludeInactive = false;
        return SearchAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void NewSupplier()
    {
        SelectedSupplier = null;
        Editor.BeginNew();
        StatusMessage = null;
        ClearError();
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

        IsBusy = true;
        try
        {
            var result = Editor.SupplierId is { } id
                ? await _catalog.UpdateAsync(id, draft, cancellationToken).ConfigureAwait(true)
                : await _catalog.CreateAsync(draft, cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                return;
            }

            StatusMessage = Editor.IsNew
                ? $"Created {result.Value!.Name}."
                : $"Saved changes to {result.Value!.Name}.";

            var savedId = result.Value!.Id;
            await SearchAsync(cancellationToken).ConfigureAwait(true);
            SelectedSupplier = Suppliers.FirstOrDefault(s => s.Id == savedId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the supplier failed.");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleActiveAsync(CancellationToken cancellationToken)
    {
        if (SelectedSupplier is not { } supplier)
        {
            return;
        }

        ClearError();
        StatusMessage = null;

        var activating = !supplier.IsActive;

        var confirmed = _dialogs.Confirm(
            activating ? "Reactivate supplier" : "Deactivate supplier",
            activating
                ? $"Make {supplier.Name} available for new purchases again?"
                : $"Hide {supplier.Name} from new purchases?{Environment.NewLine}{Environment.NewLine}" +
                  "Existing purchase history keeps referencing this record.");

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _catalog
                .SetActiveAsync(supplier.Id, activating, cancellationToken)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                return;
            }

            StatusMessage = activating
                ? $"{supplier.Name} is active again."
                : $"{supplier.Name} was deactivated.";

            var id = supplier.Id;
            await SearchAsync(cancellationToken).ConfigureAwait(true);
            SelectedSupplier = Suppliers.FirstOrDefault(s => s.Id == id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Changing the supplier active flag failed.");
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
        Editor.Load(SelectedSupplier);
        ErrorMessage = null;
        StatusMessage = null;
    }

    partial void OnSelectedSupplierChanged(SupplierDto? value)
    {
        Editor.Load(value);
        OnPropertyChanged(nameof(SelectionSummary));
        _ = LoadHistoryAsync(value);
    }

    private async Task LoadHistoryAsync(SupplierDto? supplier)
    {
        IsLoadingHistory = true;

        try
        {
            PurchaseHistory.Clear();

            if (supplier is not null)
            {
                var purchases = await _purchases
                    .SearchAsync(new PurchaseSearchQuery { SupplierId = supplier.Id, Take = 50 }, CancellationToken.None)
                    .ConfigureAwait(true);

                foreach (var purchase in purchases)
                {
                    PurchaseHistory.Add(purchase);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading supplier purchase history failed.");
        }
        finally
        {
            IsLoadingHistory = false;
            OnPropertyChanged(nameof(HistorySummary));
        }
    }

    [RelayCommand]
    private void OpenPurchases() => _navigation.NavigateTo(NavigationRoutes.Purchases);
}
