using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PhotoManager.Wpf.Controls;

/// <summary>
/// Reprodutor de vídeo avançado para visualização no aplicativo:
/// - Aceleração nativa por hardware e decodificação via Windows Media Foundation;
/// - Barra de controles flutuante moderna com esmaecimento automático (auto-hide);
/// - Seekbar precisa com prévia de tempo flutuante no hover e marcadores de corte (I/O);
/// - Avanço/recuo rápido (-5s / +5s) e quadro a quadro (, / .);
/// - Controle de velocidade de reprodução (0.25x a 2x);
/// - Repetição contínua (loop) com suporte a trecho marcado;
/// - Captura de quadros (snapshot) diretamente para a Área de Transferência;
/// - Rotação dinâmica em 90°;
/// - Alternância entre tempo total e tempo restante com um clique;
/// - Ajuste de volume via roda do mouse sobre o vídeo;
/// - Feedback tátil instantâneo na tela (HUD);
/// - Cartão elegante com diagnóstico de codecs e atalho para abrir no reprodutor padrão do sistema.
/// </summary>
public partial class VideoPlayer : UserControl
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(VideoPlayer), new PropertyMetadata(null, (d, _) => ((VideoPlayer)d).Reload()));

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(VideoPlayer), new PropertyMetadata(false, (d, e) => ((VideoPlayer)d).ApplyCompactMode((bool)e.NewValue)));

    public static readonly DependencyProperty RotationDegreesProperty = DependencyProperty.Register(
        nameof(RotationDegrees), typeof(double), typeof(VideoPlayer), new PropertyMetadata(0d, (d, e) => ((VideoPlayer)d).ApplyRotation((double)e.NewValue)));

    public static readonly DependencyProperty MarkerInProperty = DependencyProperty.Register(
        nameof(MarkerIn), typeof(double?), typeof(VideoPlayer), new PropertyMetadata(null, (d, _) => ((VideoPlayer)d).UpdateMarkers()));

    public static readonly DependencyProperty MarkerOutProperty = DependencyProperty.Register(
        nameof(MarkerOut), typeof(double?), typeof(VideoPlayer), new PropertyMetadata(null, (d, _) => ((VideoPlayer)d).UpdateMarkers()));

    public static readonly DependencyProperty IsLoopingProperty = DependencyProperty.Register(
        nameof(IsLooping), typeof(bool), typeof(VideoPlayer), new PropertyMetadata(false, (d, _) => ((VideoPlayer)d).UpdateLoopButtonVisual()));

    private static readonly double[] SpeedLevels = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0];
    private int _speedIndex = 3; // 1.0x

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _autoHideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };

    private bool _isPlaying;
    private bool _isOpen;
    private bool _seeking;
    private bool _updatingSlider;
    private bool _opening;
    private bool _showRemainingTime;
    private double _volumeBeforeMute = 0.8;

    public event RoutedEventHandler? FullscreenRequested;

    public VideoPlayer()
    {
        InitializeComponent();

        _timer.Tick += (_, _) => UpdatePosition();
        _autoHideTimer.Tick += AutoHideTimer_Tick;
        _hudTimer.Tick += HudTimer_Tick;

        IsVisibleChanged += (_, _) => Reload();
        Unloaded += (_, _) => Release();
        SizeChanged += (_, _) => UpdateMarkers();

        Media.Volume = VolumeSlider.Value;
        ApplyCompactMode(IsCompact);
    }

    /// <summary>Giro horário em graus (o MediaElement não rotaciona automaticamente os metadados do MP4).</summary>
    public double RotationDegrees { get => (double)GetValue(RotationDegreesProperty); set => SetValue(RotationDegreesProperty, value); }

    /// <summary>Modo compacto para painéis laterais pequenos da Biblioteca.</summary>
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    /// <summary>Caminho absoluto do vídeo no sistema de arquivos.</summary>
    public string? SourcePath { get => (string?)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }

    /// <summary>Início do trecho marcado em segundos.</summary>
    public double? MarkerIn { get => (double?)GetValue(MarkerInProperty); set => SetValue(MarkerInProperty, value); }

    /// <summary>Fim do trecho marcado em segundos.</summary>
    public double? MarkerOut { get => (double?)GetValue(MarkerOutProperty); set => SetValue(MarkerOutProperty, value); }

    /// <summary>Repetição contínua (se houver corte I/O, repete dentro do intervalo).</summary>
    public bool IsLooping { get => (bool)GetValue(IsLoopingProperty); set => SetValue(IsLoopingProperty, value); }

    /// <summary>Inicia a reprodução automaticamente ao abrir.</summary>
    public bool AutoPlay { get; set; }

    public bool IsPlaying => _isPlaying;

    /// <summary>Posição atual em segundos (para marcação de corte I/O).</summary>
    public double PositionSeconds => _isOpen ? Media.Position.TotalSeconds : 0;

    /// <summary>Duração total em segundos.</summary>
    public double DurationSeconds => _isOpen && Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan.TotalSeconds : 0;

    public void Play()
    {
        if (!_isOpen) return;
        if (Media.NaturalDuration.HasTimeSpan && Media.Position >= Media.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(200))
        {
            Media.Position = MarkerIn.HasValue ? TimeSpan.FromSeconds(MarkerIn.Value) : TimeSpan.Zero;
        }
        Media.Play();
        SetPlaying(true);
        ShowHud("Reproduzindo", "");
        ResetAutoHide();
    }

    public void Pause()
    {
        if (!_isOpen) return;
        Media.Pause();
        SetPlaying(false);
        ShowHud("Pausado", "");
        ResetAutoHide();
    }

    public void TogglePlay()
    {
        if (!_isOpen) return;
        if (_isPlaying) Pause();
        else Play();
    }

    public void Seek(double seconds)
    {
        if (!_isOpen) return;
        var clamped = Math.Clamp(seconds, 0, SeekSlider.Maximum);
        Media.Position = TimeSpan.FromSeconds(clamped);
        UpdatePosition();
        ResetAutoHide();
    }

    public void SkipSeconds(double deltaSeconds)
    {
        if (!_isOpen) return;
        var current = Media.Position.TotalSeconds;
        Seek(current + deltaSeconds);
        ShowHud(deltaSeconds > 0 ? $"+{deltaSeconds:0}s" : $"{deltaSeconds:0}s", deltaSeconds > 0 ? "" : "");
    }

    public void StepFrame(int direction)
    {
        if (!_isOpen) return;
        if (_isPlaying) Pause();
        var delta = direction * 0.04; // ~25 fps
        var target = Math.Max(0, Media.Position.TotalSeconds + delta);
        Media.Position = TimeSpan.FromSeconds(target);
        UpdatePosition();
        ShowHud(direction > 0 ? "Quadro seguinte (+1)" : "Quadro anterior (-1)", direction > 0 ? "" : "");
    }

    public void ToggleLoop()
    {
        IsLooping = !IsLooping;
        ShowHud(IsLooping ? "Repetição ativada" : "Repetição desativada", "");
    }

    public void ToggleMute()
    {
        if (Media.IsMuted || VolumeSlider.Value <= 0)
        {
            VolumeSlider.Value = _volumeBeforeMute > 0 ? _volumeBeforeMute : 0.8;
            ShowHud($"Volume: {(int)(VolumeSlider.Value * 100)}%", "");
        }
        else
        {
            _volumeBeforeMute = VolumeSlider.Value;
            VolumeSlider.Value = 0;
            ShowHud("Sem som", "");
        }
    }

    public void AdjustVolume(double delta)
    {
        var target = Math.Clamp(VolumeSlider.Value + delta, 0, 1);
        VolumeSlider.Value = target;
        if (target <= 0) ShowHud("Sem som", "");
        else ShowHud($"Volume: {(int)(target * 100)}%", target > 0.6 ? "" : (target > 0.2 ? "" : ""));
    }

    public void CycleSpeed()
    {
        _speedIndex = (_speedIndex + 1) % SpeedLevels.Length;
        SetSpeed(SpeedLevels[_speedIndex]);
    }

    public void SetSpeed(double speed)
    {
        if (Media is null) return;
        Media.SpeedRatio = speed;
        SpeedButtonText.Text = speed == 1.0 ? "1x" : $"{speed:0.##}x";
        ShowHud($"Velocidade: {speed:0.##}x", "");
        ResetAutoHide();
    }

    public void Rotate90()
    {
        RotationDegrees = (RotationDegrees + 90) % 360;
        ShowHud($"Giro: {RotationDegrees:0}°", "");
    }

    public void CaptureSnapshot()
    {
        if (!_isOpen || Media.ActualWidth <= 0 || Media.ActualHeight <= 0) return;
        try
        {
            var width = (int)Math.Max(1, Media.ActualWidth);
            var height = (int)Math.Max(1, Media.ActualHeight);
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(Media);

            BitmapSource finalBitmap = rtb;
            if (RotationDegrees != 0)
            {
                finalBitmap = new TransformedBitmap(rtb, new RotateTransform(RotationDegrees));
            }
            finalBitmap.Freeze();

            Clipboard.SetImage(finalBitmap);
            ShowHud("Quadro copiado para a Área de Transferência!", "");
        }
        catch (Exception)
        {
            ShowHud("Não foi possível capturar o quadro", "");
        }
    }

    public void OpenInExternalPlayer()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || !File.Exists(SourcePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(SourcePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowHud($"Erro ao abrir: {ex.Message}", "");
        }
    }

    /// <summary>Fecha o arquivo e libera todos os recursos.</summary>
    public void Release()
    {
        _timer.Stop();
        _autoHideTimer.Stop();
        _hudTimer.Stop();
        _isOpen = false;
        _opening = false;
        SetPlaying(false);

        try
        {
            Media.Stop();
            Media.Close();
        }
        catch (InvalidOperationException) { }

        Media.Source = null;
        SeekSlider.IsEnabled = false;
        SeekSlider.Value = 0;
        PositionText.Text = "0:00";
        DurationText.Text = "0:00";
        HoverBadge.Visibility = Visibility.Collapsed;
        UpdateMarkers();
    }

    public void Reload()
    {
        Release();
        ErrorCard.Visibility = Visibility.Collapsed;

        if (!IsVisible || string.IsNullOrWhiteSpace(SourcePath)) return;
        if (!File.Exists(SourcePath))
        {
            ShowErrorCard("O arquivo de vídeo não foi encontrado no disco.");
            return;
        }

        _opening = true;
        Media.Source = new Uri(SourcePath);
        Media.Play(); // Necessário para acionar o pipeline do MediaElement e renderizar o primeiro quadro
    }

    private void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        _isOpen = true;
        var duration = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        SeekSlider.Maximum = Math.Max(duration.TotalSeconds, 0.1);
        SeekSlider.IsEnabled = duration > TimeSpan.Zero;
        DurationText.Text = Format(duration);

        var wasOpening = _opening;
        if (_opening)
        {
            Media.Pause();
            Media.Position = TimeSpan.Zero;
            _opening = false;
        }

        SetPlaying(false);
        UpdateMarkers();
        _timer.Start();

        if (wasOpening && AutoPlay) Play();
        ResetAutoHide();
    }

    private void Media_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (IsLooping)
        {
            var start = MarkerIn.HasValue && MarkerIn.Value > 0 ? TimeSpan.FromSeconds(MarkerIn.Value) : TimeSpan.Zero;
            Media.Position = start;
            Media.Play();
            SetPlaying(true);
        }
        else
        {
            Media.Pause();
            SetPlaying(false);
            UpdatePosition();
            ResetAutoHide();
        }
    }

    private void Media_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        var ext = Path.GetExtension(SourcePath)?.ToUpperInvariant() ?? "VÍDEO";
        ShowErrorCard($"O Windows não conseguiu decodificar este arquivo {ext}. O codec necessário (ex.: HEVC/H.265) pode não estar instalado.");
    }

    private void ShowErrorCard(string detail)
    {
        ErrorDetailsText.Text = detail;
        ErrorCard.Visibility = Visibility.Visible;
        _timer.Stop();
        _autoHideTimer.Stop();
        _isOpen = false;
        SeekSlider.IsEnabled = false;
        SetPlaying(false);
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        PlayGlyph.Text = playing ? "" : "";
        PlayButton.ToolTip = playing ? "Pausar (Espaço)" : "Reproduzir (Espaço)";
    }

    private void UpdatePosition()
    {
        if (!_isOpen || _seeking) return;
        _updatingSlider = true;
        try
        {
            var position = Media.Position;
            if (position.TotalSeconds <= SeekSlider.Maximum)
            {
                SeekSlider.Value = position.TotalSeconds;
            }

            PositionText.Text = Format(position);

            if (_showRemainingTime && Media.NaturalDuration.HasTimeSpan)
            {
                var remaining = Media.NaturalDuration.TimeSpan - position;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                DurationText.Text = $"-{Format(remaining)}";
            }
            else
            {
                var duration = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan : TimeSpan.Zero;
                DurationText.Text = Format(duration);
            }

            // Loop de trecho marcado (MarkerOut)
            if (_isPlaying && IsLooping && MarkerOut.HasValue && position.TotalSeconds >= MarkerOut.Value)
            {
                var start = MarkerIn.HasValue && MarkerIn.Value > 0 ? TimeSpan.FromSeconds(MarkerIn.Value) : TimeSpan.Zero;
                Media.Position = start;
            }
        }
        finally
        {
            _updatingSlider = false;
        }
    }

    private void UpdateMarkers()
    {
        if (MarkerCanvas is null || SeekSlider is null) return;

        var width = SeekSlider.ActualWidth;
        var max = SeekSlider.Maximum;

        if (width <= 0 || max <= 0)
        {
            MarkerRangeRect.Visibility = Visibility.Collapsed;
            MarkerInPin.Visibility = Visibility.Collapsed;
            MarkerOutPin.Visibility = Visibility.Collapsed;
            return;
        }

        var inPos = MarkerIn.HasValue ? Math.Clamp(MarkerIn.Value, 0, max) : (double?)null;
        var outPos = MarkerOut.HasValue ? Math.Clamp(MarkerOut.Value, 0, max) : (double?)null;

        if (inPos.HasValue)
        {
            var x = (inPos.Value / max) * width;
            Canvas.SetLeft(MarkerInPin, Math.Max(0, x - 1));
            MarkerInPin.Visibility = Visibility.Visible;
        }
        else
        {
            MarkerInPin.Visibility = Visibility.Collapsed;
        }

        if (outPos.HasValue)
        {
            var x = (outPos.Value / max) * width;
            Canvas.SetLeft(MarkerOutPin, Math.Max(0, x - 1));
            MarkerOutPin.Visibility = Visibility.Visible;
        }
        else
        {
            MarkerOutPin.Visibility = Visibility.Collapsed;
        }

        if (inPos.HasValue && outPos.HasValue && outPos > inPos)
        {
            var startX = (inPos.Value / max) * width;
            var rangeWidth = ((outPos.Value - inPos.Value) / max) * width;
            Canvas.SetLeft(MarkerRangeRect, startX);
            MarkerRangeRect.Width = Math.Max(2, rangeWidth);
            MarkerRangeRect.Visibility = Visibility.Visible;
        }
        else
        {
            MarkerRangeRect.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateLoopButtonVisual()
    {
        if (LoopButton is null) return;
        if (IsLooping)
        {
            LoopButton.Background = new SolidColorBrush(Color.FromArgb(0x40, 0x3B, 0x82, 0xF6));
            LoopGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
        }
        else
        {
            LoopButton.ClearValue(BackgroundProperty);
            LoopGlyph.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private void ApplyCompactMode(bool compact)
    {
        if (SkipBackButton is null) return;

        if (compact)
        {
            // Oculta controles secundários para painéis estreitos
            SkipBackButton.Visibility = Visibility.Collapsed;
            SkipForwardButton.Visibility = Visibility.Collapsed;
            StepBackButton.Visibility = Visibility.Collapsed;
            StepForwardButton.Visibility = Visibility.Collapsed;
            VolumeSlider.Visibility = Visibility.Collapsed;
            RightSeparator.Visibility = Visibility.Collapsed;
            SpeedButton.Visibility = Visibility.Collapsed;
            LoopButton.Visibility = Visibility.Collapsed;
            SnapshotButton.Visibility = Visibility.Collapsed;
            RotateButton.Visibility = Visibility.Collapsed;
            FullscreenButton.Visibility = Visibility.Collapsed;

            ControlBar.Margin = new Thickness(0);
            ControlBar.CornerRadius = new CornerRadius(0);
            ControlBar.BorderThickness = new Thickness(0, 1, 0, 0);
            ControlBar.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x12, 0x20));
            ControlBar.Opacity = 1.0;
            _autoHideTimer.Stop();
        }
        else
        {
            SkipBackButton.Visibility = Visibility.Visible;
            SkipForwardButton.Visibility = Visibility.Visible;
            StepBackButton.Visibility = Visibility.Visible;
            StepForwardButton.Visibility = Visibility.Visible;
            VolumeSlider.Visibility = Visibility.Visible;
            RightSeparator.Visibility = Visibility.Visible;
            SpeedButton.Visibility = Visibility.Visible;
            LoopButton.Visibility = Visibility.Visible;
            SnapshotButton.Visibility = Visibility.Visible;
            RotateButton.Visibility = Visibility.Visible;
            FullscreenButton.Visibility = Visibility.Visible;

            ControlBar.Margin = new Thickness(14, 0, 14, 14);
            ControlBar.CornerRadius = new CornerRadius(10);
            ControlBar.BorderThickness = new Thickness(1);
            ControlBar.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0F, 0x17, 0x2A));
            ResetAutoHide();
        }
    }

    private void ApplyRotation(double degrees)
    {
        Media.LayoutTransform = degrees == 0 ? Transform.Identity : new RotateTransform(degrees);
    }

    private void ResetAutoHide()
    {
        _autoHideTimer.Stop();

        if (IsCompact)
        {
            ControlBar.Opacity = 1.0;
            ControlBar.Visibility = Visibility.Visible;
            Cursor = Cursors.Arrow;
            return;
        }

        ControlBar.BeginAnimation(OpacityProperty, null);
        ControlBar.Opacity = 1.0;
        ControlBar.Visibility = Visibility.Visible;
        Cursor = Cursors.Arrow;

        if (_isPlaying && !ControlBar.IsMouseOver && !SpeedButton.ContextMenu?.IsOpen == true)
        {
            _autoHideTimer.Start();
        }
    }

    private void AutoHideTimer_Tick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        if (!_isPlaying || ControlBar.IsMouseOver || IsCompact || SpeedButton.ContextMenu?.IsOpen == true) return;

        var anim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(400));
        ControlBar.BeginAnimation(OpacityProperty, anim);
        Cursor = Cursors.None;
    }

    private void ShowHud(string text, string iconGlyph)
    {
        HudIcon.Text = iconGlyph;
        HudText.Text = text;
        HudContainer.Visibility = Visibility.Visible;
        HudContainer.BeginAnimation(OpacityProperty, null);
        HudContainer.Opacity = 1.0;

        _hudTimer.Stop();
        _hudTimer.Start();
    }

    private void HudTimer_Tick(object? sender, EventArgs e)
    {
        _hudTimer.Stop();
        var anim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(250))
        {
            FillBehavior = FillBehavior.Stop
        };
        anim.Completed += (_, _) =>
        {
            HudContainer.Opacity = 0.0;
            HudContainer.Visibility = Visibility.Collapsed;
        };
        HudContainer.BeginAnimation(OpacityProperty, anim);
    }

    // Eventos do Mouse e Teclado
    private void Root_MouseMove(object sender, MouseEventArgs e) => ResetAutoHide();

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_isPlaying && !IsCompact) _autoHideTimer.Start();
    }

    private void Root_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        AdjustVolume(e.Delta > 0 ? 0.05 : -0.05);
        e.Handled = true;
    }

    private void Media_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            FullscreenRequested?.Invoke(this, e);
            return;
        }

        if (e.ClickCount == 1)
        {
            TogglePlay();
        }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void SkipBack_Click(object sender, RoutedEventArgs e) => SkipSeconds(-5);

    private void SkipForward_Click(object sender, RoutedEventArgs e) => SkipSeconds(5);

    private void StepBack_Click(object sender, RoutedEventArgs e) => StepFrame(-1);

    private void StepForward_Click(object sender, RoutedEventArgs e) => StepFrame(1);

    private void DurationText_Click(object sender, MouseButtonEventArgs e)
    {
        _showRemainingTime = !_showRemainingTime;
        UpdatePosition();
    }

    private void Seek_MouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _seeking = false;
        if (_isOpen) Media.Position = TimeSpan.FromSeconds(SeekSlider.Value);
        ResetAutoHide();
    }

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || !_isOpen) return;
        PositionText.Text = Format(TimeSpan.FromSeconds(e.NewValue));
        if (!_seeking) Media.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void SeekSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isOpen || SeekSlider.ActualWidth <= 0 || !Media.NaturalDuration.HasTimeSpan) return;
        var pos = e.GetPosition(SeekSlider);
        var ratio = Math.Clamp(pos.X / SeekSlider.ActualWidth, 0, 1);
        var targetSeconds = SeekSlider.Maximum * ratio;
        HoverTimeText.Text = Format(TimeSpan.FromSeconds(targetSeconds));

        var badgeX = pos.X - 18;
        badgeX = Math.Clamp(badgeX, 0, Math.Max(0, SeekSlider.ActualWidth - 40));
        HoverBadge.Margin = new Thickness(badgeX, 0, 0, 18);
        HoverBadge.Visibility = Visibility.Visible;
    }

    private void SeekSlider_MouseLeave(object sender, MouseEventArgs e)
    {
        HoverBadge.Visibility = Visibility.Collapsed;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Media is null) return;
        Media.Volume = e.NewValue;
        Media.IsMuted = e.NewValue <= 0;
        MuteGlyph.Text = Media.IsMuted ? "" : (e.NewValue > 0.6 ? "" : (e.NewValue > 0.2 ? "" : ""));
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void SpeedButton_Click(object sender, RoutedEventArgs e) => CycleSpeed();

    private void SpeedButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var speed in SpeedLevels)
        {
            var item = new MenuItem
            {
                Header = speed == 1.0 ? "1.0x (Normal)" : $"{speed:0.##}x",
                IsChecked = Math.Abs(Media.SpeedRatio - speed) < 0.05
            };
            var s = speed;
            item.Click += (_, _) => SetSpeed(s);
            menu.Items.Add(item);
        }
        SpeedButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void LoopButton_Click(object sender, RoutedEventArgs e) => ToggleLoop();

    private void SnapshotButton_Click(object sender, RoutedEventArgs e) => CaptureSnapshot();

    private void RotateButton_Click(object sender, RoutedEventArgs e) => Rotate90();

    private void OpenExternal_Click(object sender, RoutedEventArgs e) => OpenInExternalPlayer();

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => FullscreenRequested?.Invoke(this, e);

    private void Retry_Click(object sender, RoutedEventArgs e) => Reload();

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourcePath)) return;
        try
        {
            Clipboard.SetText(SourcePath);
            ShowHud("Caminho copiado!", "");
        }
        catch (Exception) { }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.Space: TogglePlay(); e.Handled = true; return;
                case Key.Left: SkipSeconds(-5); e.Handled = true; return;
                case Key.Right: SkipSeconds(5); e.Handled = true; return;
                case Key.Up: AdjustVolume(0.05); e.Handled = true; return;
                case Key.Down: AdjustVolume(-0.05); e.Handled = true; return;
                case Key.M: ToggleMute(); e.Handled = true; return;
                case Key.L: ToggleLoop(); e.Handled = true; return;
                case Key.S: CaptureSnapshot(); e.Handled = true; return;
                case Key.R: Rotate90(); e.Handled = true; return;
                case Key.OemComma: StepFrame(-1); e.Handled = true; return;
                case Key.OemPeriod: StepFrame(1); e.Handled = true; return;
                case Key.Home: Seek(0); e.Handled = true; return;
                case Key.End: Seek(SeekSlider.Maximum); e.Handled = true; return;
                case >= Key.D0 and <= Key.D9:
                    var pct = (e.Key - Key.D0) / 10.0;
                    Seek(SeekSlider.Maximum * pct);
                    e.Handled = true;
                    return;
                case >= Key.NumPad0 and <= Key.NumPad9:
                    var numPct = (e.Key - Key.NumPad0) / 10.0;
                    Seek(SeekSlider.Maximum * numPct);
                    e.Handled = true;
                    return;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            switch (e.Key)
            {
                case Key.Left: SkipSeconds(-1); e.Handled = true; return;
                case Key.Right: SkipSeconds(1); e.Handled = true; return;
            }
        }

        base.OnKeyDown(e);
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
