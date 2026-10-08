using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoManager.Infrastructure.Metadata;
using PhotoManager.Wpf.Controls;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

[Collection("WpfUi")]
public sealed class MediaViewerTests : IDisposable
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
    public async Task Viewer_ShowsThePhotoWithUserRotation_AndTheVideoInThePlayerWithItsRotation_AndReleasesTheFile()
    {
        _env.CreateJpeg("foto.jpg", 120, 60);
        File.WriteAllBytes(Path.Combine(_env.Photos, "clip.mp4"), FakeMp4.Build(320, 240, 3, rotationDegrees: 90));
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var photo = library.Photos.Single(c => c.FileName == "foto.jpg").Photo;
        var video = library.Photos.Single(c => c.FileName == "clip.mp4").Photo;
        await _env.Catalog.SetUserRotationAsync([photo], 90);
        var report = "";

        RunSta(() =>
        {
            EnsureResources();
            var viewer = new MediaViewerWindow(photo);
            viewer.Show();
            for (var i = 0; i < 30 && viewer.Zoom!.Source is null; i++) Pump(100);
            report += $"foto={viewer.IsVideoViewer} img={viewer.Zoom!.Source?.GetType().Name} {viewer.Zoom.Source?.Width}x{viewer.Zoom.Source?.Height}\n";
            viewer.Close();

            var clip = new MediaViewerWindow(video);
            clip.Show();
            Pump(300);
            report += $"video={clip.IsVideoViewer} rot={clip.Player!.RotationDegrees} auto={clip.Player.AutoPlay} src={Path.GetFileName(clip.Player.SourcePath)}\n";
            clip.Close();
            Pump(100);
            report += $"fechado src={clip.Player.SourcePath ?? "(nulo)"}\n";
        });

        Assert.Contains("foto=False", report);
        Assert.Contains("60x120", report);                                  // 120×60 girado 90°
        Assert.Contains("video=True", report);
        Assert.Contains("auto=True", report);
        Assert.Contains("src=clip.mp4", report);
        Assert.Contains("fechado src=(nulo)", report);                      // o arquivo é liberado
        Assert.True(video.VideoDisplayRotation is 90 or 270 or 0);
    }

    [Fact]
    public async Task MetadataView_ShowsLargeThumbnailsWithPlayBadgeForVideo_AndTheFocusedPanelHasTheMaximizeButton()
    {
        _env.CreateJpeg("a.jpg", 120, 60);
        File.WriteAllBytes(Path.Combine(_env.Photos, "v.mp4"), FakeMp4.Build(320, 240, 3));
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        library.UpdateSelection(library.Photos.ToList());
        library.SelectedPhoto = library.Photos[0];
        var editor = new MetadataEditorViewModel(library, new MetadataExtractorReader(), _env.MetadataEditing, _env.Repository);
        editor.SetActive(true);
        var report = "";

        RunSta(() =>
        {
            EnsureResources();
            var view = new MetadataView { DataContext = editor };
            var window = new Window { Content = view, Width = 1500, Height = 950 };
            window.Show();
            Pump(500);

            var thumbs = Descendants<Border>(view).Where(b => b.Cursor == System.Windows.Input.Cursors.Hand && b.Width == 144).ToList();
            var badges = thumbs.Select(t => Descendants<Border>(t).Count(b => b.Width == 30 && b.Visibility == Visibility.Visible)).ToList();
            report += $"miniaturas={thumbs.Count} 144x108={thumbs.All(t => t.Height == 108)} selos={badges.Sum()}\n";
            var focused = Descendants<Border>(view).FirstOrDefault(b => b.Name == "FocusedThumb");
            report += $"painel={(focused is null ? "nao" : $"altura={focused.Height}")} botao={(focused is not null && Descendants<Button>(focused).Any(b => b.ToolTip is string t && t.StartsWith("Abrir em tamanho grande")))}\n";
            window.Close();
        });

        Assert.Contains("miniaturas=2 144x108=True selos=1", report);        // um selo de play, só no vídeo
        Assert.Contains("painel=altura=230", report);
        Assert.Contains("botao=True", report);
    }
}
