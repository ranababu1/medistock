using MediStock.Application.Reporting;
using MediStock.Domain.Identity;

namespace MediStock.Application.Identity.Authorization;

/// <summary>
/// Guards the report run with <see cref="Permission.RunReports"/>.
/// </summary>
/// <remarks>
/// Reports here are the pharmacy's takings, its margins and its stock valuation. That is the owner's and
/// the pharmacist's business, not everyone's, so the cashier role does not have this permission.
/// </remarks>
internal sealed class GuardedReportQueries : IReportQueries
{
    private readonly IReportQueries _inner;
    private readonly IUserSession _session;

    public GuardedReportQueries(IReportQueries inner, IUserSession session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task<ReportTable> RunAsync(ReportRequest request, CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.RunReports, "run this report");
        return _inner.RunAsync(request, cancellationToken);
    }
}

/// <summary>
/// Guards the CSV export with the same permission as running the report, so a refusal cannot be worked
/// around by exporting instead of looking.
/// </summary>
internal sealed class GuardedCsvExporter : ICsvExporter
{
    private readonly ICsvExporter _inner;
    private readonly IUserSession _session;

    public GuardedCsvExporter(ICsvExporter inner, IUserSession session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task<string> ExportAsync(ReportTable report, string path, CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.RunReports, "export this report");
        return _inner.ExportAsync(report, path, cancellationToken);
    }
}
