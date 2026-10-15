using CommunityToolkit.Mvvm.ComponentModel;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Common state for every page view model: a busy flag for progress reporting and a single
/// operator-facing error slot.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? errorMessage;

    protected ViewModelBase(string title)
    {
        Title = title;
    }

    /// <summary>Heading shown at the top of the page.</summary>
    public string Title { get; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>Called the first time the page is shown.</summary>
    public virtual Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    protected void ClearError() => ErrorMessage = null;

    protected void SetError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ErrorMessage = Application.Common.OperationResult.From(exception).Message;
    }
}
