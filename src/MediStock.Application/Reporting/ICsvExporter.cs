namespace MediStock.Application.Reporting;

/// <summary>
/// Writes a report to disk as CSV. CSV comes first because it is the one format that opens in a
/// spreadsheet, in a text editor and in a script, and it needs no dependency to produce.
/// </summary>
public interface ICsvExporter
{
    /// <summary>Writes the report to <paramref name="path"/>, replacing any existing file.</summary>
    /// <returns>The path written, so the caller can show it to the operator.</returns>
    Task<string> ExportAsync(
        ReportTable report,
        string path,
        CancellationToken cancellationToken = default);
}
