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
