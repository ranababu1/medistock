using System.IO;
using System.Reflection;
using System.Windows;
using MediStock.Application.Configuration;
using MediStock.Application.Diagnostics;
using MediStock.Desktop.Logging;
using MediStock.Desktop.Services;
using MediStock.Desktop.ViewModels;
using MediStock.Desktop.Views;
using MediStock.Infrastructure;
using MediStock.Infrastructure.Persistence.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediStock.Desktop;

/// <summary>
/// Application entry point. Builds the dependency injection container, prepares the database, and
/// shows the shell. Nothing here touches business logic; the layers below own that.
/// </summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;
    private ILogger<App>? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Hold the application open until the shell window is on screen, so a startup failure can
        // be reported instead of silently exiting.
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        // Nothing below this line is allowed to take the process down without leaving a record. The
        // log file is the thing a support call starts from, and the operator is told where it is.
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _host = BuildHost(e.Args);
            _logger = _host.Services.GetRequiredService<ILogger<App>>();

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            _logger.LogInformation("MediStock {Version} starting on {Machine}.", version, Environment.MachineName);

            var options = _host.Services.GetRequiredService<IOptions<MediStockOptions>>().Value;
            var connectionString = DependencyInjection.ResolveConnectionString(
                _host.Services.GetRequiredService<IConfiguration>());
            DependencyInjection.LogDatabaseTarget(_logger, connectionString);

            await PrepareDatabaseAsync(options).ConfigureAwait(true);

            // Nobody is signed in yet — the seeder signs out deliberately — so the first window asks who
            // this is. Everything the shell offers is decided from that answer.
            if (!ShowSignIn(null))
            {
                Shutdown(0);
                return;
            }

            var shell = _host.Services.GetRequiredService<ShellViewModel>();
            var window = new ShellWindow { DataContext = shell };

            shell.SignOutRequested += (_, _) => ReturnToSignIn(window, shell);

            MainWindow = window;
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            window.Show();

            await shell.CheckDatabaseCommand.ExecuteAsync(null).ConfigureAwait(true);
            _logger.LogInformation("Shell displayed.");
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("MediStock shutting down with exit code {ExitCode}.", e.ApplicationExitCode);
        _host?.Dispose();
        base.OnExit(e);
    }

    private static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Machine-specific overrides. This file is git-ignored, and is the documented place for a
        // real connection string or credentials; nothing secret is tracked in appsettings.json.
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

#if DEBUG
        builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);
#endif

        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(new FileLoggerProvider());
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

        builder.Services.AddMediStockInfrastructure(builder.Configuration);

        builder.Services.AddSingleton<INavigationService, NavigationService>();
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddTransient<SignInViewModel>();
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<MedicinesViewModel>();
        builder.Services.AddTransient<SuppliersViewModel>();
        builder.Services.AddTransient<PurchasesViewModel>();
        builder.Services.AddTransient<InventoryViewModel>();
        builder.Services.AddTransient<NewSaleViewModel>();
        builder.Services.AddTransient<SalesHistoryViewModel>();
        builder.Services.AddTransient<ReturnsViewModel>();
        builder.Services.AddTransient<ReportsViewModel>();
        builder.Services.AddTransient<UsersSettingsViewModel>();

        return builder.Build();
    }

    private async Task PrepareDatabaseAsync(MediStockOptions options)
    {
        var health = _host!.Services.GetRequiredService<IDatabaseHealthCheck>();
        var report = await health.CheckAsync().ConfigureAwait(true);

        if (!report.IsReachable)
        {
            // Not fatal: the shell still opens and shows the failure in the status bar, which is
            // the Milestone 0 acceptance behaviour.
            _logger?.LogWarning("The database is not reachable yet: {Error}", report.Error);
            return;
        }

        // Logged on success as well as failure, so a support log always records whether the
        // application reached its database rather than leaving it to be inferred from the absence
        // of an error.
        _logger?.LogInformation(
            "Database reachable: {Database} via {Provider}, {Pending} pending migration(s), probe took {Elapsed}.",
            report.DatabaseName,
            report.Provider,
            report.PendingMigrations.Count,
            report.Elapsed);

        _logger?.LogDebug("SQL Server reports: {Version}.", report.ServerVersion);

        if (options.ApplyMigrationsOnStartup && report.HasPendingMigrations)
        {
            var applied = await health.ApplyPendingMigrationsAsync().ConfigureAwait(true);
            _logger?.LogInformation("Applied {Count} database migration(s).", applied);
        }

        if (!options.SeedDevelopmentData)
        {
            return;
        }

        var seeder = _host.Services.GetRequiredService<DevelopmentDataSeeder>();
        var movements = await seeder.SeedAsync().ConfigureAwait(true);

        if (movements > 0)
        {
            _logger?.LogInformation("Development sample data loaded ({Movements} stock movements).", movements);
        }
    }

    /// <summary>
    /// Presents the sign-in window. Returns true once somebody is signed in, and false when the operator
    /// closed the window instead — which means the application should exit rather than open a shell that
    /// nobody is allowed to use.
    /// </summary>
    private bool ShowSignIn(Window? owner)
    {
        var viewModel = _host!.Services.GetRequiredService<SignInViewModel>();
        return SignInWindow.Ask(viewModel, owner);
    }

    /// <summary>
    /// Sends the application back to the sign-in window after a sign-out. The shell is only hidden, not
    /// closed, so the operator keeps their place on the navigation rail if they sign back in.
    /// </summary>
    private void ReturnToSignIn(Window shellWindow, ShellViewModel shell)
    {
        shellWindow.Hide();

        if (ShowSignIn(shellWindow))
        {
            shellWindow.Show();
            shellWindow.Activate();
            return;
        }

        _logger?.LogInformation("Sign-in window closed without a sign-in; shutting down.");
        shellWindow.Close();
    }

    private void ReportStartupFailure(Exception exception)
    {
        _logger?.LogCritical(exception, "MediStock failed to start.");

        var logHint = $"{Environment.NewLine}{Environment.NewLine}A log was written to:{Environment.NewLine}{FileLoggerProvider.CurrentLogFile}";

        MessageBox.Show(
            $"MediStock could not start.{Environment.NewLine}{Environment.NewLine}{exception.Message}{logHint}",
            "MediStock",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        Shutdown(1);
    }

    /// <summary>
    /// The last line of defence for the interface thread. An error nobody caught means the screen may no
    /// longer describe the database, so the application stops rather than carrying on with numbers nobody
    /// can trust — but it stops having written the stack to the log and said so, instead of vanishing.
    /// </summary>
    private void OnUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "An unhandled error stopped the interface.");

        MessageBox.Show(
            $"MediStock has to close because of an unexpected error.{Environment.NewLine}{Environment.NewLine}" +
            $"{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}" +
            $"Nothing after this point was saved. The details were written to:{Environment.NewLine}" +
            $"{FileLoggerProvider.CurrentLogFile}",
            "MediStock",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
        Shutdown(1);
    }

    /// <summary>
    /// A failure on a thread the interface does not own cannot be recovered from: the process is going down
    /// either way, so all that is left to do is to make sure the log says why.
    /// </summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        _logger?.LogCritical(e.ExceptionObject as Exception, "An unhandled error stopped a background thread.");

    /// <summary>A fault nobody awaited is reported when it is collected, which is late but is still a record.</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "A background task failed and nobody was waiting for the result.");
        e.SetObserved();
    }
}

