namespace MediStock.Domain.Common;

/// <summary>
/// Small, dependency-free argument checks shared by domain entities. Kept deliberately
/// narrow: it only covers the rules the pharmacy domain actually repeats.
/// </summary>
public static class Guard
{
    public static string Required(string? value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException($"{fieldName} is required.");
        }

        var trimmed = value.Trim();
        if (maxLength > 0 && trimmed.Length > maxLength)
        {
            throw new DomainValidationException($"{fieldName} must be {maxLength} characters or fewer.");
        }

        return trimmed;
    }

    public static string? Optional(string? value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (maxLength > 0 && trimmed.Length > maxLength)
        {
            throw new DomainValidationException($"{fieldName} must be {maxLength} characters or fewer.");
        }

        return trimmed;
    }

    public static decimal Positive(decimal value, string fieldName)
    {
        if (value <= 0m)
        {
            throw new DomainValidationException($"{fieldName} must be greater than zero.");
        }

        return value;
    }

    public static decimal NonNegative(decimal value, string fieldName)
    {
        if (value < 0m)
        {
            throw new DomainValidationException($"{fieldName} cannot be negative.");
        }

        return value;
    }

    public static decimal? NonNegative(decimal? value, string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        return NonNegative(value.Value, fieldName);
    }

    /// <summary>Normalises a value for case-insensitive uniqueness checks.</summary>
    public static string? NormaliseForComparison(string? value) => value?.Trim().ToLowerInvariant();
}
