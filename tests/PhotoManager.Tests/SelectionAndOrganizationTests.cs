using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Navigation;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

/// <summary>Testes que criam janelas WPF compartilham a Application do processo: executam em série para evitar corridas entre threads STA.</summary>
[Collection("WpfUi")]
public sealed class SelectionAndOrganizationTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    // ---------- lógica pura

    [Fact]
    public void EmptyFields_MeanKeep()
    {
        Assert.True(new OrganizationBatch().IsNoOp);
        var photo = new Photo { CategoryName = "A", Rating = 3, IsFavorite = true, PersonalNote = "n", Tags = ["x"] };
        Assert.False(new OrganizationBatch().ApplyTo(photo));
        Assert.Equal(("A", 3, true, "n"), (photo.CategoryName, photo.Rating, photo.IsFavorite, photo.PersonalNote));
    }

    [Fact]
    public void TagsAndCollectionsAreAddedWithoutRemovingOrDuplicating()
    {
        var photo = new Photo { Tags = ["praia"], CollectionIds = [1L] };
        var plan = new OrganizationBatch { TagsToAdd = OrganizationBatch.ParseList("Praia, sol; mar, SOL"), CollectionsToAdd = [2L] };
        Assert.True(plan.ApplyTo(photo));
        Assert.Equal(["praia", "sol", "mar"], photo.Tags);
        Assert.Equal([1L, 2L], photo.CollectionIds);
        Assert.False(plan.ApplyTo(photo));   // idempotente
    }

    [Fact]
    public void RatingFavoriteCategoryAndNote_AreSetOnlyWhenRequested()
    {
        var photo = new Photo { Rating = 4, IsFavorite = true };
        Assert.True(new OrganizationBatch { Rating = 0, Favorite = false, Category = " Viagens ", Note = "nota" }.ApplyTo(photo));
        Assert.Equal((0, false, "Viagens", "nota"), (photo.Rating, photo.IsFavorite, photo.CategoryName, photo.PersonalNote));
        Assert.True(new OrganizationBatch { Rating = 9 }.ApplyTo(photo));
        Assert.Equal(5, photo.Rating);   // limitado a 5
    }

    // ---------- ViewModel

    private async Task<LibraryViewModel> LibraryAsync(int count = 3)
    {
        for (var i = 1; i <= count; i++) _env.CreatePng($"p{i}.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        return library;
    }

    [Fact]
    public async Task SelectionState_DrivesTitlesTargetTextAndModes()
    {
        var library = await LibraryAsync();
        Assert.False(library.HasSelection); Assert.Equal("Nenhuma selecionada", library.SelectionBadgeText);
        Assert.Contains("Selecione fotos", library.TargetText);

        library.UpdateSelection([library.Photos[0]]);
        Assert.True(library.IsSingleSelection); Assert.False(library.IsMultiSelection);
        Assert.Equal("1 foto selecionada", library.SelectionTitle);

        library.UpdateSelection(library.Photos.ToList());
        Assert.True(library.IsMultiSelection);
        Assert.Equal("3 fotos selecionadas", library.SelectionTitle); Assert.Equal("3 selecionadas", library.SelectionBadgeText);
        Assert.Equal("Será aplicado às 3 fotos selecionadas.", library.TargetText);
        Assert.Contains("3 PNG", library.SelectionDetails);
        Assert.Equal(3, library.EditableSelectionCount); Assert.True(library.HasEditableSelection);   // PNG grava no sidecar .xmp
        Assert.Equal(3, library.SelectionThumbnails.Count);
        Assert.Contains("3 selecionada(s)", library.SummaryText);
    }

    [Fact]
    public async Task OrganizationBatch_AppliesToSelection_Persists_AndReportsImpact()
    {
        var library = await LibraryAsync();
        var selected = library.Photos.Take(2).ToList();
        library.UpdateSelection(selected);
        Assert.False(library.CanApplyOrganizationBatch);
        Assert.Contains("Nada será alterado", library.OrganizationImpactText);

        library.BatchCategory = "Viagens"; library.BatchTagsText = "praia, sol"; library.BatchRatingIndex = 5; library.BatchFavoriteIndex = 1;
        Assert.True(library.CanApplyOrganizationBatch);
        Assert.Contains("2 foto(s)", library.OrganizationImpactText); Assert.Contains("categoria “Viagens”", library.OrganizationImpactText); Assert.Contains("4 estrela", library.OrganizationImpactText);

        await library.ApplyOrganizationBatchAsync();
        var reloaded = _env.CreateLibrary();
        await TestEnvironment.WaitUntilAsync(() => reloaded.Photos.Count == 3);
        var changed = reloaded.Photos.Where(p => p.Photo.CategoryName == "Viagens").ToList();
        Assert.Equal(2, changed.Count);
        Assert.All(changed, p => { Assert.Equal(["praia", "sol"], p.Photo.Tags.Order()); Assert.Equal(4, p.Photo.Rating); Assert.True(p.Photo.IsFavorite); });
        Assert.Contains("Organização aplicada a 2", library.StatusText);
        Assert.False(library.CanApplyOrganizationBatch);            // formulário limpo após aplicar
        Assert.Equal(string.Empty, library.BatchCategory);
        Assert.Null(library.Photos.Single(p => p.Photo.CategoryName != "Viagens").Photo.CategoryName);   // a 3ª não foi tocada
    }

    [Fact]
    public async Task OrganizationBatch_ReportsHowManyWereAlreadyAsRequested()
    {
        var library = await LibraryAsync(2);
        var plan = new OrganizationBatch { Category = "X" };
        await library.ApplyOrganizationBatchAsync(library.Photos.Take(1).ToList(), plan);
        await library.ApplyOrganizationBatchAsync(library.Photos.ToList(), plan);
        Assert.Contains("1 de 2", library.StatusText);
    }

    [Fact]
    public async Task RenameSelection_UsesNameForOnePhotoAndTemplateForMany()
    {
        var library = await LibraryAsync(2);
        library.UpdateSelection([library.Photos.First(p => p.FileName == "p1.png")]);
        await library.RenameSelectionAsync("unica");
        Assert.True(File.Exists(Path.Combine(_env.Photos, "unica.png")));

        library.UpdateSelection(library.Photos.ToList());
        await library.RenameSelectionAsync("{name}_ok");
        Assert.True(File.Exists(Path.Combine(_env.Photos, "unica_ok.png"))); Assert.True(File.Exists(Path.Combine(_env.Photos, "p2_ok.png")));
    }

    [Fact]
    public async Task SelectionIsRestoredAfterTheGridIsRebuilt()
    {
        var library = await LibraryAsync();
        library.UpdateSelection(library.Photos.Take(2).ToList());
        IReadOnlyList<PhotoCardViewModel>? restored = null;
        library.SelectionRestoreRequested += cards => restored = cards;
        library.TagChoice = LibraryViewModel.AllChoice;   // qualquer refiltragem reconstrói a lista
        Assert.Equal(2, restored!.Count);
    }

    // ---------- tela real (WPF)

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception ex) { failure = ex; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && (match is null || match(t))) return t;
            if (Find(child, match) is { } nested) return nested;
        }
        return default;
    }

    [Fact]
    public void RealGrid_CheckboxesSelectMultiplePhotos_AndPanelFollowsTheSelection()
    {
        for (var i = 1; i <= 3; i++) _env.CreatePng($"p{i}.png");
        RunSta(() =>
        {
            var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (app.Resources.MergedDictionaries.Count == 0)
                foreach (var name in new[] { "Colors", "Typography", "Buttons", "Inputs", "Cards", "Tabs", "Tree" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/PhotoManager;component/Themes/{name}.xaml") });
            var viewModel = new MainViewModel(new NavigationService(), _env.Catalog, _env.Thumbnails, _env.Organization, _env.FileOperations, new PhotoManager.Infrastructure.Metadata.MetadataExtractorReader(), _env.MetadataEditing);
            var window = new MainWindow(viewModel);
            var library = (LibraryViewModel)viewModel.CurrentView;
            window.Show();
            try
            {
                var import = library.ImportFolderAsync(_env.Photos);
                var frame = new DispatcherFrame(); import.ContinueWith(_ => frame.Continue = false); if (!import.IsCompleted) Dispatcher.PushFrame(frame);
                Pump(300); window.UpdateLayout();

                var list = Find<ListBox>(window, l => l.Name == "PhotoList")!;
                Assert.Equal(3, list.Items.Count);
                var boxes = Enumerable.Range(0, 3).Select(i => Find<CheckBox>((DependencyObject)list.ItemContainerGenerator.ContainerFromIndex(i))!).ToList();
                Assert.All(boxes, b => Assert.NotNull(b));

                boxes[0].IsChecked = true; boxes[2].IsChecked = true;                 // clique nas caixas = seleção múltipla sem Ctrl
                Pump(50);
                Assert.Equal(2, library.SelectionCount); Assert.True(library.IsMultiSelection);
                Assert.Equal(2, list.SelectedItems.Count);
                Assert.Equal(false, boxes[1].IsChecked);

                window.UpdateLayout();
                var summary = Find<TextBlock>(window, t => t.Text == "2 fotos selecionadas");
                Assert.NotNull(summary);                                               // o painel mostra o resumo da seleção

                list.SelectAll(); Pump(50);
                Assert.Equal(3, library.SelectionCount); Assert.All(boxes, b => Assert.Equal(true, b.IsChecked));
                list.UnselectAll(); Pump(50);
                Assert.Equal(0, library.SelectionCount); Assert.All(boxes, b => Assert.Equal(false, b.IsChecked));

                boxes[1].IsChecked = true; Pump(50);
                Assert.True(library.IsSingleSelection);

                // aplica organização em lote: a seleção deve permanecer marcada depois que a grade é reconstruída
                boxes[0].IsChecked = true; Pump(50);
                var apply = library.ApplyOrganizationBatchAsync(library.SelectedCards, new OrganizationBatch { Category = "Lote" });
                var f2 = new DispatcherFrame(); apply.ContinueWith(_ => f2.Continue = false); if (!apply.IsCompleted) Dispatcher.PushFrame(f2);
                Pump(300);
                Assert.Equal(2, library.SelectionCount);
                Assert.Equal(2, list.SelectedItems.Count);
            }
            finally { window.Close(); }
        });
    }
}
