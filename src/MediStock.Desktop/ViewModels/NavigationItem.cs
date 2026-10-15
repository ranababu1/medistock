using CommunityToolkit.Mvvm.ComponentModel;

namespace MediStock.Desktop.ViewModels;

/// <summary>
/// One entry in the shell's navigation rail. <see cref="IsPlanned"/> marks screens that are part
/// of the product plan but not implemented yet, so the rail can show the full target scope
/// without pretending the features exist.
/// </summary>
public sealed partial class NavigationItem : ObservableObject
{
    public NavigationItem(
        string routeKey,
        string monogram,
        string title,
        string description,
        bool isPlanned = false)
    {
        RouteKey = routeKey;
        Monogram = monogram;
        Title = title;
        Description = description;
        IsPlanned = isPlanned;
    }

    public string RouteKey { get; }

    /// <summary>Two-character chip shown at the left of the rail entry.</summary>
    public string Monogram { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>True when the milestone that delivers this screen has not shipped yet.</summary>
    public bool IsPlanned { get; }
}
