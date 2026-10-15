using System.Globalization;
using System.Windows.Data;

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

/// <summary>Renders the catalogue active flag as operator-facing words rather than True/False.</summary>
public sealed class ActiveStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Active" : "Inactive";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
