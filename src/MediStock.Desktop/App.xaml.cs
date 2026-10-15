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

            var shell = _host.Services.GetRequiredService<ShellViewModel>();
            var window = new ShellWindow { DataContext = shell };

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
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<MedicinesViewModel>();

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
}

