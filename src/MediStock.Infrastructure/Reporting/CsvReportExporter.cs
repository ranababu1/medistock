using System.Globalization;
using System.Text;
using MediStock.Application.Common;
using MediStock.Application.Reporting;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Reporting;

/// <summary>
/// Writes a report to a CSV file a spreadsheet, a text editor or a script can read.
/// </summary>
/// <remarks>
/// <para>
/// The file leads with the report's title, the parameters it was run with and when it was produced, so a
/// figure in a column can always be traced back to the query and the range that produced it — even after
/// the sheet has been mailed to someone else.
/// </para>
/// <para>
/// Quoting follows RFC 4180: a field is wrapped in double quotes when it holds a separator, a quote or a
/// line break, and inner quotes are doubled. Lines end with CRLF, which is what both Excel and
/// <c>Import-Csv</c> expect. Values are rendered by <see cref="ReportText"/>, the same code the screen
/// uses, so the export can never disagree with what the operator saw.
/// </para>
/// <para>
/// A text field an operator typed is written with a leading apostrophe when it begins with a character
/// a spreadsheet would treat as a formula. That is the only alteration made to a value, and it does not
/// apply to numbers, so a column of amounts stays a column of numbers.
/// </para>
/// </remarks>
internal sealed class CsvReportExporter : ICsvExporter
{
    private static readonly char[] FormulaLeaders = ['=', '+', '@'];

    private readonly IClock _clock;
    private readonly ILogger<CsvReportExporter> _logger;

    public CsvReportExporter(IClock clock, ILogger<CsvReportExporter> logger)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> ExportAsync(
        ReportTable report,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A report has to be written somewhere.", nameof(path));
        }

        var full = Path.GetFullPath(path.Trim());
        var folder = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        // Creating the file fresh rather than appending: a second export of the same report is a
        // replacement, so a folder never accumulates half-updated copies of the same figures.
        await File
            .WriteAllTextAsync(full, Render(report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Exported the {ReportTitle} report ({RowCount} rows) to {Path}.",
            report.Title,
            report.Rows.Count,
            full);

        return full;
    }

    private string Render(ReportTable report)
    {
        var text = new StringBuilder();

        text.Append(Cell(report.Title)).Append("\r\n");
        text.Append(Cell(report.Subtitle)).Append("\r\n");
        text.Append(Cell($"Generated {_clock.UtcNow.ToLocalTime():yyyy-MM-dd HH:mm}")).Append("\r\n");
        text.Append("\r\n");

        var header = report.Columns.Select(column => Cell(column.Header));

        text.Append(string.Join(',', header)).Append("\r\n");

        foreach (var row in report.Rows)
        {
            text.Append(string.Join(',', row.Select(Cell))).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>Renders one value and quotes it if a spreadsheet would otherwise misread it.</summary>
    private static string Cell(object? value)
    {
        var text = ReportText.Format(value);

        if (text.Length == 0)
        {
            return string.Empty;
        }

        // Only text an operator typed can be a formula, and only text needs the apostrophe: a number
        // never starts with one of these characters, so amounts and quantities are left alone.
        if (value is string && FormulaLeaders.Contains(text[0]))
        {
            text = "'" + text;
        }

        return text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r')
            ? string.Concat('"', text.Replace("\"", "\"\""), '"')
            : text;
    }
}
