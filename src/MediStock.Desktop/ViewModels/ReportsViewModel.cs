using System.Data;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Common;
using MediStock.Application.Configuration;
using MediStock.Application.Reporting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// Reports: pick one, say what it covers, read it, export it.
/// </summary>
/// <remarks>
/// <para>
/// The screen runs whatever <see cref="ReportDefinitions.All"/> offers and shapes the controls from the
/// definition's own flags, so adding a report does not mean editing this screen. It displays the report's
/// title and the subtitle the query produced, which states the range that was actually used rather than
/// the one that was typed.
/// </para>
/// <para>
/// Cells are rendered through <see cref="ReportText"/>, the same code the CSV export uses, so the figures
/// on screen and the figures in the file cannot differ. The table is rebuilt from the raw
/// <see cref="ReportTable"/> on every run and every export re-runs nothing: what is exported is exactly
/// what is on screen.
/// </para>
/// </remarks>
public sealed partial class ReportsViewModel : ViewModelBase
{
    private readonly IReportQueries _reports;
    private readonly ICsvExporter _csv;
    private readonly IClock _clock;
    private readonly IOptions<MediStockOptions> _options;
    private readonly ILogger<ReportsViewModel> _logger;

    /// <summary>The report as the queries produced it, which is what gets exported.</summary>
    private ReportTable? _current;

    public ReportsViewModel(
        IReportQueries reports,
        ICsvExporter csv,
        IClock clock,
        IOptions<MediStockOptions> options,
        ILogger<ReportsViewModel> logger)
        : base("Reports")
    {
        ArgumentNullException.ThrowIfNull(clock);

        _reports = reports;
        _csv = csv;
        _clock = clock;
        _options = options;
        _logger = logger;

        Definitions = ReportDefinitions.All;

        // A week of trading is the question most often asked of a report; the operator widens or narrows
        // from there.
        From = clock.BusinessDate.AddDays(-6);
        To = clock.BusinessDate;
        HorizonDays = ReportRequest.DefaultHorizonDays;
        Take = ReportRequest.DefaultTake;

        SelectedDefinition = Definitions[0];

        PropertyChanged += OnOwnPropertyChanged;
    }

    public IReadOnlyList<ReportDefinition> Definitions { get; }

