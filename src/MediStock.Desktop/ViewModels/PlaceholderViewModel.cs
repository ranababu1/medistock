using CommunityToolkit.Mvvm.Input;
using MediStock.Desktop.Services;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Stands in for a planned screen. Showing the intended scope is more useful to an operator than
/// a blank page, and it keeps the navigation rail honest about what is delivered.
/// </summary>
public sealed partial class PlaceholderViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;

    public PlaceholderViewModel(NavigationItem item, string milestone, INavigationService navigation)
        : base(item.Title)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(navigation);

        Description = item.Description;
        Milestone = milestone;
        _navigation = navigation;
    }

    public string Description { get; }

    public string Milestone { get; }

    public string Headline => $"{Title} is planned for {Milestone}.";

    public IReadOnlyList<string> PlannedScope => Title switch
    {
        "New Sale" =>
        [
            "Scan or search a medicine and add it to the cart.",
            "Allocate batches using first-expiry, first-out.",
            "Checkout as a single database transaction with a unique invoice number.",
        ],
        "Sales History" =>
        [
            "Browse completed invoices by date, operator, and payment method.",
            "Open an invoice to review its lines, batch allocations, and payments.",
        ],
        "Inventory & Batches" =>
        [
            "Browse batches by medicine, supplier, and expiry date.",
            "Post a reason-coded adjustment and reconcile balances against the movement ledger.",
        ],
        "Purchases" =>
        [
            "Record a supplier invoice and receive lines into stock.",
            "Warn on a repeated invoice number instead of silently receiving twice.",
        ],
        "Suppliers" =>
        [
            "Maintain supplier contact, GSTIN, and drug licence details.",
            "Review purchase history and outstanding balances.",
        ],
        "Returns & Adjustments" =>
        [
            "Return sale lines against their original invoice and validate returnable quantity.",
            "Record a disposition before returned stock becomes sellable again.",
        ],
        "Reports" =>
        [
            "Daily sales, sales by date range and payment method, purchase history.",
            "Stock by medicine and batch, low stock, near-expiry and expired stock.",
            "Inventory valuation at cost, with CSV export.",
        ],
        "Users & Settings" =>
        [
            "Named users and role-based permissions.",
            "Audit trail for price changes, adjustments, cancellations, and returns.",
            "Backup and restore workflow plus a support diagnostics view.",
        ],
        _ => [],
    };

    [RelayCommand]
    private void GoToMedicines() => _navigation.NavigateTo(NavigationRoutes.Medicines);

    [RelayCommand]
    private void GoToDashboard() => _navigation.NavigateTo(NavigationRoutes.Dashboard);
}
