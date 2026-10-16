using System.Windows;
using MediStock.Desktop.ViewModels;

namespace MediStock.Desktop.Views;

/// <summary>
/// The first window the application shows. It either creates the first administrator on a fresh
/// installation or asks an existing account to sign in, and it decides which by asking the authentication
/// service whether any account exists yet.
/// </summary>
/// <remarks>
/// The password boxes are read here rather than bound, because WPF does not allow a
/// <see cref="System.Windows.Controls.PasswordBox.Password"/> to be bound — which is the behaviour we want
/// anyway: the plaintext password stays inside this window and is handed to the view model, and then to
/// the hasher, without ever reaching a bound property that could be logged or serialised.
/// </remarks>
public partial class SignInWindow : Window
{
    private readonly SignInViewModel _viewModel;

    public SignInWindow(SignInViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        VersionCaption.Text = ProductInfo.VersionText;

        Loaded += OnLoaded;
    }

    /// <summary>
    /// Shows the sign-in window and blocks until it is finished. Returns true when somebody signed in, and
    /// false when the operator closed the window instead, in which case the caller should exit.
    /// </summary>
    public static bool Ask(SignInViewModel viewModel, Window? owner = null)
    {
        var window = new SignInWindow(viewModel);

        if (owner is not null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        return window.ShowDialog() == true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync();

        // The first thing this person has to fill in, so the keyboard starts there.
        var first = _viewModel.IsFirstRun ? DisplayNameBox : UserNameBox;
        first.Focus();
    }

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        var accepted = _viewModel.IsFirstRun
            ? await _viewModel.SetUpAsync(PasswordBox.Password, ConfirmPasswordBox.Password)
            : await _viewModel.SignInAsync(PasswordBox.Password);

        // Cleared either way: a refused password should not stay on screen, and a good one has no reason
        // to be kept there once it has been accepted.
        PasswordBox.Clear();
        ConfirmPasswordBox.Clear();

        if (accepted)
        {
            DialogResult = true;
            return;
        }

        PasswordBox.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
