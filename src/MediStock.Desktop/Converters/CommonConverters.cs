using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MediStock.Application.Reporting;

namespace MediStock.Desktop.Converters;

/// <summary>
/// Bridges <see cref="DateOnly"/> — the domain's business-date type — to the <see cref="DateTime"/>
/// that WPF's <c>DatePicker</c> speaks. Clearing the picker leaves the source untouched rather than
/// inventing a date.
/// </summary>
public sealed class DateOnlyToDateTimeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        DateTime dateTime => dateTime,
        _ => null,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        _ => Binding.DoNothing,
    };
}

/// <summary>True when the bound value is not null. Used to gate actions on a selection.</summary>
public sealed class NotNullToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Hides an element whose bound value is null. A banner that carries a message is shown by having one.
/// </summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Shows an enum column as words an operator reads. The wording lives in one place, so a grid cell and an
/// exported report call the same value by the same name.
/// </summary>
public sealed class EnumTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Enum word ? ReportText.Describe(word) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Renders the catalogue active flag as operator-facing words rather than True/False.</summary>
public sealed class ActiveStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Active" : "Inactive";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Shows a stored instant on the pharmacy's own clock, which is the clock the receipt already prints.
/// </summary>
/// <remarks>
/// <para>
/// Instants are stored in UTC. Binding one straight into a grid renders the UTC wall clock, so a sale rung up
/// at 14:30 in India read 09:00 on the Sales History screen while the invoice beside it said 14:30 — the same
/// sale, two answers. The receipt exporter has always converted (<c>TextReceiptExporter.Local</c>); this
/// converter brings the screens in line with it.
/// </para>
/// <para>
/// The zone used is the machine's, which is where a single-PC installation runs and which is therefore the
/// zone the receipts are printed in. The business date used for daily figures is a separate question and is
/// answered by <c>IClock</c> from configuration.
/// </para>
/// </remarks>
public sealed class InstantTextConverter : IValueConverter
{
    /// <summary>The format used when the binding does not pass one as its parameter.</summary>
    public const string DefaultFormat = "dd/MM/yyyy HH:mm";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var format = parameter as string is { Length: > 0 } supplied ? supplied : DefaultFormat;

        return value switch
        {
            DateTimeOffset instant => instant.ToLocalTime().ToString(format, CultureInfo.InvariantCulture),
            DateTime time => time.ToString(format, CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
