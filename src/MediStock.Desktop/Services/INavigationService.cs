namespace MediStock.Desktop.Services;

/// <summary>
/// Lets a page ask the shell to switch to another page without holding a
/// reference to the shell itself.
/// </summary>
public interface INavigationService
{
    event EventHandler<string>? NavigationRequested;

    void NavigateTo(string routeKey);
}

public sealed class NavigationService : INavigationService
{
    public event EventHandler<string>? NavigationRequested;

    public void NavigateTo(string routeKey)
    {
        if (string.IsNullOrWhiteSpace(routeKey))
        {
            return;
        }

        NavigationRequested?.Invoke(this, routeKey);
    }
}
