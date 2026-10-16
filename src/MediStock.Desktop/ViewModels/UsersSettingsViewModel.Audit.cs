using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediStock.Application.Identity;
using MediStock.Domain.Identity;
using Microsoft.Extensions.Logging;

namespace MediStock.Desktop.ViewModels;

/// <summary>One entry in the audit trail's action picker. The first one means "do not filter".</summary>
public sealed record AuditActionOption(AuditAction? Action, string Label)
{
    /// <summary>Every action the application can record, in the order the enum declares them.</summary>
    public static IReadOnlyList<AuditActionOption> All { get; } =
    [
        new(null, "Any action"),
        .. Enum.GetValues<AuditAction>().Select(action => new AuditActionOption(action, AuditText.Label(action))),
    ];

    public override string ToString() => Label;
}

/// <summary>
/// The audit trail section of the Users &amp; Settings page: what has been done on this installation, by whom,
/// and why.
/// </summary>
/// <remarks>
/// Reading the trail is itself an administrative act and needs <see cref="Permission.ViewAuditTrail"/>, so
/// the section reports the refusal rather than showing nothing. The list is capped and the count is not, so
/// the summary can say "showing 500 of 12,340" — the number that tells an operator there is more to look at
/// is not one they can be shown all of.
/// </remarks>
public sealed partial class UsersSettingsViewModel
{
    private const int AuditPageSize = 500;

    /// <summary>The rows the current query found, newest first unless told otherwise.</summary>
    public ObservableCollection<AuditEntryDto> AuditRows { get; } = [];

    public IReadOnlyList<AuditActionOption> AuditActions => AuditActionOption.All;

    [ObservableProperty]
    private string auditTerm = string.Empty;

    [ObservableProperty]
    private string auditActor = string.Empty;

    [ObservableProperty]
    private AuditActionOption? auditAction;

    [ObservableProperty]
    private DateOnly? auditFrom;

    [ObservableProperty]
    private DateOnly? auditTo;

    [ObservableProperty]
    private bool auditNewestFirst = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoAuditRows))]
    private string auditSummary = string.Empty;

    [ObservableProperty]
    private string? auditMessage;

    [ObservableProperty]
    private AuditEntryDto? selectedAuditEntry;

    public bool HasNoAuditRows => AuditRows.Count == 0;

    /// <summary>
    /// Runs the query and reports what was found against what matched. The two numbers are asked for
    /// separately so the screen can say how much of the trail it is not showing.
    /// </summary>
    [RelayCommand]
    private async Task RunAuditQueryAsync(CancellationToken cancellationToken)
    {
        ClearError();
        AuditMessage = null;

        if (!CanViewAuditTrail)
        {
            AuditRows.Clear();
            AuditSummary = string.Empty;
            AuditMessage = AuditRefusalNote;
            OnPropertyChanged(nameof(HasNoAuditRows));
            return;
        }

        IsBusy = true;

        try
        {
            var query = BuildAuditQuery();

            var rows = await _audit.SearchAsync(query, cancellationToken).ConfigureAwait(true);
            var total = await _audit.CountAsync(query, cancellationToken).ConfigureAwait(true);

            AuditRows.Clear();
            foreach (var row in rows)
            {
                AuditRows.Add(row);
            }

            SelectedAuditEntry = null;

            AuditSummary = AuditRows.Count == 0
                ? "Nothing in the trail matches."
                : total > AuditRows.Count
                    ? $"Showing the newest {AuditRows.Count:N0} of {total:N0} matching row(s). Narrow the range to see the rest."
                    : $"Showing all {AuditRows.Count:N0} matching row(s).";

            OnPropertyChanged(nameof(HasNoAuditRows));

            _logger.LogInformation("Read {Rows} of {Total} audit row(s).", AuditRows.Count, total);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the audit trail failed.");
            AuditRows.Clear();
            AuditSummary = string.Empty;
            AuditMessage = Application.Common.OperationResult.From(ex).Message;
            OnPropertyChanged(nameof(HasNoAuditRows));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Forgets every filter and asks for the whole trail again, newest first.</summary>
    [RelayCommand]
    private async Task ClearAuditFiltersAsync(CancellationToken cancellationToken)
    {
        AuditTerm = string.Empty;
        AuditActor = string.Empty;
        AuditAction = null;
        AuditNewestFirst = true;
        AuditFrom = null;
        AuditTo = null;

        await RunAuditQueryAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Narrows to a single business day, which is the question a support call usually asks.</summary>
    [RelayCommand]
    private async Task ShowAuditTodayAsync(CancellationToken cancellationToken)
    {
        AuditFrom = _clock.BusinessDate;
        AuditTo = _clock.BusinessDate;

        await RunAuditQueryAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Widens to the last week, which is where the panel starts.</summary>
    [RelayCommand]
    private async Task ShowAuditWeekAsync(CancellationToken cancellationToken)
    {
        AuditFrom = _clock.BusinessDate.AddDays(-6);
        AuditTo = _clock.BusinessDate;

        await RunAuditQueryAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The query as the filters describe it, with the range checked before it reaches the database.</summary>
    private AuditQuery BuildAuditQuery()
    {
        var query = new AuditQuery
        {
            Term = string.IsNullOrWhiteSpace(AuditTerm) ? null : AuditTerm,
            Actor = string.IsNullOrWhiteSpace(AuditActor) ? null : AuditActor,
            Action = AuditAction?.Action,
            From = StartOfDay(AuditFrom),
            To = EndOfDay(AuditTo),
            NewestFirst = AuditNewestFirst,
            Take = AuditPageSize,
        };

        // Normalise refuses a range that runs backwards rather than quietly swapping its ends, so the screen
        // reports the mistake instead of answering a different question.
        return query.Normalise();
    }

    /// <summary>The first instant of a local business day, as an instant.</summary>
    private static DateTimeOffset? StartOfDay(DateOnly? date) => date is { } day
        ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue)))
        : null;

    /// <summary>The last instant of a local business day, inclusive.</summary>
    private static DateTimeOffset? EndOfDay(DateOnly? date) => date is { } day
        ? new DateTimeOffset(day.ToDateTime(TimeOnly.MaxValue), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.MaxValue)))
        : null;
}
