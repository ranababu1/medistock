using System.Windows;

namespace MediStock.Desktop.Services;

/// <summary>
/// Thin wrapper over <see cref="System.Windows.MessageBox"/> so view models stay
/// testable and free of direct UI dependencies.
/// </summary>
public interface IDialogService
{
    void ShowInformation(string title, string message);

    void ShowWarning(string title, string message);

    void ShowError(string title, string message);

    bool Confirm(string title, string message);
}

public sealed class DialogService : IDialogService
{
    public void ShowInformation(string title, string message) =>
        Show(title, message, MessageBoxImage.Information);

    public void ShowWarning(string title, string message) =>
        Show(title, message, MessageBoxImage.Warning);

    public void ShowError(string title, string message) =>
        Show(title, message, MessageBoxImage.Error);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private static void Show(string title, string message, MessageBoxImage icon) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, icon);
}
