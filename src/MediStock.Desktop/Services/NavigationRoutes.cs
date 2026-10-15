namespace MediStock.Desktop.Services;

/// <summary>
/// Stable route keys for the shell's navigation rail. Using constants instead of
/// free-form strings keeps the view models from drifting apart.
/// </summary>
public static class NavigationRoutes
{
    public const string Dashboard = "dashboard";
    public const string NewSale = "new-sale";
    public const string SalesHistory = "sales-history";
    public const string Medicines = "medicines";
    public const string Inventory = "inventory";
    public const string Purchases = "purchases";
    public const string Suppliers = "suppliers";
    public const string Returns = "returns";
    public const string Reports = "reports";
    public const string Settings = "settings";
}
