using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using PhotoManager.Application.Metadata;
using PhotoManager.Application.Microstock;
using PhotoManager.Application.Navigation;
using PhotoManager.Infrastructure.Images;
using PhotoManager.Wpf.Commands;

namespace PhotoManager.Wpf.Views;

public sealed record ReviewLine(string Title, string Details);

/// <summary>
/// Modo de revisão: foto grande com zoom, filmstrip e painel com Informações / Metadados / Microstock / Histórico.
/// Compartilha seleção, lista visível e navegação com a Biblioteca; todos os dados das abas são somente leitura
/// (a edição continua nas telas Metadados e Microstock) e só são lidos enquanto o modo está ativo.
/// </summary>
public sealed class ReviewViewModel : ViewModels.ViewModelBase
{
    private const int FullImageDelayMs = 220;

    private readonly IMetadataReader _reader;
    private readonly IMetadataEditService? _editor;
    private readonly IValidationProfileRepository? _profiles;
    private readonly IMicrostockEvaluationService? _evaluation;
    private readonly IUploadHistoryService? _uploads;
    private readonly INavigationService? _navigation;
    private ImageSource? _fullImage;
    private PhotoMetadata? _metadata;
    private MicrostockPhotoEvaluation? _microstock;
    private bool _isActive, _isLoadingDetails;
    private int _fullVersion, _detailsVersion;
    private string _microstockNote = string.Empty;

    public ReviewViewModel(LibraryViewModel library, IMetadataReader reader, IMetadataEditService? editor = null, IValidationProfileRepository? profiles = null,
        IMicrostockEvaluationService? evaluation = null, IUploadHistoryService? uploads = null, INavigationService? navigation = null)
    {
        Library = library;
        _reader = reader;
        _editor = editor;
        _profiles = profiles;
        _evaluation = evaluation;
        _uploads = uploads;
        _navigation = navigation;
        library.PropertyChanged += OnLibraryChanged;
        OpenMetadataCommand = new RelayCommand(_ => Open("Metadata"), _ => Library.HasSelectedPhoto);
        OpenMicrostockCommand = new RelayCommand(_ => Open("Microstock"), _ => Library.HasSelectedPhoto);
    }

    public LibraryViewModel Library { get; }
    public RelayCommand OpenMetadataCommand { get; }
    public RelayCommand OpenMicrostockCommand { get; }
    public ObservableCollection<string> Keywords { get; } = [];
    public ObservableCollection<ReviewLine> MicrostockIssues { get; } = [];
    public ObservableCollection<ReviewLine> UploadStatuses { get; } = [];
    public ObservableCollection<ReviewLine> MetadataHistory { get; } = [];
    public ObservableCollection<ReviewLine> UploadHistory { get; } = [];

    /// <summary>Resolução natural (quando já carregada); até lá, o preview reduzido da Biblioteca.</summary>
    public ImageSource? DisplayImage => _fullImage ?? Library.PreviewImage;
    public bool IsFullResolution => _fullImage is not null;
    public bool HasPhoto => Library.SelectedPhoto is not null;
    /// <summary>A mídia atual é um vídeo: a revisão mostra o reprodutor no lugar do zoom.</summary>
    public bool IsVideo => Library.SelectedPhoto is { IsVideo: true };
    /// <summary>Caminho a reproduzir; nulo fora da revisão ou com arquivo ausente (o reprodutor então libera o arquivo).</summary>
    /// <summary>Graus a girar o player (rotação do arquivo + giro manual).</summary>
    public double VideoRotation => Library.SelectedPhoto?.Photo.VideoDisplayRotation ?? 0;
    public string? VideoPath => _isActive && Library.SelectedPhoto is { IsVideo: true, IsMissing: false } card ? card.Photo.CurrentPath : null;
    public bool IsLoadingDetails { get => _isLoadingDetails; private set { _isLoadingDetails = value; OnPropertyChanged(); } }

    /// <summary>Avaliação com gravação imediata (a de 5 estrelas da barra superior).</summary>
    public int Rating
    {
        get => Library.SelectedPhoto?.Rating ?? 0;
        set
        {
            if (Library.SelectedPhoto is not { } card || card.Rating == value) return;
            card.Rating = value;
            OnPropertyChanged();
            _ = Library.SavePhotoAsync(card);
        }
    }

