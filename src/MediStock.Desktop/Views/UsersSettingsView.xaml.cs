using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MediStock.Desktop.ViewModels;

namespace MediStock.Desktop.Views;

/// <summary>
/// The Users &amp; Settings screen.
/// </summary>
/// <remarks>
/// <para>
/// The three password boxes are read by hand rather than bound, for the same reason the sign-in window does
/// it: WPF does not allow <see cref="PasswordBox.Password"/> to be bound, and that is the behaviour worth
/// keeping — the plaintext stays in the control and is handed to the view model only for as long as the save
/// takes. The boxes are emptied when the editor closes, so a password an operator typed and then thought
/// better of does not sit in a control behind a closed panel.
/// </para>
/// <para>
/// Everything else on this screen is ordinary binding. There is no other behaviour here.
/// </para>
/// </remarks>
public partial class UsersSettingsView : UserControl
{
    private UsersSettingsViewModel? _viewModel;

    public UsersSettingsView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (e.NewValue is not UsersSettingsViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;

        // The editor is created once with the page and reopened in different modes, so the notification to
        // watch is IsOpen rather than a new instance.
        viewModel.Editor.PropertyChanged += OnEditorPropertyChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.Editor.PropertyChanged -= OnEditorPropertyChanged;
            _viewModel = null;
        }
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null
            and not nameof(AccountEditorViewModel.IsOpen)
            and not nameof(AccountEditorViewModel.Mode))
        {
            return;
        }

        if (_viewModel?.Editor.IsOpen == true)
        {
            FocusFirstField();
            return;
        }

        ClearPasswords();
    }

    /// <summary>Reads whichever password box the operator typed in and puts it where the view model keeps it.</summary>
    private void OnEditorPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var editor = _viewModel.Editor;

        if (ReferenceEquals(sender, CurrentPasswordBox))
        {
            editor.CurrentPassword = CurrentPasswordBox.Password;
        }
        else if (ReferenceEquals(sender, NewPasswordBox))
        {
            editor.Password = NewPasswordBox.Password;
        }
        else if (ReferenceEquals(sender, ConfirmPasswordBox))
        {
            editor.ConfirmPassword = ConfirmPasswordBox.Password;
        }
    }

    private void ClearPasswords()
    {
        // Clearing raises PasswordChanged, which writes the empty string back — which is wanted: the view
        // model should not be left holding a password the operator has finished with.
        CurrentPasswordBox.Clear();
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
    }

    private void FocusFirstField()
    {
        var editor = _viewModel!.Editor;

        // Whichever control is on screen first is the one the operator has to deal with first. A role-only
        // change has no text field at all, so the keyboard goes to the button that finishes it.
        Control target = editor.ShowsUserName ? UserNameBox
            : editor.ShowsDisplayName ? DisplayNameBox
            : editor.ShowsCurrentPassword ? CurrentPasswordBox
            : editor.ShowsNewPassword ? NewPasswordBox
            : SaveEditorButton;

        target.Focus();
    }
}
