using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PhotoManager.Wpf.Controls;

/// <summary>
/// Prévia de vídeo ao passar o mouse (como no Google Fotos/YouTube): depois de um instante parado sobre a miniatura, o vídeo toca sem som;
/// mover o mouse na horizontal avança pelo vídeo (esquerda = início, direita = fim). Só um vídeo por vez; o arquivo é liberado ao sair.
/// Uso: no Grid da miniatura, <c>HoverVideoPreview.Source="{Binding Caminho}"</c> e <c>HoverVideoPreview.IsEnabled="True"</c>.
/// </summary>
public static class HoverVideoPreview
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached("Source", typeof(string), typeof(HoverVideoPreview), new PropertyMetadata(null, OnChanged));
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(HoverVideoPreview), new PropertyMetadata(false, OnChanged));
    public static readonly DependencyProperty RotationProperty = DependencyProperty.RegisterAttached("Rotation", typeof(double), typeof(HoverVideoPreview), new PropertyMetadata(0d));

    public static string? GetSource(DependencyObject o) => (string?)o.GetValue(SourceProperty);
    public static void SetSource(DependencyObject o, string? value) => o.SetValue(SourceProperty, value);
    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool value) => o.SetValue(IsEnabledProperty, value);
    public static double GetRotation(DependencyObject o) => (double)o.GetValue(RotationProperty);
    public static void SetRotation(DependencyObject o, double value) => o.SetValue(RotationProperty, value);

    private static Session? _active;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel panel) return;
        panel.MouseEnter -= Enter; panel.MouseLeave -= Leave; panel.MouseMove -= Move; panel.Unloaded -= Unloaded;
        if (GetIsEnabled(panel) && !string.IsNullOrEmpty(GetSource(panel)))
        {
            panel.MouseEnter += Enter; panel.MouseLeave += Leave; panel.MouseMove += Move; panel.Unloaded += Unloaded;
        }
    }

    private static void Enter(object sender, MouseEventArgs e)
    {
        if (sender is not Panel panel || GetSource(panel) is not { Length: > 0 } path || !File.Exists(path)) return;
        _active?.Dispose();
        _active = new Session(panel, path, GetRotation(panel));
    }

    private static void Move(object sender, MouseEventArgs e)
    {
        if (_active is { } session && ReferenceEquals(session.Host, sender)) session.Scrub(e.GetPosition(session.Host).X / Math.Max(1, session.Host.ActualWidth));
    }

    private static void Leave(object sender, MouseEventArgs e)
    {
        if (_active is { } session && ReferenceEquals(session.Host, sender)) { session.Dispose(); _active = null; }
    }

    private static void Unloaded(object sender, RoutedEventArgs e) => Leave(sender, null!);

    private sealed class Session : IDisposable
    {
        private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromMilliseconds(350) };
        private readonly MediaElement _media;
        private readonly Rectangle _bar;
        private bool _opened, _disposed, _scrubbing;

        public Session(Panel host, string path, double rotation)
        {
            Host = host;
            _media = new MediaElement
            {
                LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, IsMuted = true, ScrubbingEnabled = true,
                Stretch = Stretch.Uniform, IsHitTestVisible = false, Opacity = 0,
                LayoutTransform = rotation == 0 ? Transform.Identity : new RotateTransform(rotation)
            };
            _bar = new Rectangle { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Fill = new SolidColorBrush(Color.FromRgb(0x5B, 0x9B, 0xFF)), IsHitTestVisible = false, Width = 0 };
            _media.MediaOpened += (_, _) => { _opened = true; _media.Opacity = 1; if (!_scrubbing) _media.Play(); };
            _media.MediaEnded += (_, _) => { _media.Position = TimeSpan.Zero; _media.Play(); };
            _delay.Tick += (_, _) =>
            {
                _delay.Stop();
                if (_disposed) return;
                Host.Children.Add(_media);
                Host.Children.Add(_bar);
                _media.Source = new Uri(path);
                _media.Play();                                              // Manual: Play abre o arquivo
            };
            _delay.Start();
        }

        public Panel Host { get; }

        /// <summary>Mouse na horizontal = posição no vídeo; parar de mexer não volta a tocar sozinho (o usuário está procurando um quadro).</summary>
        public void Scrub(double fraction)
        {
            if (!_opened || !_media.NaturalDuration.HasTimeSpan) return;
            fraction = Math.Clamp(fraction, 0, 1);
            _scrubbing = true;
            _media.Pause();
            _media.Position = TimeSpan.FromMilliseconds(_media.NaturalDuration.TimeSpan.TotalMilliseconds * fraction);
            _bar.Width = Host.ActualWidth * fraction;
        }

        public void Dispose()
        {
            _disposed = true;
            _delay.Stop();
            try { _media.Stop(); _media.Close(); } catch (InvalidOperationException) { }
            _media.Source = null;
            Host.Children.Remove(_media);
            Host.Children.Remove(_bar);
        }
    }
}