    // ----- Metadados (somente leitura)
    public PhotoMetadata? Metadata { get => _metadata; private set { _metadata = value; NotifyMetadata(); } }
    public bool HasMetadata => Metadata is { Error: null };
    public string MetadataError => Metadata?.Error ?? string.Empty;
    public string SourcesText => Metadata is { Error: null } m ? (m.Sources.Count == 0 ? "Nenhum metadado encontrado no arquivo" : "Fontes: " + string.Join(" · ", m.Sources)) : string.Empty;
    public string TitleText => Metadata?.Title ?? "—";
    public string DescriptionText => Metadata?.Description ?? "—";
    public string AuthorText => Metadata?.Author ?? "—";
    public string CopyrightText => Metadata?.Copyright ?? "—";
    public string CameraText => string.Join("  ·  ", new[] { Metadata?.Camera, Metadata?.Lens }.Where(v => !string.IsNullOrWhiteSpace(v))) is { Length: > 0 } text ? text : "—";
    public string ExposureText
    {
        get
        {
            var parts = new[] { Metadata?.Aperture, Metadata?.ExposureTime, Metadata?.Iso is { } iso ? $"ISO {iso}" : null, Metadata?.FocalLength }.Where(v => !string.IsNullOrWhiteSpace(v));
            return string.Join("  ·  ", parts) is { Length: > 0 } text ? text : "—";
        }
    }
    public string DateTakenText => Metadata?.DateTaken?.ToString("g", CultureInfo.CurrentCulture) ?? "—";

    // ----- Microstock (somente leitura)
    public bool HasMicrostock => _microstock is not null;
    public string MicrostockStatusText => _microstock is null ? string.Empty : StatusLabel(_microstock.Status);
    public MicrostockPreparationStatus? MicrostockStatus => _microstock?.Status;
    public string MicrostockNote { get => _microstockNote; private set { _microstockNote = value; OnPropertyChanged(); } }

