using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Catalogue;
using MediStock.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Medicines catalogue: searchable list on the left, editor for the selected record on the right.
/// Records are deactivated rather than deleted so historical sales remain explainable.
/// </summary>
public sealed partial class MedicinesViewModel : ViewModelBase
{
    private readonly IMedicineCatalog _catalog;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MedicinesViewModel> _logger;

    public MedicinesViewModel(
        IMedicineCatalog catalog,
        IDialogService dialogs,
        ILogger<MedicinesViewModel> logger)
        : base("Medicines")
    {
        _catalog = catalog;
        _dialogs = dialogs;
        _logger = logger;
    }

    public ObservableCollection<MedicineDto> Medicines { get; } = [];

    public MedicineEditorViewModel Editor { get; } = new();

    [ObservableProperty]
    private string searchTerm = string.Empty;

    [ObservableProperty]
    private bool includeInactive;

    [ObservableProperty]
    private MedicineDto? selectedMedicine;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private bool hasSearched;

    public string ResultSummary => Medicines.Count switch
    {
        0 => "No medicines match the current filters.",
        1 => "1 medicine",
        _ => $"{Medicines.Count} medicines",
    };

    public bool HasNoResults => HasSearched && Medicines.Count == 0;

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
                    new MedicineSearchQuery
                    {
                        Term = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim(),
                        IncludeInactive = IncludeInactive,
                    },
                    cancellationToken)
                .ConfigureAwait(true);

            var previousId = SelectedMedicine?.Id;

            Medicines.Clear();
            foreach (var medicine in results)
            {
                Medicines.Add(medicine);
            }

            HasSearched = true;
            OnPropertyChanged(nameof(ResultSummary));
            OnPropertyChanged(nameof(HasNoResults));

            SelectedMedicine = previousId is null
                ? Medicines.FirstOrDefault()
                : Medicines.FirstOrDefault(m => m.Id == previousId) ?? Medicines.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Catalogue search failed.");
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
    private void NewMedicine()
    {
        SelectedMedicine = null;
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

        if (Editor.IsNew)
        {
            var duplicates = await _catalog
                .FindPotentialDuplicatesAsync(draft, null, cancellationToken)
                .ConfigureAwait(true);

            if (duplicates.Count > 0)
            {
                var names = string.Join(Environment.NewLine, duplicates.Take(5).Select(d => "• " + d.Summary));
                var proceed = _dialogs.Confirm(
                    "Possible duplicate",
                    $"These catalogue records look similar:{Environment.NewLine}{names}{Environment.NewLine}{Environment.NewLine}" +
                    "Add this medicine anyway?");

                if (!proceed)
                {
                    return;
                }
            }
        }

        IsBusy = true;
        try
        {
            var result = Editor.MedicineId is { } id
                ? await _catalog.UpdateAsync(id, draft, cancellationToken).ConfigureAwait(true)
                : await _catalog.CreateAsync(draft, cancellationToken).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                return;
            }

            StatusMessage = Editor.IsNew
                ? $"Created {result.Value!.DisplayName}."
                : $"Saved changes to {result.Value!.DisplayName}.";

            var savedId = result.Value!.Id;
            await SearchAsync(cancellationToken).ConfigureAwait(true);
            SelectedMedicine = Medicines.FirstOrDefault(m => m.Id == savedId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the catalogue record failed.");
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
        if (SelectedMedicine is not { } medicine)
        {
            return;
        }

        ClearError();
        StatusMessage = null;

        var activating = !medicine.IsActive;
        var confirmed = _dialogs.Confirm(
            activating ? "Reactivate medicine" : "Deactivate medicine",
            activating
                ? $"Make {medicine.DisplayName} available for new sales again?"
                : $"Hide {medicine.DisplayName} from new sales?{Environment.NewLine}{Environment.NewLine}" +
                  "Existing sales and stock history keep referencing this record.");

        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _catalog
                .SetActiveAsync(medicine.Id, activating, cancellationToken)
                .ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ErrorMessage = result.Message;
                return;
            }

            StatusMessage = activating
                ? $"{medicine.DisplayName} is active again."
                : $"{medicine.DisplayName} was deactivated.";

            var id = medicine.Id;
            await SearchAsync(cancellationToken).ConfigureAwait(true);
            SelectedMedicine = Medicines.FirstOrDefault(m => m.Id == id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Changing the active flag failed.");
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
        Editor.Load(SelectedMedicine);
        ErrorMessage = null;
        StatusMessage = null;
    }

    partial void OnSelectedMedicineChanged(MedicineDto? value)
    {
        Editor.Load(value);
        OnPropertyChanged(nameof(SelectionSummary));
    }

    public string SelectionSummary => SelectedMedicine is { } medicine
        ? medicine.IsActive ? "Active" : "Inactive"
        : "No record selected";
}
