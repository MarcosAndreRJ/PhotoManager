using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoManager.Application.Collections;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

[Collection("WpfUi")]
public sealed class MultiSelectTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    [Theory]
    [InlineData(false, true, 3, false, false, PhotoClickAction.CollapseOnRelease)]   // foto já selecionada numa seleção múltipla: não reduzir ao apertar
    [InlineData(false, true, 1, false, false, PhotoClickAction.Default)]
    [InlineData(false, false, 3, false, false, PhotoClickAction.Default)]            // foto fora da seleção: clique normal troca a seleção
    [InlineData(false, true, 3, true, false, PhotoClickAction.Default)]              // Ctrl/Shift continuam com o ListBox
    [InlineData(false, true, 3, false, true, PhotoClickAction.Default)]
    [InlineData(true, false, 0, false, false, PhotoClickAction.ToggleOnRelease)]     // multi-seleção: clicar na mídia alterna
    [InlineData(true, true, 3, false, false, PhotoClickAction.ToggleOnRelease)]
    [InlineData(true, true, 3, true, false, PhotoClickAction.ToggleOnRelease)]
    [InlineData(true, false, 2, false, true, PhotoClickAction.Default)]              // Shift = intervalo
    public void ClickPolicy_ResolvesAsExpected(bool multi, bool selected, int count, bool ctrl, bool shift, PhotoClickAction expected)
        => Assert.Equal(expected, PhotoClickPolicy.Resolve(multi, selected, count, ctrl, shift));

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception ex) { failure = ex; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private static void Pump(int ms = 200)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>Entrega o evento pela grade (rota real: janela → ListBox), com o item como origem, como o mouse faria.</summary>
    private static void Click(ListBox list, UIElement item, RoutedEvent routedEvent)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent, Source = item };
        list.RaiseEvent(args);
    }

    private static void PressAndRelease(ListBox list, UIElement target)
    {
        Click(list, target, UIElement.PreviewMouseLeftButtonDownEvent);
        Click(list, target, UIElement.PreviewMouseLeftButtonUpEvent);
    }

    private static (Window Window, LibraryView View, ListBox List) Show(LibraryViewModel vm)
    {
        var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
            foreach (var name in new[] { "Colors", "Typography", "Buttons", "Inputs", "Cards", "Tabs", "Tree" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/PhotoManager;component/Themes/{name}.xaml") });
        if (!app.Resources.Contains("BooleanToVisibilityConverter")) app.Resources.Add("BooleanToVisibilityConverter", new BooleanToVisibilityConverter());
        var view = new LibraryView { DataContext = vm };
        var window = new Window { Content = view, Width = 1300, Height = 900 };
        window.Show();
        Pump(500);
        return (window, view, (ListBox)view.FindName("PhotoList"));
    }

    [Fact]
    public async Task NormalMode_PressingOnSelectedPhoto_KeepsMultiSelection_AndCollapsesOnlyOnRelease()
    {
        for (var i = 0; i < 4; i++) _env.CreatePng($"p{i}.png");
        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);
        RunSta(() =>
        {
            var (window, _, list) = Show(vm);
            var items = Descendants<ListBoxItem>(list).ToList();
            Assert.True(items.Count >= 4);
            items[0].IsSelected = items[1].IsSelected = items[2].IsSelected = true;
            Pump();
            Assert.Equal(3, vm.SelectedCards.Count);

            // Apertar numa foto selecionada (início de um arraste) NÃO pode reduzir a seleção.
            Click(list, items[1], UIElement.PreviewMouseLeftButtonDownEvent);
            Pump(50);
            Assert.Equal(3, list.SelectedItems.Count);
            Assert.Equal(3, vm.SelectedCards.Count);

            // Soltar sem arrastar = clique simples: fica só essa foto.
            Click(list, items[1], UIElement.PreviewMouseLeftButtonUpEvent);
            Pump(100);
            Assert.Single(list.SelectedItems);
            Assert.Single(vm.SelectedCards);
            window.Close();
        });
    }

    [Fact]
    public async Task MultiSelectMode_ShowsCheckboxes_AndClickingThePhotoTogglesIt()
    {
        for (var i = 0; i < 4; i++) _env.CreatePng($"p{i}.png");
        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);
        RunSta(() =>
        {
            var (window, _, list) = Show(vm);
            var items = Descendants<ListBoxItem>(list).ToList();

            Assert.All(Descendants<CheckBox>(list), c => Assert.NotEqual(Visibility.Visible, c.Visibility));   // fora do modo: sem caixas

            vm.IsMultiSelectMode = true;
            Pump(200);
            Assert.All(Descendants<CheckBox>(list).Take(4), c => Assert.Equal(Visibility.Visible, c.Visibility));

            PressAndRelease(list, items[0]);
            PressAndRelease(list, items[2]);
            Pump(100);
            Assert.Equal(2, vm.SelectedCards.Count);                       // as duas continuam marcadas (não troca a seleção)
            PressAndRelease(list, items[0]);                                     // clicar de novo desmarca
            Pump(100);
            Assert.Single(vm.SelectedCards);
            Assert.False(items[0].IsSelected);
            Assert.True(items[2].IsSelected);

            // Pressionar numa foto marcada para arrastar mantém todas as marcadas.
            items[1].IsSelected = true;
            Pump(100);
            Click(list, items[1], UIElement.PreviewMouseLeftButtonDownEvent);
            Pump(50);
            Assert.Equal(2, vm.SelectedCards.Count);

            vm.IsMultiSelectMode = false;
            Pump(200);
            Assert.All(Descendants<CheckBox>(list), c => Assert.NotEqual(Visibility.Visible, c.Visibility));
            window.Close();
        });
    }
}
