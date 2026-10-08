namespace PhotoManager.Application.Navigation;

public sealed class NavigationService : INavigationService
{
    private readonly IReadOnlyList<NavigationItem> _items =
    [
        new("Library", "Biblioteca", "▦"),
        new("Metadata", "Metadados", "✎"),
        new("Microstock", "Microstock", "↗"),
        new("Transfer", "Transferência", "⇄"),
        new("Tools", "Ferramentas", "⚙"),
        new("Settings", "Configurações", "☷")
    ];

    public IReadOnlyList<NavigationItem> Items => _items;
    public string CurrentKey { get; private set; } = "Library";
    public event EventHandler<string>? Navigated;

    public void Navigate(string key)
    {
        if (_items.All(item => item.Key != key) || CurrentKey == key)
        {
            return;
        }

        CurrentKey = key;
        Navigated?.Invoke(this, key);
    }
}
