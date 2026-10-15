using System.Windows.Controls;
using MediStock.Desktop.ViewModels;

namespace MediStock.Desktop.Views;

/// <summary>
/// Code-behind for the point-of-sale screen. The only behaviour here is the double-click gesture on a
/// search result, because that is a view gesture rather than a business rule.
/// </summary>
public partial class NewSaleView : UserControl
{
    public NewSaleView()
    {
        InitializeComponent();

        Loaded += (_, _) => ScanBox.Focus();
    }

    private void OnSearchResultActivated(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is NewSaleViewModel viewModel && viewModel.SelectedSearchResult is not null)
        {
            viewModel.AddSelectedCommand.Execute(null);
        }
    }
}
