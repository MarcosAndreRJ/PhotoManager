using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoManager.Application.Catalog;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

[Collection("WpfUi")]
public sealed class ColorAndFilterTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    // ---------- cores (dados puros) ----------

    [Fact]
    public void PhotoColors_HaveNamesHexAndKeyboardDigits()
    {
        Assert.Equal(6, PhotoColors.All.Count);
        Assert.Equal("Vermelho", PhotoColors.Name(PhotoColor.Red));
        Assert.Equal("Sem cor", PhotoColors.Name(PhotoColor.None));
        Assert.All(PhotoColors.All, c => Assert.Matches("^#[0-9A-F]{6}$", PhotoColors.Hex(c)));
        Assert.Equal(PhotoColors.All.Count, PhotoColors.All.Select(PhotoColors.Hex).Distinct().Count());
        Assert.Equal(PhotoColor.Green, PhotoColors.FromDigit(4));
        Assert.Equal(PhotoColor.None, PhotoColors.FromDigit(0));
        Assert.Null(PhotoColors.FromDigit(7));
        Assert.Equal(["Qualquer", "Sem cor", "Vermelho", "Laranja", "Amarelo", "Verde", "Azul", "Roxo"], PhotoColors.FilterChoices);
        Assert.Null(PhotoColors.FromFilterChoice("Qualquer"));
        Assert.Equal(PhotoColor.None, PhotoColors.FromFilterChoice("Sem cor"));
        Assert.Equal(PhotoColor.Purple, PhotoColors.FromFilterChoice("Roxo"));
    }

    [Fact]
    public async Task ColorLabel_IsPersisted_ForManyItems_AndCanBeCleared()
    {
        _env.CreatePng("c1.png"); _env.CreatePng("c2.png"); _env.CreatePng("c3.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photos = (await _env.Catalog.GetPhotosAsync()).ToList();
        Assert.All(photos, p => Assert.Equal(PhotoColor.None, p.ColorLabel));

        await _env.Catalog.SetColorLabelAsync(photos.Take(2).ToList(), PhotoColor.Red);
        var reloaded = (await _env.Catalog.GetPhotosAsync()).OrderBy(p => p.FileName).ToList();
        Assert.Equal([PhotoColor.Red, PhotoColor.Red, PhotoColor.None], reloaded.Select(p => p.ColorLabel));

        await _env.Catalog.SetColorLabelAsync([reloaded[0]], PhotoColor.None);
        Assert.Equal([PhotoColor.None, PhotoColor.Red, PhotoColor.None], (await _env.Catalog.GetPhotosAsync()).OrderBy(p => p.FileName).Select(p => p.ColorLabel));
    }

    [Fact]
    public async Task Library_SetColor_UpdatesCards_Status_AndUsesTheContextCardOnlyOnce()
    {
        _env.CreatePng("m1.png"); _env.CreatePng("m2.png"); _env.CreatePng("m3.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var cards = library.Photos.OrderBy(c => c.FileName).ToList();
        library.UpdateSelection([cards[0], cards[1]]);

        // Menu de contexto aberto sobre uma foto da seleção: vale para a seleção toda.
        library.SetContextCard(cards[0]);
        library.ColorChoices.Single(c => c.Color == PhotoColor.Blue).Command.Execute(null);
        await TestEnvironment.WaitUntilAsync(() => cards[1].HasColor);
        Assert.Equal([PhotoColor.Blue, PhotoColor.Blue, PhotoColor.None], cards.Select(c => c.ColorLabel));
        Assert.Contains("azul", library.StatusText);

        // O cartão de contexto é de uso único: o botão da revisão/teclas agem sobre a seleção.
        library.SelectionColorChoices.Single(c => c.Color == PhotoColor.Green).Command.Execute(null);
        await TestEnvironment.WaitUntilAsync(() => cards[0].ColorLabel == PhotoColor.Green);
        Assert.Equal([PhotoColor.Green, PhotoColor.Green, PhotoColor.None], cards.Select(c => c.ColorLabel));

        await library.SetColorForSelectionAsync(PhotoColor.None);               // tecla 0
        Assert.All(cards, c => Assert.False(c.HasColor));
        Assert.Contains("removida", library.StatusText);
        Assert.Equal(7, library.ColorChoices.Count);
    }

    [Fact]
    public async Task ColorFilter_ListsOnlyThatColor_OrThoseWithoutColor()
    {
        _env.CreatePng("f1.png"); _env.CreatePng("f2.png"); _env.CreatePng("f3.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var cards = library.Photos.OrderBy(c => c.FileName).ToList();
        await library.SetColorAsync([cards[0]], PhotoColor.Red);
        await library.SetColorAsync([cards[1]], PhotoColor.Green);

        library.ColorFilterChoice = "Vermelho";
        Assert.Equal(["f1.png"], library.Photos.Select(c => c.FileName));
        library.ColorFilterChoice = "Sem cor";
        Assert.Equal(["f3.png"], library.Photos.Select(c => c.FileName));
        library.ColorFilterChoice = "Qualquer";
        Assert.Equal(3, library.Photos.Count);

        // Marcar a cor com o filtro ativo tira o item da lista na hora.
        library.ColorFilterChoice = "Vermelho";
        await library.SetColorAsync([library.Photos.Single()], PhotoColor.Blue);
        Assert.Empty(library.Photos);
    }

    // ---------- filtros avançados ----------

    [Fact]
    public async Task TypeAndExtensionFilters_ListTheCatalogExtensions_AndFilter()
    {
        _env.CreatePng("a.png"); _env.CreateJpeg("b.jpg"); _env.CreateJpeg("c.jpeg");
        File.WriteAllBytes(Path.Combine(_env.Photos, "v.mp4"), FakeMp4.Build(320, 240, 3));
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        Assert.Equal(["Todas", ".jpeg", ".jpg", ".mp4", ".png"], library.ExtensionChoices);
        Assert.Equal(4, library.Photos.Count);

        library.TypeChoice = "Vídeos";
        Assert.Equal(["v.mp4"], library.Photos.Select(c => c.FileName));
        library.TypeChoice = "Fotos";
        Assert.Equal(3, library.Photos.Count);
        library.TypeChoice = "Todos";

        library.ExtensionChoice = ".jpg";
        Assert.Equal(["b.jpg"], library.Photos.Select(c => c.FileName));
        library.ExtensionChoice = "Todas";
        Assert.Equal(4, library.Photos.Count);
    }

    [Fact]
    public async Task DateFilter_UsesTheCaptureOrCreationDate_AndBothEndsAreInclusive()
    {
        var old = _env.CreatePng("old.png"); var mid = _env.CreatePng("mid.png"); var recent = _env.CreatePng("new.png");
        File.SetCreationTime(old, new DateTime(2020, 3, 10, 12, 0, 0));
        File.SetCreationTime(mid, new DateTime(2023, 6, 15, 23, 59, 0));
        File.SetCreationTime(recent, new DateTime(2025, 1, 2, 0, 1, 0));
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        library.DateFrom = new DateTime(2023, 1, 1);
        Assert.Equal(["mid.png", "new.png"], library.Photos.Select(c => c.FileName).OrderBy(x => x));
        library.DateTo = new DateTime(2023, 6, 15);                                     // o dia final conta inteiro
        Assert.Equal(["mid.png"], library.Photos.Select(c => c.FileName));
        library.DateFrom = null;
        Assert.Equal(["mid.png", "old.png"], library.Photos.Select(c => c.FileName).OrderBy(x => x));
    }

    [Fact]
    public async Task AdvancedFilters_AreCounted_ShownInTheLabel_ClearedTogether_AndKeptWhenChangingFolder()
    {
        _env.CreatePng("A/p.png", width: 40, height: 80); _env.CreatePng("B/q.png", width: 40, height: 80); _env.CreatePng("B/r.png", width: 80, height: 40);
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        Assert.Equal(0, library.AdvancedFilterCount);
        Assert.Equal("Filtros avançados", library.AdvancedFiltersLabel);
        Assert.False(library.IsAdvancedOpen);

        library.OrientationChoice = "Retrato";
        library.ExtensionChoice = ".png";
        library.DateFrom = new DateTime(2000, 1, 1);
        library.DateTo = new DateTime(2100, 1, 1);                                       // as duas pontas contam como UM filtro de data
        Assert.Equal(3, library.AdvancedFilterCount);
        Assert.Equal("Filtros avançados (3)", library.AdvancedFiltersLabel);
        Assert.True(library.HasActiveFilters);

        library.SelectFolderCommand.Execute(library.Folders[0].Children.Single(n => n.Label == "B"));
        Assert.Equal(["q.png"], library.Photos.Select(c => c.FileName));                 // trocar de pasta não limpou os avançados
        Assert.Equal(3, library.AdvancedFilterCount);

        library.ClearFiltersCommand.Execute(null);
        Assert.Equal(0, library.AdvancedFilterCount);
        Assert.Equal(("Qualquer", "Todos", "Todas", null, null), (library.OrientationChoice, library.TypeChoice, library.ExtensionChoice, library.DateFrom, library.DateTo));
        Assert.False(library.HasActiveFilters);
    }

    [Fact]
    public async Task RatingFilter_CountsAsAnAdvancedFilter()
    {
        _env.CreatePng("g1.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        library.MinimumRatingChoice = 3;
        Assert.Equal(1, library.AdvancedFilterCount);
        library.MinimumRatingChoice = 0;
        Assert.Equal(0, library.AdvancedFilterCount);
    }

    // ---------- interface ----------

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
    public async Task Ui_AdvancedPanelToggles_ColorMenuHasSevenEntries_AndTheCardShowsTheColorBar()
    {
        _env.CreatePng("u1.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var card = library.Photos.Single();
        await library.SetColorAsync([card], PhotoColor.Orange);
        var report = "";

        RunSta(() =>
        {
            EnsureResources();
            var view = new LibraryView { DataContext = library };
            var window = new Window { Content = view, Width = 1400, Height = 900 };
            window.Show();
            Pump(400);

            // painel avançado: escondido até clicar em "Filtros avançados"
            var typeCombo = Descendants<ComboBox>(view).First(c => ReferenceEquals(c.ItemsSource, LibraryViewModel.TypeChoices));
            var panel = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(typeCombo));
            var hidden = FindWrap(typeCombo).Visibility;
            library.IsAdvancedOpen = true;
            Pump(150);
            report += $"avancado antes={hidden} depois={FindWrap(typeCombo).Visibility}\n";

            var grid = Descendants<Grid>(view).First(g => g.ContextMenu is not null && g.DataContext is PhotoCardViewModel);
            grid.ContextMenu!.PlacementTarget = grid;
            grid.ContextMenu.IsOpen = true;
            Pump(200);
            var colorItem = grid.ContextMenu.Items.OfType<MenuItem>().First(i => (string)i.Header == "Cor");
            report += $"cores={colorItem.Items.Count}\n";
            grid.ContextMenu.IsOpen = false;

            ListBoxItem? container = null;
            for (DependencyObject? node = grid; node is not null; node = VisualTreeHelper.GetParent(node)) if (node is ListBoxItem lbi) { container = lbi; break; }
            Border Frame() => (Border)container!.Template.FindName("Frame", container);
            string Describe() => $"bg={((SolidColorBrush)Frame().Background).Color} borda={((SolidColorBrush)Frame().BorderBrush).Color} esp={Frame().BorderThickness.Left}";
            report += $"normal {Describe()}\n";
            container!.IsSelected = true;
            Pump(100);
            report += $"selecionado {Describe()}\n";
            window.Close();

            static FrameworkElement FindWrap(DependencyObject from)
            {
                for (var node = from; node is not null; node = VisualTreeHelper.GetParent(node)) if (node is WrapPanel w) return w;
                throw new InvalidOperationException("WrapPanel não encontrado");
            }
        });

        Assert.Contains("avancado antes=Collapsed depois=Visible", report);
        Assert.Contains("cores=7", report);
        Assert.Contains("normal bg=#1FF97316 borda=#B3F97316 esp=2", report);          // laranja: fundo suave + borda discreta
        Assert.Contains("selecionado bg=#1FF97316 borda=#FF2563EB esp=3", report);      // seleção: borda azul, fundo da etiqueta preservado
    }
}
