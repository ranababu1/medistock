using System.Globalization;
using MediStock.Domain.Common;

namespace MediStock.Domain.Tests.Common;

public class MoneyTests
{
    [Theory]
    [InlineData("0.005", "0.01")]
    [InlineData("-0.005", "-0.01")]
    [InlineData("2.345", "2.35")]
    [InlineData("1.0049", "1.00")]
    [InlineData("2", "2.00")]
    [InlineData("0", "0.00")]
    public void Round_uses_half_away_from_zero(string amount, string expected)
    {
        var rounded = Money.Round(Parse(amount));

        Assert.Equal(Parse(expected), rounded);
    }

    [Fact]
    public void Round_never_uses_bankers_rounding()
    {
        // Banker's rounding would return 0.02 for the first case, which would disagree
        // with the till and with a printed invoice.
        Assert.Equal(0.03m, Money.Round(0.025m));
        Assert.Equal(0.02m, Money.Round(0.015m));
    }

    [Fact]
    public void Rounding_policy_is_documented_by_the_constant()
    {
        Assert.Equal(2, Money.DecimalPlaces);
        Assert.Equal(MidpointRounding.AwayFromZero, Money.Rounding);
    }

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
