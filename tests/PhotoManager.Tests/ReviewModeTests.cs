using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoManager.Application.Metadata;
using PhotoManager.Application.Microstock;
using PhotoManager.Application.Navigation;
using PhotoManager.Wpf;
using PhotoManager.Wpf.Controls;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

/// <summary>Testes que criam janelas WPF compartilham a Application do processo: executam em série para evitar corridas entre threads STA.</summary>
[Collection("WpfUi")]
public sealed class ReviewModeTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private ReviewViewModel CreateReview(LibraryViewModel library, INavigationService? navigation = null)
    {
        var uploads = new UploadHistoryService(_env.Repository, _env.Repository);
        var review = new ReviewViewModel(library, new PhotoManager.Infrastructure.Metadata.MetadataExtractorReader(), _env.MetadataEditing, _env.Repository,
            new MicrostockEvaluationService(new PhotoManager.Infrastructure.Metadata.MetadataExtractorReader()), uploads, navigation);
        library.Review = review;
        return review;
    }

    private async Task<LibraryViewModel> JpegLibraryAsync(int count = 3)
    {
        for (var i = 1; i <= count; i++)
        {
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, new byte[64 * 48 * 4], 64 * 4), null, new BitmapMetadata("jpg") { CameraModel = "Cam" + i }, null));
            using var stream = File.Create(Path.Combine(_env.Photos, $"p{i}.jpg"));
            encoder.Save(stream);
        }
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        return library;
    }

    // ---------- modo e navegação

    [Fact]
    public async Task EnterReview_NeedsAPhoto_AndExitRestoresBrowseMode()
    {
        var library = await JpegLibraryAsync();
        CreateReview(library);
        library.EnterReview();
        Assert.False(library.IsReviewMode);                 // sem foto selecionada não entra

        library.EnterReview(library.Photos[1]);
        Assert.True(library.IsReviewMode); Assert.False(library.IsBrowseMode);
        Assert.Same(library.Photos[1], library.SelectedPhoto);

        library.ExitReview();
        Assert.False(library.IsReviewMode); Assert.True(library.IsBrowseMode);
    }

    [Fact]
    public async Task ArrowsWalkThePhotos_AndPositionIsFormatted()
    {
        var library = await JpegLibraryAsync();
        CreateReview(library);
        library.EnterReview(library.Photos[0]);
        Assert.Equal("1 de 3", library.PositionText);
        library.NextCommand.Execute(null); library.NextCommand.Execute(null);
        Assert.Equal("3 de 3", library.PositionText);
        Assert.False(library.NextCommand.CanExecute(null));
        library.PreviousCommand.Execute(null);
        Assert.Equal("p2.jpg", library.SelectedPhoto!.FileName);
    }

    [Fact]
    public async Task FullScreen_OnlyInReview_AndLeavingReviewClearsIt()
    {
        var library = await JpegLibraryAsync();
        CreateReview(library);
        library.ToggleFullScreenCommand.Execute(null);
        Assert.False(library.IsFullScreen);                 // fora da revisão não faz nada
        library.EnterReview(library.Photos[0]);
        library.ToggleFullScreenCommand.Execute(null);
        Assert.True(library.IsFullScreen);
        library.ExitReview();
        Assert.False(library.IsFullScreen);
    }

    [Fact]
    public async Task Shell_HidesNavigationInFullScreen()
    {
        for (var i = 1; i <= 2; i++) _env.CreatePng($"p{i}.png");
        var main = new MainViewModel(new NavigationService(), _env.Catalog, _env.Thumbnails, _env.Organization, _env.FileOperations, new PhotoManager.Infrastructure.Metadata.MetadataExtractorReader(), _env.MetadataEditing);
        var library = (LibraryViewModel)main.CurrentView;
        await library.ImportFolderAsync(_env.Photos);
        Assert.True(main.ShowNavigation);
        library.EnterReview(library.Photos[0]);
        library.ToggleFullScreenCommand.Execute(null);
        Assert.True(main.IsFullScreen); Assert.False(main.ShowNavigation);
        library.ExitReview();
        Assert.True(main.ShowNavigation);
    }

    // ---------- dados do painel

    [Fact]
    public async Task Details_LoadForTheSelectedPhoto_AndFollowNavigation()
    {
        var library = await JpegLibraryAsync();
        await _env.MetadataEditing.SaveAsync(library.Photos[0].Photo, new MetadataEdit { Title = "Foto um", Keywords = ["a", "b"], Author = "Ana" });
        var review = CreateReview(library);
        library.EnterReview(library.Photos[0]);
        await TestEnvironment.WaitUntilAsync(() => review.HasMetadata && review.HasMicrostock);

        Assert.Equal("Foto um", review.TitleText); Assert.Equal("Ana", review.AuthorText); Assert.Equal(["a", "b"], review.Keywords);
        Assert.Contains("Cam1", review.CameraText);
        Assert.Contains("IPTC", review.SourcesText);
        Assert.NotEmpty(review.MetadataHistory);                       // versões v1 e "Original"
        Assert.Contains("Perfil", review.MicrostockNote);
        Assert.False(string.IsNullOrEmpty(review.MicrostockStatusText));

        library.NextCommand.Execute(null);
        await TestEnvironment.WaitUntilAsync(() => review.HasMetadata && review.TitleText == "—");
        Assert.Contains("Cam2", review.CameraText);
        Assert.Empty(review.MetadataHistory); Assert.Empty(review.Keywords);
    }

    [Fact]
    public async Task Details_AreNotReadWhileReviewIsInactive()
    {
        var library = await JpegLibraryAsync();
        var review = CreateReview(library);
        library.SelectedPhoto = library.Photos[0];
        await Task.Delay(300);
        Assert.Null(review.Metadata);
        library.EnterReview();
        await TestEnvironment.WaitUntilAsync(() => review.Metadata is not null);
    }

    [Fact]
    public async Task UploadStatusAndHistory_AreShownReadOnly()
    {
        var library = await JpegLibraryAsync(1);
        var uploads = new UploadHistoryService(_env.Repository, _env.Repository);
        var agency = (await uploads.GetAgenciesAsync(true)).First(a => a.Name == "Adobe Stock");
        await uploads.MarkUploadedAsync([library.Photos[0].Photo.Id], [agency.Id], DateTime.UtcNow, "remoto.jpg", null);
        var review = CreateReview(library);
        library.EnterReview(library.Photos[0]);
        await TestEnvironment.WaitUntilAsync(() => review.UploadStatuses.Count == 1 && review.UploadHistory.Count == 1);
        Assert.Equal("Adobe Stock", review.UploadStatuses[0].Title); Assert.Contains("Enviado", review.UploadStatuses[0].Details);
        Assert.Contains("remoto.jpg", review.UploadHistory[0].Details);
    }

    [Fact]
    public async Task MissingFile_ShowsMessage_AndOpenButtonsNavigateOutOfReview()
    {
        var library = await JpegLibraryAsync(1);
        var navigation = new NavigationService();
        var review = CreateReview(library, navigation);
        File.Delete(library.Photos[0].Photo.CurrentPath);
        await library.ApplyFiltersAsync();                  // recarrega: a foto passa a "ausente"
        library.EnterReview(library.Photos[0]);
        await TestEnvironment.WaitUntilAsync(() => review.Metadata is not null);
        Assert.Contains("ausente", review.MetadataError);

        library.EnterReview();
        review.OpenMetadataCommand.Execute(null);
        Assert.False(library.IsReviewMode); Assert.Equal("Metadata", navigation.CurrentKey);
    }

    [Fact]
    public async Task Rating_IsSavedImmediately()
    {
        var library = await JpegLibraryAsync(1);
        var review = CreateReview(library);
        library.EnterReview(library.Photos[0]);
        review.Rating = 4;
        Assert.Equal(4, library.Photos[0].Rating);
        var reloaded = _env.CreateLibrary();
        await TestEnvironment.WaitUntilAsync(() => reloaded.Photos.Count == 1 && reloaded.Photos[0].Rating == 4);
    }

    [Fact]
    public async Task FullResolutionImage_ReplacesThePreviewAfterAShortDelay()
    {
        var library = await JpegLibraryAsync(1);
        var review = CreateReview(library);
        library.EnterReview(library.Photos[0]);
        await TestEnvironment.WaitUntilAsync(() => review.IsFullResolution);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(review.DisplayImage);
        Assert.Equal((64, 48), (bitmap.PixelWidth, bitmap.PixelHeight));    // tamanho natural, sem ampliar nem reduzir
        Assert.True(bitmap.IsFrozen);
    }

    // ---------- ZoomViewer (WPF real)

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

    private static BitmapSource Bitmap(int w, int h) => BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, new byte[w * h * 4], w * 4);

    private static ZoomViewer Viewer(ImageSource source, double width = 200, double height = 150)
    {
        var viewer = new ZoomViewer { Width = width, Height = height, Source = source };
        viewer.Measure(new Size(width, height)); viewer.Arrange(new Rect(0, 0, width, height)); viewer.UpdateLayout();
        return viewer;
    }

    [Fact]
    public void ZoomViewer_FitActualAndZoomSteps()
    {
        RunSta(() =>
        {
            var viewer = Viewer(Bitmap(400, 300));
            Assert.True(viewer.IsFit); Assert.Equal(50, viewer.ZoomPercent);          // 400x300 em 200x150
            viewer.Actual();
            Assert.False(viewer.IsFit); Assert.Equal(100, viewer.ZoomPercent);
            viewer.ZoomIn(); Assert.True(viewer.ZoomPercent > 100);
            viewer.ZoomOut(); viewer.ZoomOut(); Assert.True(viewer.ZoomPercent < 100);
            viewer.Fit();
            Assert.True(viewer.IsFit); Assert.Equal(50, viewer.ZoomPercent);
        });
    }

    [Fact]
    public void ZoomViewer_FitNeverUpscalesSmallImages_AndNewPhotoResetsToFit()
    {
        RunSta(() =>
        {
            var viewer = Viewer(Bitmap(100, 60));
            Assert.Equal(100, viewer.ZoomPercent);                                     // cabe: não amplia
            viewer.Actual(); viewer.ZoomIn();
            Assert.False(viewer.IsFit);
            viewer.Source = Bitmap(800, 600);                                          // outra foto (outra proporção/tamanho)
            viewer.UpdateLayout();
            Assert.True(viewer.IsFit); Assert.Equal(25, viewer.ZoomPercent);
        });
    }

    [Fact]
    public void ZoomViewer_KeepsFramingWhenThePreviewIsReplacedByTheFullImage()
    {
        RunSta(() =>
        {
            var viewer = Viewer(Bitmap(200, 150));            // prévia reduzida
            viewer.Actual(); viewer.ZoomIn();
            var before = viewer.ZoomPercent;
            viewer.Source = Bitmap(400, 300);                 // mesma proporção, o dobro dos pixels
            viewer.UpdateLayout();
            Assert.False(viewer.IsFit);
            Assert.Equal(Math.Round(before / 2), viewer.ZoomPercent);                  // continua do mesmo tamanho na tela (agora = metade do real)
        });
    }

    // ---------- tela real

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

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void RealWindow_ReviewShowsViewerFilmstripPanelAndRespondsToKeys()
    {
        for (var i = 1; i <= 3; i++) _env.CreatePng($"p{i}.png");
        RunSta(() =>
        {
            var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (app.Resources.MergedDictionaries.Count == 0)
                foreach (var name in new[] { "Colors", "Typography", "Buttons", "Inputs", "Cards", "Tabs", "Tree" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/PhotoManager;component/Themes/{name}.xaml") });
            var uploads = new UploadHistoryService(_env.Repository, _env.Repository);
            var main = new MainViewModel(new NavigationService(), _env.Catalog, _env.Thumbnails, _env.Organization, _env.FileOperations, new PhotoManager.Infrastructure.Metadata.MetadataExtractorReader(), _env.MetadataEditing,
                _env.Repository, new MicrostockEvaluationService(new PhotoManager.Infrastructure.Metadata.MetadataExtractorReader()), uploads);
            var window = new MainWindow(main);
            var library = (LibraryViewModel)main.CurrentView;
            window.Show();
            try
            {
                var import = library.ImportFolderAsync(_env.Photos);
                var f = new DispatcherFrame(); import.ContinueWith(_ => f.Continue = false); if (!import.IsCompleted) Dispatcher.PushFrame(f);
                Pump(300); window.UpdateLayout();

                var grid = Find<ListBox>(window, l => l.Name == "PhotoList")!;
                library.EnterReview(library.Photos[0]);
                Pump(300); window.UpdateLayout();

                var filmstrip = Find<ListBox>(window, l => l.Name == "Filmstrip")!;
                Assert.Equal(3, filmstrip.Items.Count);
                Assert.True(filmstrip.IsVisible); Assert.False(grid.IsVisible);       // a revisão substitui a grade
                var viewer = Find<ZoomViewer>(window)!;
                Assert.NotNull(viewer.Source);
                Assert.NotNull(Find<TextBlock>(window, t => t.Text == "1 de 3"));

                var review = Find<ReviewView>(window)!;
                void Press(Key key)
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    review.RaiseEvent(args);
                    Pump(30);
                }
                Press(Key.Right); Assert.Equal("p2.png", library.SelectedPhoto!.FileName);
                Press(Key.End); Assert.Equal("p3.png", library.SelectedPhoto!.FileName);
                Press(Key.Left); Press(Key.Home); Assert.Equal("p1.png", library.SelectedPhoto!.FileName);
                Pump(50);
                for (var wait = 0; !library.Review!.IsFullResolution && wait < 80; wait++) Pump(100);   // a imagem nova volta a "ajustar": espera chegar antes de dar zoom
                Press(Key.D1); Assert.False(viewer.IsFit);
                Press(Key.F); Assert.True(viewer.IsFit);

                filmstrip.SelectedItem = library.Photos[2]; Pump(30);                    // clicar numa miniatura do filmstrip troca a foto
                Assert.Equal("p3.png", library.SelectedPhoto!.FileName);

                Press(Key.Enter); Assert.True(library.IsFullScreen);
                window.UpdateLayout();
                Assert.False(filmstrip.IsVisible);                                        // tela cheia: só a foto e a barra
                Press(Key.Escape); Assert.False(library.IsFullScreen); Assert.True(library.IsReviewMode);
                Press(Key.Escape); Assert.False(library.IsReviewMode);
                window.UpdateLayout();
                Assert.True(grid.IsVisible);
            }
            finally { window.Close(); }
        });
    }
}
