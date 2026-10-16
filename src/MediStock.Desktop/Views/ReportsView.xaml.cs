using System.Windows;
using System.Windows.Controls;
using MediStock.Desktop.ViewModels;

namespace MediStock.Desktop.Views;

/// <summary>
/// Code-behind for the reports screen. The report columns are not known until a report has been run, so
/// the grid generates them and this handler only right-aligns the ones the report marked as figures. It
/// is the one piece of the screen that cannot be expressed in XAML, because the decision is a property of
/// the data rather than of the view.
/// </summary>
public partial class ReportsView : UserControl
{
    public ReportsView()
    {
        InitializeComponent();
    }

    private void OnAutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.Column is not DataGridTextColumn column)
        {
            return;
        }

        // The view model fills NumericHeaders from the report itself before the table is handed over, so
        // by the time a column is generated the answer is already known.
        if (DataContext is ReportsViewModel viewModel && viewModel.NumericHeaders.Contains(e.PropertyName))
        {
            column.CellStyle = (Style)FindResource("NumericCell");
        }
    }
}
