using System.Windows;
using Microsoft.Win32;
using PhotoManager.Application.Library;

namespace PhotoManager.Wpf;

/// <summary>Troca a paleta (Colors.xaml ↔ ColorsDark.xaml) em tempo real; as telas usam DynamicResource e se atualizam sozinhas.</summary>
public static class ThemeService
{
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        // Só no aplicativo de verdade (nos testes a Application é compartilhada e não deve mudar de paleta).
        if (System.Windows.Application.Current is not App app || !app.Dispatcher.CheckAccess()) return;
        var dark = theme == AppTheme.Dark || (theme == AppTheme.System && SystemPrefersDark());
        var source = new Uri($"pack://application:,,,/PhotoManager;component/Themes/{(dark ? "ColorsDark" : "Colors")}.xaml");
        var dictionaries = app.Resources.MergedDictionaries;
        var index = dictionaries.ToList().FindIndex(d => d.Source?.OriginalString.Contains("Colors", StringComparison.OrdinalIgnoreCase) == true);
        var palette = new ResourceDictionary { Source = source };
        if (index >= 0) dictionaries[index] = palette; else dictionaries.Insert(0, palette);
    }

    /// <summary>"Modo de aplicativo" do Windows (Configurações › Personalização › Cores).</summary>
    public static bool SystemPrefersDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int light && light == 0; }
        catch (System.Security.SecurityException) { return false; }
    }
}
