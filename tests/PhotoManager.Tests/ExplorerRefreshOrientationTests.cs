using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Controls;
using PhotoManager.Wpf.ViewModels;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

[Collection("WpfUi")]
public sealed class ExplorerRefreshOrientationTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    // ---------- orientação ----------

    [Theory]
    [InlineData(1920, 1080, PhotoOrientation.Landscape, "Paisagem")]
    [InlineData(1080, 1920, PhotoOrientation.Portrait, "Retrato")]
    [InlineData(1000, 1000, PhotoOrientation.Square, "Quadrada")]
    public void Orientation_IsDerivedFromDimensions(int width, int height, PhotoOrientation expected, string text)
    {
        var photo = new Photo { FileName = "a.jpg", CurrentPath = @"X:\a.jpg", Extension = ".jpg", Width = width, Height = height };
        Assert.Equal(expected, photo.Orientation);
        Assert.Equal(text, new PhotoCardViewModel(photo).OrientationText);
    }

    [Fact]
    public void Orientation_IsUnknownWithoutDimensions()
    {
        var photo = new Photo { FileName = "a.mkv", CurrentPath = @"X:\a.mkv", Extension = ".mkv" };
        Assert.Equal(PhotoOrientation.Unknown, photo.Orientation);
        Assert.Equal("—", new PhotoCardViewModel(photo).OrientationText);
    }

    [Fact]
    public async Task OrientationFilter_ListsOnlyMatching_AndIsClearedWithTheOtherFilters()
    {
        _env.CreatePng("land.png", width: 80, height: 40);
        _env.CreatePng("port.png", width: 40, height: 80);
        _env.CreatePng("sq.png", width: 50, height: 50);
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        Assert.Equal(3, library.Photos.Count);
        Assert.False(library.HasActiveFilters);

        library.OrientationChoice = "Retrato";
        Assert.Equal(["port.png"], library.Photos.Select(c => c.FileName));
        Assert.True(library.HasActiveFilters);

        library.OrientationChoice = "Paisagem";
        Assert.Equal(["land.png"], library.Photos.Select(c => c.FileName));
        library.OrientationChoice = "Quadrada";
        Assert.Equal(["sq.png"], library.Photos.Select(c => c.FileName));

        library.ClearFiltersCommand.Execute(null);
        Assert.Equal("Qualquer", library.OrientationChoice);
        Assert.Equal(3, library.Photos.Count);
        Assert.Contains("Retrato", LibraryViewModel.OrientationChoices);
    }

    // ---------- Explorer ----------

    [Fact]
    public async Task ShowInExplorer_SelectsTheFile_OrOpensTheFolderWhenTheFileIsGone()
    {
        var path = _env.CreatePng("e1.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var calls = new List<(string Path, bool Select)>();
        library.OpenInExplorer = (p, select) => calls.Add((p, select));
        var card = library.Photos.Single();

        library.ShowInExplorer(card);
        Assert.Equal([(path, true)], calls);

        File.Delete(path);
        library.ShowInExplorer(card);
        Assert.Equal((_env.Photos, false), calls[1]);
        Assert.Contains("pasta foi aberta", library.StatusText);

        library.ShowFolderInExplorer(Path.Combine(_env.Photos, "nao-existe"));
        Assert.Equal(2, calls.Count);                                   // pasta inexistente: não abre nada
        Assert.Contains("não existe mais", library.StatusText);
        library.ShowFolderInExplorer(_env.Photos);
        Assert.Equal((_env.Photos, false), calls[2]);
    }

    // ---------- excluir pelo menu ----------

    [Fact]
    public async Task RecycleFromMenu_AsksFirst_AndAppliesToTheWholeSelectionOnlyWhenTheCardIsInIt()
    {
        var a = _env.CreatePng("d1.png");
        var b = _env.CreatePng("d2.png");
        var c = _env.CreatePng("d3.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var cards = library.Photos.OrderBy(x => x.FileName).ToList();
        library.UpdateSelection([cards[0], cards[1]]);

        Assert.Equal(2, library.ContextTargets(cards[0]).Count);        // clicou numa selecionada: vale para a seleção
        Assert.Equal([cards[2]], library.ContextTargets(cards[2]));     // clicou fora da seleção: só ela

        var asked = new List<int>();
        library.ConfirmRecycle = count => { asked.Add(count); return false; };
        await library.RecycleFromMenuAsync(cards[0]);
        await library.RecycleFromMenuAsync(cards[2]);
        Assert.Equal([2, 1], asked);
        Assert.True(File.Exists(a) && File.Exists(b) && File.Exists(c));   // recusou: nada foi excluído
    }

    // ---------- atualizar pasta ----------

    [Fact]
    public async Task RefreshFolder_ImportsNewFiles_AndMarksRemovedOnesAsMissing()
    {
        var keep = _env.CreatePng("sub/r1.png");
        var removed = _env.CreatePng("sub/r2.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        Assert.Equal(2, library.Photos.Count);

        var added = _env.CreatePng("sub/r3.png");
        File.Delete(removed);
        MissingItemsViewModel? shown = null;
        library.ShowMissingDialog = vm => shown = vm;
        await library.RefreshFolderAsync(Path.Combine(_env.Photos, "sub"));

        Assert.Equal(3, library.Photos.Count);                                  // o ausente continua no catálogo
        Assert.True(library.Photos.Single(x => x.FileName == "r2.png").IsMissing);
        Assert.False(library.Photos.Single(x => x.FileName == "r3.png").IsMissing);
        Assert.True(File.Exists(keep) && File.Exists(added));
        Assert.Contains("1 novo(s), 1 ausente(s)", library.StatusText);
        Assert.NotNull(shown);                                                  // o que não foi encontrado é listado no fim
        var entry = Assert.Single(shown!.Entries).Entry;
        Assert.True(entry.FolderExists);
        Assert.Equal(1, entry.FileCount);

        await library.RefreshFolderAsync(null);                                  // sem pasta: todas as importadas
        Assert.Contains("0 novo(s), 1 ausente(s)", library.StatusText);
    }

    [Fact]
    public async Task RefreshFolder_WhenTheFolderNoLongerExists_MarksItsFilesMissing_WithoutError()
    {
        var folder = Path.Combine(_env.Photos, "some");
        _env.CreatePng("some/x1.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        for (var attempt = 0; ; attempt++)   // a geração de miniaturas pode estar lendo o arquivo por alguns ms
        {
            try { Directory.Delete(folder, recursive: true); break; }
            catch (IOException) when (attempt < 20) { await Task.Delay(100); }
        }

        MissingItemsViewModel? shown = null;
        library.ShowMissingDialog = vm => shown = vm;
        await library.RefreshFolderAsync(folder);

        Assert.True(library.Photos.Single().IsMissing);
        Assert.Contains("não existem mais", library.StatusText);
        var gone = Assert.Single(shown!.Entries).Entry;
        Assert.False(gone.FolderExists);
        Assert.Equal(1, gone.FileCount);
        Assert.DoesNotContain("Erro", library.StatusText);
    }

    // ---------- vídeo no painel de preview ----------

    [Fact]
    public async Task SelectedVideo_ExposesThePathOnlyForASingleExistingVideo()
    {
        _env.CreatePng("p.png");
        var video = Path.GetFullPath(Path.Combine(_env.Photos, "v.mp4"));
        File.WriteAllBytes(video, FakeMp4.Build(320, 240, 4));
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var videoCard = library.Photos.Single(c => c.IsVideo);
        var photoCard = library.Photos.Single(c => !c.IsVideo);

        library.SelectedPhoto = videoCard;
        library.UpdateSelection([videoCard]);
        Assert.True(library.IsSelectedVideo);
        Assert.Equal(video, library.SelectedVideoPath);

        library.UpdateSelection([videoCard, photoCard]);                 // seleção múltipla: não toca
        Assert.Null(library.SelectedVideoPath);

        library.UpdateSelection([videoCard]);
        library.SelectedPhoto = photoCard;
        Assert.False(library.IsSelectedVideo);
        Assert.Null(library.SelectedVideoPath);
    }

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
    public async Task Ui_PreviewPlayer_ReleasesTheVideoSoItCanBeMoved_AndMenusAreBound()
    {
        var videoPath = Path.GetFullPath(Path.Combine(_env.Photos, "mv.mp4"));
        File.WriteAllBytes(videoPath, FakeMp4.Build(640, 360, 5));
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var card = library.Photos.Single();
        var dest = Path.Combine(_env.Photos, "destino");
        var report = "";

        RunSta(() =>
        {
            EnsureResources();
            var view = new LibraryView { DataContext = library };
            var window = new Window { Content = view, Width = 1300, Height = 900 };
            window.Show();
            Pump(400);

            library.SelectedPhoto = card;
            library.UpdateSelection([card]);
            Pump(600);
            var player = Descendants<VideoPlayer>(view).Single(p => p.IsCompact);
            report += $"player={player.Visibility} compacto={player.IsCompact} fonte={(player.SourcePath == videoPath ? "ok" : player.SourcePath ?? "null")}\n";

            // Mover o arquivo enquanto o painel o exibe: o player precisa soltá-lo.
            var move = library.MoveSelectedAsync([card], dest);
            var guard = 0;
            while (!move.IsCompleted && guard++ < 50) Pump(100);
            report += $"movido={File.Exists(Path.Combine(dest, "mv.mp4"))} origemRemovida={!File.Exists(videoPath)}\n";

            // Menus de contexto ligados aos comandos do ViewModel.
            var photoDock = Descendants<Grid>(view).First(g => g.ContextMenu is not null && g.DataContext is PhotoCardViewModel);
            photoDock.ContextMenu!.PlacementTarget = photoDock;
            photoDock.ContextMenu.IsOpen = true;
            Pump(200);
            report += "menuFoto=" + string.Join("|", photoDock.ContextMenu.Items.OfType<MenuItem>().Select(i => $"{i.Header}:{(i.Command is null ? "NULL" : "ok")}")) + "\n";
            photoDock.ContextMenu.IsOpen = false;

            var folderDock = Descendants<DockPanel>(view).First(d => d.ContextMenu is not null && d.DataContext is FolderNode);
            folderDock.ContextMenu!.PlacementTarget = folderDock;
            folderDock.ContextMenu.IsOpen = true;
            Pump(200);
            report += "menuPasta=" + string.Join("|", folderDock.ContextMenu.Items.OfType<MenuItem>().Select(i => $"{i.Header}:{(i.Command is null ? "NULL" : "ok")}")) + "\n";
            folderDock.ContextMenu.IsOpen = false;
            window.Close();
        });

        Assert.Contains("player=Visible compacto=True fonte=ok", report);
        Assert.Contains("movido=True origemRemovida=True", report);
        Assert.Contains("Exibir no Explorer:ok", report);
        Assert.Contains("Excluir (Lixeira)…:ok", report);
        Assert.Contains("menuPasta=Atualizar:ok|Exibir no Explorer:ok", report);
        Assert.Contains("Expandir tudo:ok", report);
        Assert.Contains("Recolher tudo:ok", report);
        Assert.Contains("Remover do catálogo…:ok", report);
        Assert.Contains("Girar à esquerda:ok", report);
        Assert.Contains("Girar à direita:ok", report);
    }
}
