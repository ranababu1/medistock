using MediStock.Application.Identity;
using MediStock.Domain.Identity;

namespace MediStock.Application.Tests.Identity;

/// <summary>
/// How the audit trail is filtered. Two of these rules protect the database rather than the operator: the
/// row count is clamped so no screen can ask for the whole table, and a range that runs backwards is refused
/// rather than quietly swapped, because a swapped range would show the operator a result their filter did
/// not describe.
/// </summary>
public sealed class AuditQueryTests
{
    [Fact]
    public void The_default_query_is_the_newest_five_hundred_rows()
    {
        var query = new AuditQuery();

        Assert.True(query.NewestFirst);
        Assert.Equal(500, query.Take);
        Assert.Null(query.From);
        Assert.Null(query.To);
        Assert.Null(query.Action);
        Assert.Null(query.Actor);
        Assert.Null(query.EntityType);
        Assert.Null(query.Term);
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(-1, 500)]
    [InlineData(-5000, 500)]
    [InlineData(1, 1)]
    [InlineData(250, 250)]
    [InlineData(AuditQuery.MaxResults, AuditQuery.MaxResults)]
    [InlineData(AuditQuery.MaxResults + 1, AuditQuery.MaxResults)]
    [InlineData(int.MaxValue, AuditQuery.MaxResults)]
    public void The_row_count_is_clamped_to_what_a_screen_may_ask_for(int asked, int expected)
    {
        Assert.Equal(expected, new AuditQuery { Take = asked }.Normalise().Take);
    }

    [Theory]
    [InlineData("  invoice 1042 ", "invoice 1042")]
    [InlineData("  ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_search_term_is_trimmed_and_blank_means_no_filter(string? typed, string? expected)
    {
        Assert.Equal(expected, new AuditQuery { Term = typed }.Normalise().Term);
    }

    [Theory]
    [InlineData("  admin ", "admin")]
    [InlineData("\t", null)]
    public void An_actor_is_trimmed_and_blank_means_no_filter(string? typed, string? expected)
    {
        Assert.Equal(expected, new AuditQuery { Actor = typed }.Normalise().Actor);
    }

    [Theory]
    [InlineData("  Medicine ", "Medicine")]
    [InlineData("   ", null)]
    public void An_entity_type_is_trimmed_and_blank_means_no_filter(string? typed, string? expected)
    {
        Assert.Equal(expected, new AuditQuery { EntityType = typed }.Normalise().EntityType);
    }

    [Fact]
    public void A_range_that_runs_backwards_is_refused_rather_than_swapped()
    {
        var query = new AuditQuery
        {
            From = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(5.5)),
            To = new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.FromHours(5.5)),
        };

        var thrown = Assert.Throws<ArgumentException>(query.Normalise);

        Assert.StartsWith("The start of the range is after the end of it.", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_range_of_one_instant_is_allowed()
    {
        var instant = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(5.5));

        var normalised = new AuditQuery { From = instant, To = instant }.Normalise();

        Assert.Equal(instant, normalised.From);
        Assert.Equal(instant, normalised.To);
    }

    [Fact]
    public void A_half_open_range_is_allowed()
    {
        var query = new AuditQuery { From = DateTimeOffset.UnixEpoch };

        Assert.Equal(DateTimeOffset.UnixEpoch, query.Normalise().From);
    }

    [Fact]
    public void Normalising_keeps_the_filter_that_was_asked_for()
    {
        var query = new AuditQuery
        {
            Action = AuditAction.PriceChanged,
            NewestFirst = false,
            Take = 25,
        };

        var normalised = query.Normalise();

        Assert.Equal(AuditAction.PriceChanged, normalised.Action);
        Assert.False(normalised.NewestFirst);
        Assert.Equal(25, normalised.Take);
    }

    [Fact]
    public void Normalising_twice_changes_nothing_the_second_time()
    {
        var once = new AuditQuery { Term = "  invoice ", Take = 9_000, Actor = "  admin " }.Normalise();

        Assert.Equal(once, once.Normalise());
    }
}
