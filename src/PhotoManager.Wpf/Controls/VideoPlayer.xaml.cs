using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace PhotoManager.Wpf.Controls;

/// <summary>
/// Reprodutor embutido (MediaElement do WPF, que usa os codecs do próprio Windows): reproduzir/pausar, posição, volume e mudo.
/// Abre o vídeo pausado no primeiro quadro. O arquivo é liberado (Close) ao trocar de vídeo, ocultar o controle ou descarregar,
/// para nunca impedir mover/renomear/excluir o arquivo.
/// </summary>
public partial class VideoPlayer : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(VideoPlayer), new PropertyMetadata(null, (d, _) => ((VideoPlayer)d).Reload()));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _isPlaying, _isOpen, _seeking, _updatingSlider, _opening;
    private double _volumeBeforeMute = 0.8;

    public VideoPlayer()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdatePosition();
        IsVisibleChanged += (_, _) => Reload();
        Unloaded += (_, _) => Release();
        Media.Volume = VolumeSlider.Value;
    }

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(VideoPlayer), new PropertyMetadata(false, (d, e) => ((VideoPlayer)d).VolumeSlider.Visibility = (bool)e.NewValue ? Visibility.Collapsed : Visibility.Visible));

    public static readonly DependencyProperty RotationDegreesProperty = DependencyProperty.Register(
        nameof(RotationDegrees), typeof(double), typeof(VideoPlayer), new PropertyMetadata(0d, (d, e) => ((VideoPlayer)d).Media.LayoutTransform = new System.Windows.Media.RotateTransform((double)e.NewValue)));

    /// <summary>Giro horário em graus: o MediaElement ignora a rotação gravada no arquivo, então ela (mais o giro manual) é aplicada aqui.</summary>
    public double RotationDegrees { get => (double)GetValue(RotationDegreesProperty); set => SetValue(RotationDegreesProperty, value); }

    /// <summary>Versão para painéis pequenos: sem a barra de volume (o botão de mudo continua).</summary>
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    /// <summary>Caminho do vídeo; nulo/vazio descarrega e libera o arquivo.</summary>
    public string? SourcePath { get => (string?)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }

    /// <summary>Começa a tocar assim que o vídeo abre (o padrão é abrir pausado no primeiro quadro).</summary>
    public bool AutoPlay { get; set; }

    public bool IsPlaying => _isPlaying;

    /// <summary>Posição atual em segundos (marcar entrada/saída de trechos).</summary>
    public double PositionSeconds => _isOpen ? Media.Position.TotalSeconds : 0;

    public void Seek(double seconds)
    {
        if (!_isOpen) return;
        Media.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        UpdatePosition();
    }

    /// <summary>Pausa (sem fechar); usado ao sair da revisão ou trocar de aba.</summary>
    public void Pause()
    {
        if (!_isOpen) return;
        Media.Pause();
        SetPlaying(false);
    }

    public void TogglePlay()
    {
        if (!_isOpen) return;
        if (_isPlaying) Pause();
        else
        {
            if (Media.NaturalDuration.HasTimeSpan && Media.Position >= Media.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(250)) Media.Position = TimeSpan.Zero;
            Media.Play();
            SetPlaying(true);
        }
    }

    /// <summary>Fecha o arquivo e zera o estado.</summary>
    public void Release()
    {
        _timer.Stop();
        _isOpen = false; _opening = false;
        SetPlaying(false);
        try { Media.Stop(); Media.Close(); } catch (InvalidOperationException) { }
        Media.Source = null;
        SeekSlider.IsEnabled = false;
        SeekSlider.Value = 0;
        PositionText.Text = "0:00";
        DurationText.Text = "0:00";
    }

    private void Reload()
    {
        Release();
        ErrorText.Visibility = Visibility.Collapsed;
        if (!IsVisible || string.IsNullOrWhiteSpace(SourcePath)) return;
        if (!System.IO.File.Exists(SourcePath)) { ShowError("O arquivo de vídeo não está disponível."); return; }
        _opening = true;
        Media.Source = new Uri(SourcePath);
        Media.Play();          // MediaOpened pausa no primeiro quadro (sem isso o MediaElement não desenha nada)
    }

    private void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        _isOpen = true;
        var duration = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        SeekSlider.Maximum = Math.Max(duration.TotalSeconds, 0.1);
        SeekSlider.IsEnabled = duration > TimeSpan.Zero;
        DurationText.Text = Format(duration);
        var wasOpening = _opening;
        if (_opening) { Media.Pause(); Media.Position = TimeSpan.Zero; _opening = false; }
        SetPlaying(false);
        _timer.Start();
        if (wasOpening && AutoPlay) TogglePlay();
    }

    private void Media_MediaEnded(object sender, RoutedEventArgs e)
    {
        Media.Pause();
        SetPlaying(false);
        UpdatePosition();
    }

    private void Media_MediaFailed(object? sender, ExceptionRoutedEventArgs e) =>
        ShowError("Não foi possível reproduzir este vídeo. O Windows pode não ter o codec deste formato; a miniatura e os metadados continuam disponíveis.");

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        _timer.Stop();
        _isOpen = false;
        SeekSlider.IsEnabled = false;
        SetPlaying(false);
    }

    private void Media_Click(object sender, MouseButtonEventArgs e) { Focus(); TogglePlay(); }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space) { TogglePlay(); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        PlayGlyph.Text = playing ? "" : "";
    }

    private void UpdatePosition()
    {
        if (!_isOpen || _seeking) return;
        _updatingSlider = true;
        try
        {
            var position = Media.Position;
            if (position.TotalSeconds <= SeekSlider.Maximum) SeekSlider.Value = position.TotalSeconds;
            PositionText.Text = Format(position);
        }
        finally { _updatingSlider = false; }
    }

    private void Seek_MouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _seeking = false;
        if (_isOpen) Media.Position = TimeSpan.FromSeconds(SeekSlider.Value);
    }

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || !_isOpen) return;
        PositionText.Text = Format(TimeSpan.FromSeconds(e.NewValue));
        if (!_seeking) Media.Position = TimeSpan.FromSeconds(e.NewValue);   // clique/teclado na barra
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Media is null) return;
        Media.Volume = e.NewValue;
        Media.IsMuted = e.NewValue <= 0;
        MuteGlyph.Text = Media.IsMuted ? "" : "";
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Media.IsMuted || VolumeSlider.Value <= 0) { VolumeSlider.Value = _volumeBeforeMute > 0 ? _volumeBeforeMute : 0.8; }
        else { _volumeBeforeMute = VolumeSlider.Value; VolumeSlider.Value = 0; }
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
