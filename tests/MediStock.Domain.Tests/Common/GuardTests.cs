using MediStock.Domain.Common;

namespace MediStock.Domain.Tests.Common;

public class GuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Required_rejects_missing_text(string? value)
    {
        var exception = Assert.Throws<DomainValidationException>(() => Guard.Required(value, 10, "Batch number"));

        Assert.Contains("Batch number is required.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_trims_surrounding_whitespace()
        => Assert.Equal("PCM2411", Guard.Required("  PCM2411  ", 64, "Batch number"));

    [Fact]
    public void Required_rejects_text_longer_than_the_limit()
    {
        var exception = Assert.Throws<DomainValidationException>(() => Guard.Required(new string('x', 11), 10, "Batch number"));

        Assert.Contains("10 characters or fewer", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Optional_normalises_blank_text_to_null(string? value)
        => Assert.Null(Guard.Optional(value, 10, "Reference"));

    [Fact]
    public void Positive_rejects_zero_and_negative()
    {
        Assert.Throws<DomainValidationException>(() => Guard.Positive(0m, "Quantity"));
        Assert.Throws<DomainValidationException>(() => Guard.Positive(-1m, "Quantity"));
        Assert.Equal(0.5m, Guard.Positive(0.5m, "Quantity"));
    }

    [Fact]
    public void NonNegative_allows_zero_but_not_negative()
    {
        Assert.Equal(0m, Guard.NonNegative(0m, "Purchase cost"));
        Assert.Throws<DomainValidationException>(() => Guard.NonNegative(-0.01m, "Purchase cost"));
    }

    [Fact]
    public void NonNegative_nullable_passes_null_through()
    {
        Assert.Null(Guard.NonNegative(null, "Reorder threshold"));
        Assert.Equal(5m, Guard.NonNegative(5m, "Reorder threshold"));
    }

    [Theory]
    [InlineData(" Paracetamol ", "paracetamol")]
    [InlineData(null, null)]
    public void NormaliseForComparison_is_case_and_whitespace_insensitive(string? input, string? expected)
        => Assert.Equal(expected, Guard.NormaliseForComparison(input));
}