    /// <summary>Chamado pela Biblioteca ao entrar/sair do modo de revisão.</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (active) { OnImageChanged(); _ = LoadDetailsAsync(); ScheduleFullImage(); }
        else { _fullImage = null; _fullVersion++; _detailsVersion++; OnPropertyChanged(nameof(DisplayImage)); OnPropertyChanged(nameof(IsFullResolution)); NotifyVideo(); }
    }

    private void NotifyVideo() { OnPropertyChanged(nameof(IsVideo)); OnPropertyChanged(nameof(VideoPath)); OnPropertyChanged(nameof(VideoRotation)); }

    /// <summary>O usuário girou o item: recarrega a imagem grande e a rotação do player.</summary>
    public void RefreshAfterRotation()
    {
        _fullImage = null;
        _fullVersion++;
        NotifyVideo();
        OnPropertyChanged(nameof(DisplayImage)); OnPropertyChanged(nameof(IsFullResolution));
        if (_isActive) ScheduleFullImage();
    }

    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LibraryViewModel.PreviewImage): OnPropertyChanged(nameof(DisplayImage)); break;
            case nameof(LibraryViewModel.SelectedPhoto):
                _fullImage = null; _fullVersion++;
                OnPropertyChanged(nameof(HasPhoto)); OnPropertyChanged(nameof(Rating)); OnPropertyChanged(nameof(DisplayImage)); OnPropertyChanged(nameof(IsFullResolution));
                NotifyVideo();
                OpenMetadataCommand.RaiseCanExecuteChanged(); OpenMicrostockCommand.RaiseCanExecuteChanged();
                if (_isActive) { _ = LoadDetailsAsync(); ScheduleFullImage(); }
                break;
        }
    }

    private void OnImageChanged() { NotifyVideo(); OnPropertyChanged(nameof(DisplayImage)); OnPropertyChanged(nameof(IsFullResolution)); OnPropertyChanged(nameof(HasPhoto)); OnPropertyChanged(nameof(Rating)); }

    /// <summary>A resolução natural só é decodificada se o usuário parar na foto (navegar com as setas não dispara decodificações pesadas).</summary>
    private async void ScheduleFullImage()
    {
        var version = ++_fullVersion;
        if (Library.SelectedPhoto is not { IsMissing: false } card) return;
        if (card.Photo.IsVideo) return;   // vídeo: o reprodutor cuida; não decodifica quadro em resolução cheia
        try
        {
            await Task.Delay(FullImageDelayMs);
            if (version != _fullVersion) return;
            var path = card.Photo.CurrentPath;
            var rotation = card.Photo.UserRotation;
            var image = await Task.Run(() => ImageLoader.LoadFull(path) is { } full ? ExifOrientation.Rotate(full, rotation) : null);
            if (version != _fullVersion || image is null) return;
            _fullImage = image;
            OnPropertyChanged(nameof(DisplayImage)); OnPropertyChanged(nameof(IsFullResolution));
        }
        catch (Exception) { /* a imagem reduzida continua visível */ }
    }

    private async Task LoadDetailsAsync()
    {
        var version = ++_detailsVersion;
        var card = Library.SelectedPhoto;
        Metadata = null; _microstock = null;
        Keywords.Clear(); MicrostockIssues.Clear(); UploadStatuses.Clear(); MetadataHistory.Clear(); UploadHistory.Clear();
        MicrostockNote = string.Empty;
        NotifyMicrostock();
        if (card is null) return;
        if (card.IsMissing) { Metadata = new PhotoMetadata { Error = "Arquivo ausente: não há metadados para ler." }; return; }
        IsLoadingDetails = true;
        try
        {
            var metadata = await _reader.ReadAsync(card.Photo.CurrentPath);
            if (version != _detailsVersion) return;
            foreach (var keyword in metadata.Keywords) Keywords.Add(keyword);
            Metadata = metadata;

            if (_editor is not null)
                foreach (var entry in await _editor.GetHistoryAsync(card.Photo.Id))
                    MetadataHistory.Add(new ReviewLine($"v{entry.Version} · {entry.ChangedAt.ToLocalTime():g}", entry.Note ?? string.Empty));
            if (_uploads is not null)
                foreach (var record in await _uploads.GetHistoryAsync(card.Photo.Id))
                    UploadHistory.Add(new ReviewLine($"{record.AgencyName} · {UploadLabel(record.Status)} · {record.UploadedAt.ToLocalTime():dd/MM/yyyy HH:mm}",
                        string.Join(" · ", new[] { record.RemoteFileName, record.LastError, record.Notes, $"metadados v{record.MetadataVersion}" }.Where(v => !string.IsNullOrWhiteSpace(v)))));
            if (version != _detailsVersion) return;
            await LoadMicrostockAsync(card, metadata, version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _detailsVersion) Metadata = new PhotoMetadata { Error = $"Não foi possível ler os metadados: {ex.Message}" };
        }
        finally { if (version == _detailsVersion) IsLoadingDetails = false; }
    }

    private async Task LoadMicrostockAsync(PhotoCardViewModel card, PhotoMetadata metadata, int version)
    {
        if (_profiles is null || _evaluation is null) { MicrostockNote = "O workflow de microstock não está disponível."; return; }
        var profile = (await _profiles.GetAllAsync()).FirstOrDefault(p => p.IsActive);
        if (profile is null) { MicrostockNote = "Nenhum perfil de validação ativo."; return; }
        IReadOnlyCollection<UploadRecordSnapshot> snapshots = _uploads is null ? [] : await _uploads.GetCurrentSnapshotsAsync([card.Photo.Id]);
        IReadOnlyCollection<long>? activeAgencies = _uploads is null ? null : (await _uploads.GetAgenciesAsync(true)).Select(a => a.Id).ToList();
        var result = (await _evaluation.EvaluateAsync([card.Photo], profile, snapshots, activeAgencies)).FirstOrDefault();
        if (version != _detailsVersion || result is null) return;
        _microstock = result;
        MicrostockNote = $"Perfil: {profile.Name}";
        foreach (var issue in result.Validation.Issues) MicrostockIssues.Add(new ReviewLine(issue.Message, string.Empty));
        foreach (var upload in snapshots.OrderBy(u => u.AgencyName))
            UploadStatuses.Add(new ReviewLine(upload.AgencyName, upload.Status switch
            {
                UploadStatusSnapshot.Uploaded => $"✓ Enviado em {upload.UploadedAt.ToLocalTime():dd/MM/yyyy}",
                UploadStatusSnapshot.Rejected => "✗ Rejeitado",
                UploadStatusSnapshot.Error => "✗ Erro",
                _ => "— Pendente"
            }));
        NotifyMicrostock();
    }

    private void Open(string key)
    {
        if (_navigation is null) return;
        Library.ExitReview();
        _navigation.Navigate(key);
    }

    private static string StatusLabel(MicrostockPreparationStatus status) => status switch
    {
        MicrostockPreparationStatus.NotPrepared => "Não preparada",
        MicrostockPreparationStatus.MetadataIncomplete => "Metadata incompleto",
        MicrostockPreparationStatus.ReadyForSubmission => "Pronta para envio",
        MicrostockPreparationStatus.PartiallyUploaded => "Enviada parcialmente",
        MicrostockPreparationStatus.UploadedToAll => "Enviada para todos",
        MicrostockPreparationStatus.Error => "Com erro",
        MicrostockPreparationStatus.ChangedAfterUpload => "Alterada após envio",
        _ => status.ToString()
    };

    private static string UploadLabel(UploadRecordStatus status) => status switch
    {
        UploadRecordStatus.Uploaded => "Enviado", UploadRecordStatus.Rejected => "Rejeitado", UploadRecordStatus.Error => "Erro", _ => "Pendente"
    };

    private void NotifyMetadata()
    {
        foreach (var name in new[] { nameof(Metadata), nameof(HasMetadata), nameof(MetadataError), nameof(SourcesText), nameof(TitleText), nameof(DescriptionText), nameof(AuthorText),
                     nameof(CopyrightText), nameof(CameraText), nameof(ExposureText), nameof(DateTakenText) })
            OnPropertyChanged(name);
    }

    private void NotifyMicrostock()
    {
        foreach (var name in new[] { nameof(HasMicrostock), nameof(MicrostockStatusText), nameof(MicrostockStatus) }) OnPropertyChanged(name);
    }
}
