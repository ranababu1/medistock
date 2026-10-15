using System.Windows;

namespace MediStock.Desktop.Views;

/// <summary>
/// Minimal modal prompt for a single line of text. Used where an operation must be given a reason
/// before it can proceed, so the operator can cancel by leaving the box empty or pressing Escape.
/// </summary>
public partial class TextPromptWindow : Window
{
    private TextPromptWindow(string title, string message, string? initialValue)
    {
        InitializeComponent();

        Title = title;
        HeadlineText.Text = title;
        MessageText.Text = message;
        ResponseBox.Text = initialValue ?? string.Empty;

        Loaded += (_, _) =>
        {
            ResponseBox.Focus();
            ResponseBox.SelectAll();
        };
    }

    /// <summary>Returns the trimmed response, or null when the operator cancelled or typed nothing.</summary>
    public static string? Ask(Window? owner, string title, string message, string? initialValue = null)
    {
        var window = new TextPromptWindow(title, message, initialValue);

        if (owner is not null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(window.ResponseBox.Text)
            ? window.ResponseBox.Text.Trim()
            : null;
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
