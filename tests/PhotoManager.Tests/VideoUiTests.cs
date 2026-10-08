using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoManager.Infrastructure.Metadata;
using PhotoManager.Wpf.Controls;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

[Collection("WpfUi")]
public sealed class VideoUiTests : IDisposable
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

    private static void Pump(int ms = 200)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void EnsureResources()
    {
        var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
            foreach (var name in new[] { "Colors", "Typography", "Buttons", "Inputs", "Cards", "Tabs", "Tree" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/PhotoManager;component/Themes/{name}.xaml") });
        if (!app.Resources.Contains("BooleanToVisibilityConverter")) app.Resources.Add("BooleanToVisibilityConverter", new System.Windows.Controls.BooleanToVisibilityConverter());
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

    [Fact]
    public async Task Review_ShowsThePlayerForVideos_AndTheZoomViewerForPhotos_ReleasingTheFile()
    {
        var png = _env.CreatePng("a.png");
        var videoPath = Path.GetFullPath(Path.Combine(_env.Photos, "b.mp4"));
        File.WriteAllBytes(videoPath, FakeMp4.Build(640, 360, 5));
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var review = new ReviewViewModel(library, new MetadataExtractorReader());
        library.Review = review;
        var videoCard = library.Photos.Single(c => c.IsVideo);
        var photoCard = library.Photos.Single(c => !c.IsVideo);
        var report = "";

        RunSta(() =>
        {
            EnsureResources();
            var view = new ReviewView { DataContext = review };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            window.Show();
            library.SelectedPhoto = videoCard;
            library.EnterReview(videoCard);
            Pump(600);

            var player = Descendants<VideoPlayer>(view).Single();
            var viewer = Descendants<ZoomViewer>(view).Single();
            report += $"video: player={player.Visibility} viewer={viewer.Visibility} source={(player.SourcePath == videoPath ? "ok" : player.SourcePath ?? "null")}\n";

            library.SelectedPhoto = photoCard;
            Pump(400);
            report += $"foto: player={player.Visibility} viewer={viewer.Visibility} source={player.SourcePath ?? "null"}\n";

            // O arquivo de vídeo não pode ficar preso pelo reprodutor depois de trocar de mídia.
            try { using var fs = new FileStream(videoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); report += "arquivo livre\n"; }
            catch (IOException) { report += "ARQUIVO PRESO\n"; }

            library.ExitReview();
            window.Close();
        });

        Assert.Contains("video: player=Visible viewer=Collapsed source=ok", report);
        Assert.Contains("foto: player=Collapsed viewer=Visible source=null", report);
        Assert.Contains("arquivo livre", report);
        Assert.True(File.Exists(png));
    }

    [Fact]
    public void VideoPlayer_Instantiates_AndHandlesMissingAndInvalidFiles_WithoutThrowing()
    {
        RunSta(() =>
        {
            EnsureResources();
            var player = new VideoPlayer();
            var window = new Window { Content = player, Width = 800, Height = 500 };
            window.Show();
            player.SourcePath = Path.Combine(_env.Photos, "nao-existe.mp4");
            Pump(200);
            Assert.False(player.IsPlaying);

            var fake = Path.Combine(_env.Photos, "falso.mp4");
            File.WriteAllBytes(fake, FakeMp4.Build(320, 240, 2));
            player.SourcePath = fake;
            Pump(800);
            player.TogglePlay();           // sem mídia decodificável: não pode lançar
            player.Release();
            player.SourcePath = null;
            window.Close();
        });
    }
}
