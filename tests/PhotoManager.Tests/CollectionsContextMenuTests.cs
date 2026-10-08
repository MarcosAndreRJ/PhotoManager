using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

/// <summary>Reproduz o uso real do menu de contexto da árvore de coleções (item de menu → comando → diálogo).</summary>
[Collection("WpfUi")]
public sealed class CollectionsContextMenuTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

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

    private static void Pump(int ms = 150)
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

    private static void EnsureResources()
    {
        var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
            foreach (var name in new[] { "Colors", "Typography", "Buttons", "Inputs", "Cards", "Tabs", "Tree" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/PhotoManager;component/Themes/{name}.xaml") });
        if (!app.Resources.Contains("BooleanToVisibilityConverter"))
            app.Resources.Add("BooleanToVisibilityConverter", new BooleanToVisibilityConverter());
    }

    [Fact]
    public async Task ContextMenu_Items_AreBoundToViewModelCommands_AndNewSubcollectionWorks()
    {
        var pai = await _env.Collections.CreateAsync("Viagens", null);
        await _env.Collections.CreateAsync("Roraima", pai.Id);
        string report = "";
        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);

        RunSta(() =>
        {
            EnsureResources();
            

            var shown = 0;
            vm.ShowNameDialog = nameVm => { shown++; nameVm.Name = "Nordeste"; return true; };
            vm.ShowMessage = (text, title) => report += $"[{title}] {text}\n";

            var view = new LibraryView { DataContext = vm };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            window.Show();
            Pump(400);

            var docks = Descendants<DockPanel>(view).Where(d => d.DataContext is CollectionNode && d.ContextMenu is not null).ToList();
            report += $"docks com menu: {docks.Count}\n";
            var dock = docks.First(d => ((CollectionNode)d.DataContext).Name == "Viagens");
            dock.ContextMenu!.PlacementTarget = dock;
            dock.ContextMenu.IsOpen = true;
            Pump(200);

            foreach (var item in dock.ContextMenu.Items.OfType<MenuItem>())
                report += $"item '{item.Header}': Command={(item.Command is null ? "NULL" : "ok")} Enabled={item.IsEnabled} Param={(item.CommandParameter as CollectionNode)?.Name}\n";

            var sub = dock.ContextMenu.Items.OfType<MenuItem>().First(i => (string)i.Header == "Nova subcoleção…");
            if (sub.Command is not null) sub.Command.Execute(sub.CommandParameter);
            Pump(800);
            dock.ContextMenu.IsOpen = false;
            window.Close();

            report += $"dialogos mostrados: {shown}\n";
            var names = vm.CollectionNodes.SelectMany(n => n.SelfAndDescendants()).Select(n => n.Name).ToList();
            report += "nós: " + string.Join(", ", names) + "\n";
        });

        Assert.True(report.Contains("Nordeste"), report);
    }

    [Fact]
    public async Task ContextMenu_NewSubcollection_WithRealDialog_CreatesSubcollection()
    {
        var pai = await _env.Collections.CreateAsync("Viagens", null);
        string report = "";
        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);

        RunSta(() =>
        {
            EnsureResources();
            vm.ShowMessage = (text, title) => report += $"[{title}] {text}\n";
            CollectionNameDialog? current = null;
            vm.ShowNameDialog = v => { var sw = System.Diagnostics.Stopwatch.StartNew(); var d = new CollectionNameDialog(v); current = d; d.Closed += (_, _) => report += "dialog Closed\n"; d.Loaded += (_, _) => report += "dialog Loaded\n"; var r = d.ShowDialog(); report += $"ShowDialog devolveu {r} após {sw.ElapsedMilliseconds}ms\n"; return r; };
            var view = new LibraryView { DataContext = vm };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            window.Show();
            Pump(400);

            var dock = Descendants<DockPanel>(view).First(d => d.DataContext is CollectionNode { Name: "Viagens" } && d.ContextMenu is not null);
            dock.ContextMenu!.PlacementTarget = dock;
            dock.ContextMenu.IsOpen = true;
            Pump(200);
            var sub = dock.ContextMenu.Items.OfType<MenuItem>().First(i => (string)i.Header == "Nova subcoleção…");

            // O diálogo é modal: um timer na mesma thread o preenche e confirma enquanto ShowDialog bloqueia.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var dialog = current;
                if (dialog is null) { report += "diálogo não apareceu\n"; return; }
                var nameVm = (PhotoManager.Wpf.ViewModels.CollectionNameViewModel)dialog.DataContext;
                nameVm.Name = "Nordeste";
                var save = Descendants<Button>(dialog).First(b => b.IsDefault);
                report += $"salvar habilitado: {save.IsEnabled}\n";
                var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(save);
                ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
            };
            timer.Start();
            report += $"param={sub.CommandParameter?.GetType().Name ?? "null"} dock.DataContext={(dock.DataContext as CollectionNode)?.Name}\n";
            var node = (CollectionNode)sub.CommandParameter!; var task = vm.CreateSubcollectionAsync(node);
            Pump(1500); report += $"task: {task.Status} {task.Exception?.ToString()}\n";
            dock.ContextMenu.IsOpen = false;
            report += "nós: " + string.Join(", ", vm.CollectionNodes.SelectMany(n => n.SelfAndDescendants()).Select(n => n.Name)) + "\n";
            window.Close();
        });

        Assert.True(report.Contains("Nordeste"), report);
    }

    [Fact]
    public async Task ContextMenu_AllCommands_WithSelectedCollectionAndPhotos_DoNotCrash()
    {
        _env.CreatePng("a.png");
        _env.CreatePng("b.png");
        var pai = await _env.Collections.CreateAsync("Viagens", null);
        var filho = await _env.Collections.CreateAsync("Roraima", pai.Id);
        var outro = await _env.Collections.CreateAsync("Familia", null);
        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);
        var photos = await _env.Catalog.GetPhotosAsync();
        await _env.Collections.AddPhotosAsync(pai.Id, [photos[0].Id]);
        await _env.Collections.AddPhotosAsync(filho.Id, [photos[0].Id, photos[1].Id]);
        await vm.ImportFolderAsync(_env.Photos);   // recarrega CollectionIds
        string report = "";

        RunSta(() =>
        {
            EnsureResources();
            vm.ShowMessage = (text, title) => report += $"[{title}] {text}\n";
            var nameCalls = 0;
            vm.ShowNameDialog = v => { nameCalls++; v.Name = nameCalls == 1 ? "Nordeste" : "Renomeada"; return true; };
            vm.ShowMoveDialog = v => { report += $"move: {v.GetType().Name}\n"; return false; };
            vm.ShowDeleteDialog = v => { report += "delete dialog\n"; return false; };

            var view = new LibraryView { DataContext = vm };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            window.Show();
            Pump(400);

            var node = vm.CollectionNodes.First(n => n.Name == "Viagens");
            vm.SelectCollectionNode(node);                // filtro ativo + TreeViewItem selecionado de verdade
            Pump(300);
            report += $"visíveis após selecionar: {vm.Photos.Count}\n";

            void Click(string header)
            {
                var dock = Descendants<DockPanel>(view).First(d => d.DataContext is CollectionNode cn && cn.Id == pai.Id && d.ContextMenu is not null);
                dock.ContextMenu!.PlacementTarget = dock;
                dock.ContextMenu.IsOpen = true;
                Pump(150);
                var item = dock.ContextMenu.Items.OfType<MenuItem>().First(i => (string)i.Header == header);
                var peer = new System.Windows.Automation.Peers.MenuItemAutomationPeer(item);
                ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
                Pump(500);
                dock.ContextMenu.IsOpen = false;
                report += $"após '{header}': nós=" + string.Join(",", vm.CollectionNodes.SelectMany(n => n.SelfAndDescendants()).Select(n => n.Name)) + "\n";
            }

            Click("Nova subcoleção…");
            Click("Renomear…");
            Click("Mover para…");
            Click("Excluir…");
            window.Close();
        });

        Assert.DoesNotContain("[Erro", report);
        Assert.Contains("Nordeste", report);
        Assert.Contains("Renomeada", report);
        Assert.Contains("move:", report);
        Assert.Contains("delete dialog", report);
    }

    [Fact]
    public async Task Tree_ShowsAggregateTodasFirst_SelectingItFiltersWithoutLoops_AndItHasNoContextMenu()
    {
        for (var i = 0; i < 3; i++) _env.CreatePng($"t{i}.png");
        var pai = await _env.Collections.CreateAsync("Pai", null);
        var filho = await _env.Collections.CreateAsync("Filho", pai.Id);
        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);
        var ids = (await _env.Catalog.GetPhotosAsync()).Select(p => p.Id).ToList();
        await _env.Collections.AddPhotosAsync(pai.Id, [ids[0]]);
        await _env.Collections.AddPhotosAsync(filho.Id, [ids[1]]);
        await vm.ImportFolderAsync(_env.Photos);
        var report = "";

        RunSta(() =>
        {
            EnsureResources();
            var view = new LibraryView { DataContext = vm };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            window.Show();
            Pump(300);

            vm.CollectionNodes.Single(n => n.Id == pai.Id).IsExpanded = true;
            Pump(300);
            var items = Descendants<TreeViewItem>((TreeView)view.FindName("CollectionTree")).Where(i => i.DataContext is CollectionNode).ToList();
            report += "itens: " + string.Join(",", items.Select(i => ((CollectionNode)i.DataContext).IsAggregate ? "Todas*" : ((CollectionNode)i.DataContext).Name)) + "\n";

            var aggregateItem = items.First(i => ((CollectionNode)i.DataContext).IsAggregate);
            aggregateItem.IsSelected = true;       // seleção real do TreeView, como um clique
            Pump(400);
            report += $"visíveis={vm.Photos.Count} selecionado={vm.SelectedCollectionNode?.IsAggregate}\n";

            var dock = Descendants<DockPanel>(aggregateItem).First(d => d.DataContext is CollectionNode { IsAggregate: true });
            var args = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new object[] { dock, true, 0d, 0d }, null)!;
            args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
            dock.RaiseEvent(args);
            report += $"menuCancelado={args.Handled}\n";
            window.Close();
        });

        Assert.Contains("itens: Pai,Todas*,Filho", report);
        Assert.True(report.Contains("visíveis=2 selecionado=True"), report);
        Assert.Contains("menuCancelado=True", report);
    }
}
