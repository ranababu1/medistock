namespace MediStock.Application.Reporting;

/// <summary>
/// One column of a report: what it is called and whether its values are numbers, which decides how the
/// viewer and the CSV file align it.
/// </summary>
public sealed record ReportColumn(string Header, bool IsNumeric = false);

/// <summary>
/// A finished report: a title, a line saying exactly what was asked for, and rectangular rows.
/// </summary>
/// <remarks>
/// Reports are handed back as plain tables of <see cref="object"/> values rather than as formatted text.
/// The viewer and the CSV exporter decide how to render a date or an amount, so the totals stay
/// comparable as numbers and the tests can assert on money rather than on punctuation. Every report
/// states its own parameters in <see cref="Subtitle"/> because a figure on its own, with no date range
/// attached, cannot be checked by the person reading it.
/// </remarks>
public sealed record ReportTable(
    string Title,
    string Subtitle,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows)
{
    public bool IsEmpty => Rows.Count == 0;

    public static ReportTable Empty(string title, string subtitle, params ReportColumn[] columns)
        => new(title, subtitle, columns, []);
}
