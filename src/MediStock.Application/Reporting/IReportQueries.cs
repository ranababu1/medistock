namespace MediStock.Application.Reporting;

/// <summary>
/// Runs the reports. One entry point keeps the screen, the exporter and the tests agreed about what a
/// report is; each report still has its own query behind it, so its arithmetic can be read and checked
/// on its own terms.
/// </summary>
/// <remarks>
/// Every report states its parameters in <see cref="ReportTable.Subtitle"/> and its rows carry raw
/// values, so a figure can always be traced back to the query and the range that produced it.
/// </remarks>
public interface IReportQueries
{
    /// <summary>Runs one report, normalising the request against the current business date.</summary>
    Task<ReportTable> RunAsync(ReportRequest request, CancellationToken cancellationToken = default);
}
