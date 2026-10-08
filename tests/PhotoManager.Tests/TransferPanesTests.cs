using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoManager.Application.Navigation;
using PhotoManager.Application.Transfer;
using PhotoManager.Infrastructure.Images;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

[Collection("WpfUi")]
public sealed class TransferPanesTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private string Make(string relative, int size = 10, DateTime? modified = null)
    {
        var path = Path.Combine(_env.Root, "tr", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        if (modified is { } when) File.SetLastWriteTime(path, when);
        return path;
    }

    private string Folder(string relative) => Directory.CreateDirectory(Path.Combine(_env.Root, "tr", relative)).FullName;

    private static TransferEntry E(string name, long size = 0, bool folder = false, int day = 1) =>
        new("X:\\" + name, name, folder ? TransferEntryKind.Folder : FolderLister.KindOf(name), size, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc));

    // ---------- lógica pura ----------

    [Fact]
    public void Listing_FiltersByNameAndExtension_WithAllTerms()
    {
        var all = new[] { E("viagem01.mp4"), E("viagem02.jpg"), E("IMG_2026.jpg"), E("Viagens", folder: true) };
        Assert.Equal(["viagem01.mp4", "viagem02.jpg"], TransferListing.Apply(all, "viagem", TransferSortField.Name, false, true).Select(e => e.Name).OrderBy(n => n));
        Assert.Equal(["viagem01.mp4"], TransferListing.Apply(all, "VIAGEM mp4", TransferSortField.Name, false, true).Select(e => e.Name));
        Assert.Equal(["IMG_2026.jpg"], TransferListing.Apply(all, "2026", TransferSortField.Name, false, true).Select(e => e.Name));
        Assert.Equal(4, TransferListing.Apply(all, "  ", TransferSortField.Name, false, true).Count);
    }

    [Fact]
    public void Listing_SortsNaturally_ByDateSizeType_FoldersFirstEvenDescending()
    {
        var all = new[] { E("IMG10.jpg", 5, day: 3), E("IMG2.jpg", 50, day: 1), E("a.mp4", 1, day: 2), E("Zeta", folder: true), E("alfa", folder: true) };
        Assert.Equal(["alfa", "Zeta", "a.mp4", "IMG2.jpg", "IMG10.jpg"], TransferListing.Apply(all, null, TransferSortField.Name, false, true).Select(e => e.Name));
        Assert.Equal(["Zeta", "alfa", "IMG10.jpg", "IMG2.jpg", "a.mp4"], TransferListing.Apply(all, null, TransferSortField.Name, true, true).Select(e => e.Name));
        Assert.Equal(["IMG2.jpg", "a.mp4", "IMG10.jpg"], TransferListing.Apply(all, null, TransferSortField.Date, false, true).Where(e => !e.IsFolder).Select(e => e.Name));
        Assert.Equal(["IMG2.jpg", "IMG10.jpg", "a.mp4"], TransferListing.Apply(all, null, TransferSortField.Size, true, true).Where(e => !e.IsFolder).Select(e => e.Name));
        Assert.Equal(".jpg", TransferListing.Apply(all, null, TransferSortField.Type, false, true).First(e => !e.IsFolder && e.Extension == ".jpg").Extension);
        Assert.Equal("a.mp4", TransferListing.Apply(all, null, TransferSortField.Name, false, false)[0].Name);   // sem "pastas primeiro", pasta e arquivo se misturam
    }

    [Fact]
    public void History_GoesBackForward_AndRevisitDiscardsForward()
    {
        var history = new NavigationHistory();
        history.Visit("A"); history.Visit("B"); history.Visit("C");
        Assert.False(history.CanGoForward);
        Assert.Equal("B", history.Back());
        Assert.Equal("A", history.Back());
        Assert.False(history.CanGoBack);
        Assert.Equal("B", history.Forward());
        history.Visit("D");
        Assert.False(history.CanGoForward);
        Assert.Equal("B", history.Back());
        history.Visit("B");                                      // repetir a pasta atual não cria entrada nem apaga o "avançar"
        Assert.True(history.CanGoForward);
    }

    [Theory]
    [InlineData(@"D:\Fotos\2026", @"D:\Fotos")]
    [InlineData(@"D:\Fotos", @"D:\")]
    [InlineData(@"D:\", null)]
    [InlineData(@"D:", null)]
    public void Parent_StopsAtTheRoot(string path, string? expected) => Assert.Equal(expected, NavigationHistory.Parent(path));

    [Fact]
    public void Breadcrumbs_SplitDriveAndUnc()
    {
        Assert.Equal([("D:", @"D:\"), ("Fotos", @"D:\Fotos"), ("2026", @"D:\Fotos\2026")], Breadcrumbs.Split(@"D:\Fotos\2026").Select(p => (p.Label, p.Path)));
        var unc = Breadcrumbs.Split(@"\\servidor\fotos\viagem");
        Assert.Equal(@"\\servidor\fotos", unc[0].Path);
        Assert.Equal(@"\\servidor\fotos\viagem", unc[^1].Path);
        Assert.Empty(Breadcrumbs.Split(""));
    }

    [Fact]
    public void FolderLister_ClassifiesEntries_AndReportsOfflineFolders()
    {
        Make("L/a.jpg"); Make("L/v.mp4"); Make("L/notas.txt"); Folder("L/sub");
        var ok = FolderLister.List(Path.Combine(_env.Root, "tr", "L"));
        Assert.True(ok.IsOk);
        Assert.Equal([("a.jpg", TransferEntryKind.Photo), ("notas.txt", TransferEntryKind.Other), ("sub", TransferEntryKind.Folder), ("v.mp4", TransferEntryKind.Video)],
            ok.Entries.OrderBy(e => e.Name).Select(e => (e.Name, e.Kind)));
        var missing = FolderLister.List(Path.Combine(_env.Root, "tr", "nao-existe"));
        Assert.False(missing.IsOk);
        Assert.Empty(missing.Entries);
        Assert.False(FolderLister.List("").IsOk);
    }

    [Fact]
    public void Navigation_HasTransferBetweenMicrostockAndTools()
    {
        var keys = new NavigationService().Items.Select(i => i.Key).ToList();
        Assert.Equal(keys.IndexOf("Microstock") + 1, keys.IndexOf("Transfer"));
        Assert.Equal(keys.IndexOf("Transfer") + 1, keys.IndexOf("Tools"));
    }

    // ---------- painéis ----------

    [Fact]
    public async Task Panes_AreIndependent_NavigationSearchAndSort()
    {
        Make("A/b2.jpg", 5); Make("A/b10.jpg", 50); Make("A/outro.png", 20); Folder("A/sub");
        Make("B/x.mp4", 7); Make("B/y.mp4", 70);
        var view = new TransferViewModel(null, rightStart: null);
        var left = view.LeftPane; var right = view.RightPane;
        await left.NavigateAsync(Path.Combine(_env.Root, "tr", "A"));
        await right.NavigateAsync(Path.Combine(_env.Root, "tr", "B"));

        left.SearchText = "b";
        Assert.Equal(["b2.jpg", "b10.jpg"], left.Items.Where(i => !i.IsFolder).Select(i => i.Name));
        Assert.Equal(2, right.Items.Count);
        Assert.Equal("", right.SearchText);

        left.SortDescending = true;
        Assert.Equal(["b10.jpg", "b2.jpg"], left.Items.Where(i => !i.IsFolder).Select(i => i.Name));
        Assert.Equal(["x.mp4", "y.mp4"], right.Items.Select(i => i.Name));

        right.SortOption = TransferPaneViewModel.SortOptions.First(o => o.Field == TransferSortField.Size);
        right.SortDescending = true;
        Assert.Equal(["y.mp4", "x.mp4"], right.Items.Select(i => i.Name));
        Assert.Equal(TransferSortField.Name, left.SortOption.Field);

        // navegar à esquerda não mexe na direita
        var rightPath = right.CurrentPath;
        await left.NavigateAsync(Path.Combine(_env.Root, "tr", "A", "sub"));
        Assert.Equal(rightPath, right.CurrentPath);
    }

    [Fact]
    public async Task Pane_BackForwardUpRefresh_AndBreadcrumb()
    {
        Make("N/um/dois/f.jpg");
        var pane = new TransferPaneViewModel();
        var root = Path.Combine(_env.Root, "tr", "N");
        await pane.NavigateAsync(root);
        await pane.NavigateAsync(Path.Combine(root, "um"));
        await pane.NavigateAsync(Path.Combine(root, "um", "dois"));
        Assert.Equal(["f.jpg"], pane.Items.Select(i => i.Name));
        Assert.Equal("dois", pane.Breadcrumb[^1].Label);

        pane.UpCommand.Execute(null);
        await TestEnvironment.WaitUntilAsync(() => pane.CurrentPath == Path.Combine(root, "um"));
        pane.BackCommand.Execute(null);                                   // histórico: dois foi visitado antes de "subir"
        await TestEnvironment.WaitUntilAsync(() => pane.CurrentPath == Path.Combine(root, "um", "dois"));
        Assert.True(pane.BackCommand.CanExecute(null));
        Assert.True(pane.ForwardCommand.CanExecute(null));
        pane.ForwardCommand.Execute(null);
        await TestEnvironment.WaitUntilAsync(() => pane.CurrentPath == Path.Combine(root, "um"));

        Make("N/um/novo.jpg");
        await pane.RefreshAsync();
        Assert.Contains("novo.jpg", pane.Items.Select(i => i.Name));
        pane.NavigateCommand.Execute(pane.Breadcrumb[^2]);                 // clicar no trecho do breadcrumb
        await TestEnvironment.WaitUntilAsync(() => pane.CurrentPath == root);
    }

    [Fact]
    public async Task Pane_OfflineFolder_ShowsMessage_AndRecoversOnRefresh()
    {
        var path = Path.Combine(_env.Root, "tr", "SomeDisk");
        var pane = new TransferPaneViewModel();
        await pane.NavigateAsync(path);
        Assert.True(pane.HasError);
        Assert.True(pane.IsEmpty);
        Assert.Contains("não existe", pane.EmptyText);
        Assert.Equal(path, pane.CurrentPath);
        Make("SomeDisk/ok.jpg");
        await pane.RefreshAsync();
        Assert.False(pane.HasError);
        Assert.Equal(["ok.jpg"], pane.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Pane_ChooseFolderAndTypedPath_NavigateAnywhere()
    {
        Make("P/a.jpg");
        var chosen = Path.Combine(_env.Root, "tr", "P");
        var pane = new TransferPaneViewModel(chooseFolder: () => chosen);
        Assert.True(pane.IsEmpty);
        Assert.Contains("Escolha", pane.EmptyText);
        pane.ChooseFolderCommand.Execute(null);
        await TestEnvironment.WaitUntilAsync(() => pane.CurrentPath == chosen);

        pane.BeginEditPathCommand.Execute(null);
        Assert.True(pane.IsEditingPath);
        pane.PathText = "\"" + Path.Combine(_env.Root, "tr") + "\\\"";
        pane.CommitPathCommand.Execute(null);
        await TestEnvironment.WaitUntilAsync(() => pane.CurrentPath == Path.Combine(_env.Root, "tr"));
        Assert.False(pane.IsEditingPath);
    }

    [Fact]
    public async Task Pane_Selection_CountsFilesAndSurvivesSorting_ModeSwitchClearsIt()
    {
        Make("S/a.jpg", 1024); Make("S/b.jpg", 2048); Folder("S/pasta");
        var pane = new TransferPaneViewModel();
        await pane.NavigateAsync(Path.Combine(_env.Root, "tr", "S"));
        pane.SetSelection(pane.Items.Where(i => !i.IsFolder));
        Assert.Equal("2 selecionado(s) · 3 KB", pane.SelectionText);
        pane.SortDescending = true;
        Assert.Equal(2, pane.SelectedItems.Count);
        pane.ViewMode = TransferViewMode.List;
        Assert.Empty(pane.SelectedItems);
        Assert.Equal("", pane.SelectionText);
    }

    [Fact]
    public async Task Pane_Thumbnails_AreCreatedForMediaOnly_AndCachedByPathSizeAndDate()
    {
        var png = _env.CreatePng("../tr/T/foto.png");
        Make("T/notas.txt");
        var service = new ThumbnailService(Path.Combine(_env.Root, "cache"));
        var pane = new TransferPaneViewModel(service);
        await pane.NavigateAsync(Path.Combine(_env.Root, "tr", "T"));
        await TestEnvironment.WaitUntilAsync(() => pane.Items.Any(i => i.Name == "foto.png" && i.ThumbnailUri is not null));
        Assert.Null(pane.Items.Single(i => i.Name == "notas.txt").ThumbnailUri);
        Assert.True(pane.Items.Single(i => i.Name == "notas.txt").ShowGlyph);

        var first = await service.GetOrCreateForFileAsync(png);
        Assert.NotNull(first);
        Assert.Equal(first, await service.GetOrCreateForFileAsync(png));            // mesmo cache
        File.WriteAllBytes(png, File.ReadAllBytes(png).Concat(new byte[] { 0 }).ToArray());
        File.SetLastWriteTime(png, DateTime.Now.AddMinutes(5));
        Assert.NotEqual(first, await service.GetOrCreateForFileAsync(png));         // arquivo mudou → miniatura nova
        Assert.Null(await service.GetOrCreateForFileAsync(Path.Combine(_env.Root, "nao-existe.jpg")));
    }

    // ---------- tela ----------

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

    private static void EnsureResources()
    {
        var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
            foreach (var name in new[] { "Colors", "Typography", "Buttons", "Inputs", "Cards", "Tabs", "Tree" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/PhotoManager;component/Themes/{name}.xaml") });
        if (!app.Resources.Contains("BooleanToVisibilityConverter")) app.Resources.Add("BooleanToVisibilityConverter", new BooleanToVisibilityConverter());
    }

    [Fact]
    public async Task Ui_TwoPanesWithSplitter_ShowItems_SwitchBetweenGridAndList_AndNavigateOnDoubleClickOnFolders()
    {
        Make("U/a.jpg"); Make("U/b.mp4"); Folder("U/pasta"); Make("U/pasta/dentro.jpg"); Make("V/z.png");
        var viewModel = new TransferViewModel(null, Path.Combine(_env.Root, "tr", "U"), Path.Combine(_env.Root, "tr", "V"));
        await TestEnvironment.WaitUntilAsync(() => viewModel.LeftPane.Items.Count == 3 && viewModel.RightPane.Items.Count == 1);
        var report = "";

        RunSta(() =>
        {
            EnsureResources();
            var view = new TransferView { DataContext = viewModel };
            var window = new Window { Content = view, Width = 1500, Height = 900 };
            window.Show();
            Pump(400);

            var panes = Descendants<TransferPaneView>(view).ToList();
            var splitters = Descendants<System.Windows.Controls.GridSplitter>(view).Count();
            var grid = panes[0].FindName("GridList") as ListBox;
            var detail = panes[0].FindName("DetailList") as ListView;
            report += $"paineis={panes.Count} splitter={splitters} grade={grid!.Visibility} lista={detail!.Visibility} itens={grid.Items.Count}\n";

            viewModel.LeftPane.ViewMode = TransferViewMode.List;
            Pump(200);
            report += $"modo lista: grade={grid.Visibility} lista={detail.Visibility} direita={((ListBox)panes[1].FindName("GridList")!).Visibility}\n";

            var firstColumn = ((GridView)detail.View).Columns.Count;
            report += $"colunas={firstColumn}\n";
            report += $"organizar: novaPasta={panes[0].FindName("NewFolderButton") is Button} renomear={panes[0].FindName("RenameButton") is Button} cor={panes[0].FindName("ColorButton") is Button} lixeira={panes[0].FindName("DeleteButton") is Button} menu={grid.ContextMenu is not null && detail.ContextMenu is not null}\n";
            report += $"extras: data={panes[0].FindName("OrganizeButton") is Button} detalhes={panes[0].FindName("DetailsButton") is System.Windows.Controls.Primitives.ToggleButton} fixar={panes[0].FindName("PinButton") is Button} "
                + $"centro={new[] { "CopyRightButton", "MoveRightButton", "CopyLeftButton", "MoveLeftButton", "SwapButton", "UndoButton" }.All(n => view.FindName(n) is Button)} locais={view.FindName("PlacesBar") is Border} soltar={grid.AllowDrop && detail.AllowDrop}\n";
            window.Close();
        });

        Assert.Contains("paineis=2 splitter=1 grade=Visible lista=Collapsed itens=3", report);
        Assert.Contains("modo lista: grade=Collapsed lista=Visible direita=Visible", report);   // o outro painel não muda
        Assert.Contains("colunas=4", report);
        Assert.Contains("organizar: novaPasta=True renomear=True cor=True lixeira=True menu=True", report);
        Assert.Contains("extras: data=True detalhes=True fixar=True centro=True locais=True soltar=True", report);
    }
}
