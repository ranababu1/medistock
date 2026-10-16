using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Common;
using MediStock.Application.Configuration;
using MediStock.Application.Diagnostics;
using MediStock.Application.Identity;
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
    private readonly IUserSession _session;
    private readonly IAuthenticationService _authentication;
    private readonly IClock _clock;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly Dictionary<string, ViewModelBase> _pages = new(StringComparer.Ordinal);

    public ShellViewModel(
        IServiceProvider services,
        INavigationService navigation,
        IDatabaseHealthCheck healthCheck,
        IUserSession session,
        IAuthenticationService authentication,
        IClock clock,
        IOptions<MediStockOptions> options,
        ILogger<ShellViewModel> logger)
        : base("MediStock")
    {
        _services = services;
        _navigation = navigation;
        _healthCheck = healthCheck;
        _session = session;
        _authentication = authentication;
        _clock = clock;
        _logger = logger;

        PharmacyName = options.Value.PharmacyName;
        NearExpiryDays = options.Value.NearExpiryDays;

        NavigationItems = new ObservableCollection<NavigationItem>
        {
            new(NavigationRoutes.Dashboard, "DB", "Dashboard", "Today at a glance"),
            new(NavigationRoutes.NewSale, "NS", "New Sale", "Point of sale and billing"),
            new(NavigationRoutes.SalesHistory, "SH", "Sales History", "Completed invoices"),
            new(NavigationRoutes.Medicines, "MD", "Medicines", "Catalogue and pricing defaults"),
            new(NavigationRoutes.Inventory, "BN", "Inventory & Batches", "Batch stock and expiry"),
            new(NavigationRoutes.Purchases, "PU", "Purchases", "Supplier invoices and receiving"),
            new(NavigationRoutes.Suppliers, "SU", "Suppliers", "Contacts and purchase history"),
            new(NavigationRoutes.Returns, "RT", "Returns & Adjustments", "Sale returns and refunds"),
            new(NavigationRoutes.Reports, "RE", "Reports", "Sales, stock, and valuation"),
            new(NavigationRoutes.Settings, "SE", "Users & Settings", "Accounts, backup, diagnostics"),
        };

        _navigation.NavigationRequested += OnNavigationRequested;
        _session.Changed += OnSessionChanged;

        SelectedNavigationItem = NavigationItems[0];
    }

    /// <summary>
    /// Raised when the signed-in person asks to sign out. The application answers it by returning to the
    /// sign-in window, because it owns the windows; the shell only says that it happened.
    /// </summary>
    public event EventHandler? SignOutRequested;

    public ObservableCollection<NavigationItem> NavigationItems { get; }

    public string PharmacyName { get; }

    public int NearExpiryDays { get; }

    /// <summary>Who is signed in. Falls back to the Windows account so the bar is never blank.</summary>
    public string OperatorName => _session.CurrentUser?.FriendlyName ?? Environment.UserName;

    /// <summary>The signed-in person's role, so it is obvious which set of permissions is in force.</summary>
    public string OperatorRoleText => _session.CurrentUser?.RoleLabel ?? "Not signed in";

    public bool IsSignedIn => _session.IsSignedIn;

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

    /// <summary>
    /// Ends the session. The audit row is written by the authentication service before the signed-in person
    /// is forgotten, so the trail records who stopped using the application and when.
    /// </summary>
    [RelayCommand]
    private async Task SignOutAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _authentication.SignOutAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Still worth returning to the sign-in window: leaving somebody signed in because the audit row
            // could not be written would be the worse of the two failures.
            _logger.LogError(ex, "Signing out did not complete cleanly.");
        }

        SignOutRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reacts to a sign-in or a sign-out. The authentication service raises the notification from whatever
    /// thread its own awaits finished on, and everything done here touches collections that belong to the
    /// interface thread, so the work is marshalled back before it starts.
    /// </summary>
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        var dispatcher = global::System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => OnSessionChanged(sender, e));
            return;
        }

        OnPropertyChanged(nameof(OperatorName));
        OnPropertyChanged(nameof(OperatorRoleText));
        OnPropertyChanged(nameof(IsSignedIn));

        // Every page caches what it decided to show, including which buttons the previous role was allowed
        // to press. Dropping them makes the next visit decide again from the new permissions.
        _pages.Clear();

        if (SelectedNavigationItem is { } item)
        {
            var page = GetOrCreatePage(item);
            CurrentPage = page;
            _ = InitializePageAsync(page);
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
        NavigationRoutes.Suppliers => _services.GetRequiredService<SuppliersViewModel>(),
        NavigationRoutes.Purchases => _services.GetRequiredService<PurchasesViewModel>(),
        NavigationRoutes.Inventory => _services.GetRequiredService<InventoryViewModel>(),
        NavigationRoutes.NewSale => _services.GetRequiredService<NewSaleViewModel>(),
        NavigationRoutes.SalesHistory => _services.GetRequiredService<SalesHistoryViewModel>(),
        NavigationRoutes.Returns => _services.GetRequiredService<ReturnsViewModel>(),
        NavigationRoutes.Reports => _services.GetRequiredService<ReportsViewModel>(),
        NavigationRoutes.Settings => _services.GetRequiredService<UsersSettingsViewModel>(),
        _ => new PlaceholderViewModel(item, "A later milestone", _navigation),
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
