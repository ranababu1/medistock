using System.Windows;

namespace MediStock.Desktop.Views;

/// <summary>
/// The application shell. Its data context is supplied by the composition root so the window stays
/// free of logic.
/// </summary>
public partial class ShellWindow : Window
{
    public ShellWindow()
    {
        InitializeComponent();
    }
}
