namespace PhotoManager.Application.Navigation;

public interface INavigationService
{
    IReadOnlyList<NavigationItem> Items { get; }
    string CurrentKey { get; }
    event EventHandler<string>? Navigated;
    void Navigate(string key);
}
