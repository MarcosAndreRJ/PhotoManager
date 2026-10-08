using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoManager.Application.Navigation;
using PhotoManager.Wpf;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

/// <summary>Carrega a janela real (XAML, estilos, bindings) numa thread STA para detectar erros que só aparecem em runtime.</summary>
/// <summary>Testes que criam janelas WPF compartilham a Application do processo: executam em série para evitar corridas entre threads STA.</summary>
[Collection("WpfUi")]
public sealed class UiSmokeTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); // como no Application.Run
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is System.Windows.Markup.XamlParseException xaml) throw new InvalidOperationException($"XAML {xaml.BaseUri} linha {xaml.LineNumber}:{xaml.LinePosition} - {xaml.Message}", failure);
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    /// <summary>Espera a tarefa bombeando o Dispatcher (bloquear a thread de UI com GetResult causaria deadlock nos awaits).</summary>
    private static void Await(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void EnsureApplication()
    {
        // O assembly da UI é um WinExe: o pack URI "application" precisa de uma Application com esse assembly como principal.
        var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count > 0) return;
        foreach (var name in new[] { "Colors", "Typography", "Buttons", "Inputs", "Cards", "Tabs", "Tree" }) // mesma ordem do App.xaml
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/PhotoManager;component/Themes/{name}.xaml") });
    }

    private MainWindow CreateWindow()
    {
        var navigation = new NavigationService();
        var viewModel = new MainViewModel(navigation, _env.Catalog, _env.Thumbnails, _env.Organization, _env.FileOperations, new PhotoManager.Infrastructure.Metadata.MetadataExtractorReader(), _env.MetadataEditing);
        return new MainWindow(viewModel);
    }

    private static string DumpTree(DependencyObject root)
    {
        var lines = new List<string>();
        void Walk(DependencyObject node, int depth)
        {
            lines.Add(new string(' ', depth) + node.GetType().Name);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i), depth + 1);
        }
        Walk(root, 0);
        return string.Join("\n", lines.TakeLast(25));
    }

    private static string FindBrokenForeground(DependencyObject root)
    {
        var broken = new List<string>();
        void Walk(DependencyObject node, string path)
        {
            if (node is TextBlock text)
            {
                try { _ = text.Foreground; } catch (Exception) { broken.Add($"{path}/{text.Text}"); }
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i), $"{path}/{node.GetType().Name}");
        }
        Walk(root, string.Empty);
        return string.Join("\n", broken);
    }

    [Fact]
    public void MainWindow_LoadsAndLaysOut_WithPopulatedLibrary()
    {
        _env.CreatePng("a.png"); _env.CreatePng("b.png");
        RunSta(() =>
        {
            EnsureApplication();
            var window = CreateWindow();
            var library = (LibraryViewModel)((MainViewModel)window.DataContext).CurrentView;
            window.Show();
            try
            {
                Await(library.ImportFolderAsync(_env.Photos));
                Pump(400);
                try { window.UpdateLayout(); }
                catch (Exception ex) { throw new InvalidOperationException("Layout falhou. Árvore realizada:\n" + DumpTree(window), ex); }
                var broken = FindBrokenForeground(window);
                Assert.True(broken.Length == 0, "TextBlocks com Foreground inválido:\n" + broken);
                Assert.True(library.Photos.Count == 2, library.StatusText);
                library.SelectedPhoto = library.Photos[0];
                Pump(400);
                window.UpdateLayout();
                Assert.True(string.IsNullOrEmpty(FindBrokenForeground(window)));
                for (var wait = 0; library.PreviewImage is null && wait < 80; wait++) Pump(100);   // decodificação em segundo plano: espera com limite (a máquina pode estar carregada)
                Assert.NotNull(library.PreviewImage);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ThemeControlTemplates_Instantiate()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var failures = new List<string>();
            void Check(string name, FrameworkTemplate? template)
            {
                if (template is null) return;
                try { template.LoadContent(); } catch (Exception ex) { failures.Add($"{name}: {ex.GetBaseException().Message}"); }
            }
            void Visit(ResourceDictionary dictionary)
            {
                foreach (var child in dictionary.MergedDictionaries) Visit(child);
                foreach (var key in dictionary.Keys)
                {
                    var value = dictionary[key];
                    if (value is FrameworkTemplate template) Check(key.ToString()!, template);
                    if (value is Style style)
                        foreach (var setter in style.Setters.OfType<Setter>().Where(s => s.Property == Control.TemplateProperty))
                            Check($"{key}/Template", setter.Value as FrameworkTemplate);
                }
            }
            Visit(System.Windows.Application.Current.Resources);
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        });
    }

    [Fact]
    public void LibraryView_TemplatesInstantiate()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var view = new LibraryView();
            var failures = new List<string>();
            foreach (var key in view.Resources.Keys)
            {
                if (view.Resources[key] is not DataTemplate template) continue;
                try { template.LoadContent(); } catch (Exception ex) { failures.Add($"{key}: {ex.GetBaseException().Message}"); }
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        });
    }

    [Fact]
    public void Navigation_SwitchesAreas_AndKeepsTheSameLibraryInstance()
    {
        RunSta(() =>
        {
            EnsureApplication();
            var navigation = new NavigationService();
            var viewModel = new MainViewModel(navigation, _env.Catalog, _env.Thumbnails, _env.Organization, _env.FileOperations, new PhotoManager.Infrastructure.Metadata.MetadataExtractorReader(), _env.MetadataEditing);
            var window = new MainWindow(viewModel);
            window.Show();
            try
            {
                var library = viewModel.CurrentView;
                foreach (var key in new[] { "Metadata", "Microstock", "Tools", "Settings" })
                {
                    viewModel.NavigateCommand.Execute(key);
                    Pump(50);
                    window.UpdateLayout();
                    Assert.Equal(key, viewModel.SelectedNavigationKey);
                    Assert.Single(viewModel.NavigationItems, item => item.IsSelected);
                }
                viewModel.NavigateCommand.Execute("Library");
                Assert.Same(library, viewModel.CurrentView);
            }
            finally { window.Close(); }
        });
    }
}