    /// <summary>
    /// Headers of the columns holding numbers, so the table can line its figures up on the right without
    /// the view having to know what any particular report contains.
    /// </summary>
    public HashSet<string> NumericHeaders { get; } = new(StringComparer.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefinitionDescription))]
    [NotifyPropertyChangedFor(nameof(UsesDateRange))]
    [NotifyPropertyChangedFor(nameof(UsesHorizon))]
    [NotifyPropertyChangedFor(nameof(UsesTake))]
    private ReportDefinition? selectedDefinition;

    [ObservableProperty]
    private DateOnly? from;

    [ObservableProperty]
    private DateOnly? to;

    [ObservableProperty]
    private int horizonDays;

    [ObservableProperty]
    private int take;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    [NotifyPropertyChangedFor(nameof(RowSummary))]
    [NotifyPropertyChangedFor(nameof(IsEmptyReport))]
    private DataTable? table;

    [ObservableProperty]
    private string reportTitle = string.Empty;

    [ObservableProperty]
    private string reportSubtitle = string.Empty;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyReport))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool hasRun;

    public bool IsNotBusy => !IsBusy;

    public bool HasRows => Table?.Rows.Count > 0;

    /// <summary>True when a report has been run and it had nothing to say, which is a result, not an error.</summary>
    public bool IsEmptyReport => HasRun && !HasRows;

    /// <summary>True while the screen is still waiting to be asked for something.</summary>
    public bool IsIdle => !HasRun;

    public bool UsesDateRange => SelectedDefinition?.UsesDateRange == true;

    public bool UsesHorizon => SelectedDefinition?.UsesHorizonDays == true;

    public bool UsesTake => SelectedDefinition?.UsesTake == true;

    public string DefinitionDescription => SelectedDefinition?.Description ?? string.Empty;

    public string RowSummary => Table is null
        ? "Choose a report and run it."
        : HasRows
            ? $"{Table.Rows.Count:N0} row(s)"
            : "No rows";

    /// <summary>The export path the next export would write to, shown before it is written.</summary>
    public string ExportFolder => OutputFolder.Resolve(_options.Value.ReportFolder, @"MediStock\Reports");

    public override Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(cancellationToken);

    [RelayCommand]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        ClearError();
        StatusMessage = null;
        IsBusy = true;

        var definition = SelectedDefinition ?? Definitions[0];

        try
        {
            var request = new ReportRequest
            {
                Kind = definition.Kind,
                From = definition.UsesDateRange ? From : null,
                To = definition.UsesDateRange ? To : null,
                HorizonDays = definition.UsesHorizonDays ? HorizonDays : null,
                Take = definition.UsesTake ? Take : null,
            };

            var report = await _reports.RunAsync(request, cancellationToken).ConfigureAwait(true);

            _current = report;
            Show(report);

            _logger.LogInformation(
                "Ran the {Report} report: {Rows} row(s), {Subtitle}.",
                report.Title,
                report.Rows.Count,
                report.Subtitle);
        }
        catch (Exception ex)
        {
            _current = null;
            Table = null;
            HasRun = false;
            _logger.LogError(ex, "Running the {Report} report failed.", definition.Title);
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Writes what is on screen to a CSV file. The file is a replacement, not an addition, so exporting
    /// the same report twice leaves one copy of its figures rather than two.
    /// </summary>
    [RelayCommand]
    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        ClearError();
        StatusMessage = null;

        var report = _current;

        if (report is null)
        {
            ErrorMessage = "Run a report before exporting it.";
            return;
        }

        IsBusy = true;

        try
        {
            var name = OutputFolder.SafeFileName($"{report.Title} {report.Subtitle}", "report");
            var path = Path.Combine(ExportFolder, name + ".csv");

            var written = await _csv.ExportAsync(report, path, cancellationToken).ConfigureAwait(true);

            StatusMessage = $"Exported {report.Rows.Count:N0} row(s) of {report.Title} to {written}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exporting the {Report} report failed.", report.Title);
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Narrows the range to the business date the operator is working on.</summary>
    [RelayCommand]
    private void ShowToday()
    {
        From = _clock.BusinessDate;
        To = From;
    }

    /// <summary>Widens the range to the last thirty days.</summary>
    [RelayCommand]
    private void ShowLastMonth()
    {
        To = _clock.BusinessDate;
        From = To.Value.AddDays(-29);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _current = null;
        Table = null;
        HasRun = false;
        ReportTitle = string.Empty;
        ReportSubtitle = string.Empty;
        ClearError();
        StatusMessage = null;
    }

    /// <summary>Renders the raw report into the table the grid displays.</summary>
    private void Show(ReportTable report)
    {
        var source = new DataTable { Locale = CultureInfo.CurrentCulture };
        var headers = new List<string>(report.Columns.Count);

        NumericHeaders.Clear();

        foreach (var column in report.Columns)
        {
            // A DataTable will not hold two columns of the same name, so a repeated header is suffixed
            // rather than silently renamed by the grid.
            var header = Unique(column.Header, headers);
            headers.Add(header);

            source.Columns.Add(new DataColumn(header, typeof(string)));

            if (column.IsNumeric)
            {
                NumericHeaders.Add(header);
            }
        }

        foreach (var row in report.Rows)
        {
            var values = new object?[headers.Count];

            for (var index = 0; index < headers.Count; index++)
            {
                values[index] = index < row.Count ? ReportText.Format(row[index]) : string.Empty;
            }

            source.Rows.Add(values);
        }

        ReportTitle = report.Title;
        ReportSubtitle = report.Subtitle;
        Table = source;
        HasRun = true;
    }

    private static string Unique(string header, List<string> taken)
    {
        var name = string.IsNullOrWhiteSpace(header) ? "Column" : header.Trim();

        if (!taken.Contains(name, StringComparer.Ordinal))
        {
            return name;
        }

        // A report with two "Quantity" columns is a report that needs its headings looked at, but the
        // viewer still has to show it.
        var suffix = 2;
        while (taken.Contains($"{name} ({suffix})", StringComparer.Ordinal))
        {
            suffix++;
        }

        return $"{name} ({suffix})";
    }

    partial void OnHasRunChanged(bool value) => OnPropertyChanged(nameof(IsEmptyReport));

    private void OnOwnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(IsBusy))
        {
            return;
        }

        OnPropertyChanged(nameof(IsNotBusy));
    }
}
