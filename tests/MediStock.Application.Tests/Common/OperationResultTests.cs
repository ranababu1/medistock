using MediStock.Application.Common;
using MediStock.Domain.Common;
using MediStock.Domain.Inventory;

namespace MediStock.Application.Tests.Common;

public class OperationResultTests
{
    [Fact]
    public void Success_reports_no_errors()
    {
        var result = OperationResult.Success();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Errors);
        Assert.Equal(string.Empty, result.Message);
    }

    [Fact]
    public void Fail_carries_every_message()
    {
        var result = OperationResult.Fail("First problem.", "Second problem.");

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains("Second problem.", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fail_never_reports_an_empty_message_list()
    {
        var result = OperationResult.Fail("   ", "");

        Assert.False(result.Succeeded);
        Assert.Single(result.Errors);
        Assert.Equal("The operation failed.", result.Errors[0]);
    }

    [Fact]
    public void Fail_describes_a_domain_rule_violation_verbatim()
    {
        var result = OperationResult.From(new DomainValidationException("Display name is required."));

        Assert.False(result.Succeeded);
        Assert.Equal("Display name is required.", result.Errors[0]);
    }

    [Fact]
    public void Fail_hides_the_details_of_an_unexpected_exception()
    {
        var result = OperationResult.From(new InvalidOperationException("Server=(localdb)\\MSSQLLocalDB;Password=hunter2"));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("hunter2", result.Message, StringComparison.Ordinal);
        Assert.Contains("application log", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generic_success_exposes_the_value_and_no_errors()
    {
        var result = OperationResult<int>.Success(42);

        Assert.True(result.Succeeded);
        Assert.Equal(42, result.Value);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Generic_failure_has_no_value()
    {
        var result = OperationResult<int>.Fail("Nope.");

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.Value);
        Assert.Equal("Nope.", result.Errors[0]);
    }

    [Fact]
    public void Generic_failure_applies_the_same_fallback_message()
        => Assert.Equal("The operation failed.", OperationResult<int>.Fail([]).Errors[0]);

    [Fact]
    public void Generic_failure_describes_a_domain_rule_violation()
        => Assert.Equal(
            "Insufficient stock.",
            OperationResult<int>.From(new InsufficientStockException("Insufficient stock.")).Errors[0]);
}
