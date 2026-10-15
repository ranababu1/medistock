using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Common;
using MediStock.Application.Configuration;
using MediStock.Application.Diagnostics;
using MediStock.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// The application shell: owns the navigation rail, the top status bar, and the current page.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly INavigationService _navigation;
    private readonly IDatabaseHealthCheck _healthCheck;
    private readonly IClock _clock;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly Dictionary<string, ViewModelBase> _pages = new(StringComparer.Ordinal);

    public ShellViewModel(
        IServiceProvider services,
        INavigationService navigation,
        IDatabaseHealthCheck healthCheck,
        IClock clock,
        IOptions<MediStockOptions> options,
        ILogger<ShellViewModel> logger)
        : base("MediStock")
    {
        _services = services;
        _navigation = navigation;
        _healthCheck = healthCheck;
        _clock = clock;
        _logger = logger;

        PharmacyName = options.Value.PharmacyName;
        NearExpiryDays = options.Value.NearExpiryDays;

        NavigationItems = new ObservableCollection<NavigationItem>
        {
            new(NavigationRoutes.Dashboard, "DB", "Dashboard", "Today at a glance"),
            new(NavigationRoutes.NewSale, "NS", "New Sale", "Point of sale and billing", isPlanned: true),
            new(NavigationRoutes.SalesHistory, "SH", "Sales History", "Completed invoices", isPlanned: true),
            new(NavigationRoutes.Medicines, "MD", "Medicines", "Catalogue and pricing defaults"),
            new(NavigationRoutes.Inventory, "BN", "Inventory & Batches", "Batch stock and expiry", isPlanned: true),
            new(NavigationRoutes.Purchases, "PU", "Purchases", "Supplier invoices and receiving", isPlanned: true),
            new(NavigationRoutes.Suppliers, "SU", "Suppliers", "Contacts and purchase history", isPlanned: true),
            new(NavigationRoutes.Returns, "RT", "Returns & Adjustments", "Sale returns and write-offs", isPlanned: true),
            new(NavigationRoutes.Reports, "RE", "Reports", "Sales, stock, and valuation", isPlanned: true),
            new(NavigationRoutes.Settings, "SE", "Users & Settings", "Accounts, backup, diagnostics", isPlanned: true),
        };

        _navigation.NavigationRequested += OnNavigationRequested;
        SelectedNavigationItem = NavigationItems[0];
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; }

    public string PharmacyName { get; }

    public int NearExpiryDays { get; }

    public string OperatorName => Environment.UserName;

    public string BusinessDateText =>
        _clock.BusinessDate.ToString("dddd, dd MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);

    public string TimeZoneText => TimeZoneInfo.Local.Id;

    [ObservableProperty]
    private NavigationItem? selectedNavigationItem;

    [ObservableProperty]
    private ViewModelBase? currentPage;

    [ObservableProperty]
    private string databaseStatusText = "Checking database…";

    [ObservableProperty]
    private string databaseDetailText = string.Empty;

    [ObservableProperty]
    private bool isDatabaseConnected;

    [ObservableProperty]
    private bool isCheckingDatabase;

    /// <summary>Probes the database and reports the result in the top status bar.</summary>
    [RelayCommand]
    private async Task CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        IsCheckingDatabase = true;
        try
        {
            var report = await _healthCheck.CheckAsync(cancellationToken).ConfigureAwait(true);

            IsDatabaseConnected = report.IsReachable;
            DatabaseStatusText = report.IsReachable
                ? report.HasPendingMigrations
                    ? $"Connected · {report.PendingMigrations.Count} pending migration(s)"
                    : "Connected"
                : "Database unavailable";

            DatabaseDetailText = report.IsReachable
                ? $"{report.DatabaseName} · {report.Provider} · {report.Elapsed.TotalMilliseconds:F0} ms"
                : report.Error ?? "The database could not be reached.";

            if (!report.IsReachable)
            {
                _logger.LogWarning("Database probe failed: {Error}", report.Error);
            }
        }
        catch (Exception ex)
        {
            IsDatabaseConnected = false;
            DatabaseStatusText = "Database unavailable";
            DatabaseDetailText = "The database connection could not be tested. See the log for details.";
            _logger.LogError(ex, "Database probe threw.");
        }
        finally
        {
            IsCheckingDatabase = false;
        }
    }

    [RelayCommand]
    private void Navigate(NavigationItem? item)
    {
        if (item is not null)
        {
            SelectedNavigationItem = item;
        }
    }

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (value is null)
        {
            return;
        }

        var page = GetOrCreatePage(value);
        CurrentPage = page;
        _ = InitializePageAsync(page);
    }

    private void OnNavigationRequested(object? sender, string routeKey)
    {
        var target = NavigationItems.FirstOrDefault(
            item => string.Equals(item.RouteKey, routeKey, StringComparison.Ordinal));

        if (target is null)
        {
            _logger.LogWarning("Navigation request for unknown route {Route}.", routeKey);
            return;
        }

        SelectedNavigationItem = target;
    }

    private ViewModelBase GetOrCreatePage(NavigationItem item)
    {
        if (_pages.TryGetValue(item.RouteKey, out var existing))
        {
            return existing;
        }

        var page = CreatePage(item);
        _pages[item.RouteKey] = page;
        return page;
    }

    private ViewModelBase CreatePage(NavigationItem item) => item.RouteKey switch
    {
        NavigationRoutes.Dashboard => _services.GetRequiredService<DashboardViewModel>(),
        NavigationRoutes.Medicines => _services.GetRequiredService<MedicinesViewModel>(),
        _ => new PlaceholderViewModel(item, DescribeMilestone(item.RouteKey), _navigation),
    };

    private static string DescribeMilestone(string routeKey) => routeKey switch
    {
        NavigationRoutes.NewSale => "Milestone 3 — Point of sale",
        NavigationRoutes.SalesHistory => "Milestone 3 — Point of sale",
        NavigationRoutes.Inventory => "Milestone 2 — Suppliers and receiving",
        NavigationRoutes.Purchases => "Milestone 2 — Suppliers and receiving",
        NavigationRoutes.Suppliers => "Milestone 2 — Suppliers and receiving",
        NavigationRoutes.Returns => "Milestone 4 — Returns, expiry, and reports",
        NavigationRoutes.Reports => "Milestone 4 — Returns, expiry, and reports",
        NavigationRoutes.Settings => "Milestone 5 — Users, security, and operations",
        _ => "A later milestone",
    };

    private async Task InitializePageAsync(ViewModelBase page)
    {
        try
        {
            await page.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialising page {Page} failed.", page.GetType().Name);
            page.ErrorMessage = "This screen could not be loaded. See the application log for details.";
        }
    }
}
