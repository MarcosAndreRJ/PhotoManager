using System.Collections.ObjectModel;
using PhotoManager.Application.Ai;
using PhotoManager.Application.Library;
using PhotoManager.Wpf.Commands;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

/// <summary>Um modelo de IA na tela de Configurações: tamanho, se está instalado e o download (só quando o usuário pede).</summary>
public sealed class AiModelViewModel(AiModelInfo model, IAiModelManager? manager) : ViewModelBase
{
    private double _progress;
    private bool _isDownloading;
    private string _error = string.Empty;

    public AiModelInfo Model { get; } = model;
    public string Name => Model.Name;
    public string Description => Model.Description;
    public string SizeText => "≈ " + LibraryStats.FormatBytes(Model.ApproxBytes);
    public bool IsInstalled => manager?.IsInstalled(Model) == true;
    public string StatusText => IsDownloading ? $"Baixando… {Progress:0}%" : IsInstalled ? "Instalado" : "Não baixado";
    public double Progress { get => _progress; set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); } }
    public bool IsDownloading { get => _isDownloading; set { _isDownloading = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); } }
    public string Error { get => _error; set { _error = value; OnPropertyChanged(); } }
    public void Refresh() { OnPropertyChanged(nameof(IsInstalled)); OnPropertyChanged(nameof(StatusText)); }
}

/// <summary>Um recurso de IA (busca por descrição, pessoas, etiquetas, transcrição): ligado/desligado, motor disponível e modelos.</summary>
public sealed class AiCapabilityViewModel : ViewModelBase
{
    private readonly SettingsViewModel _owner;

    public AiCapabilityViewModel(SettingsViewModel owner, AiCapability capability, AiEngineRegistry? engines, IAiModelManager? manager)
    {
        _owner = owner;
        Capability = capability;
        HasEngine = engines?.HasEngineFor(capability) == true;
        Models = AiModelCatalog.For(capability).Select(m => new AiModelViewModel(m, manager)).ToList();
    }

    public AiCapability Capability { get; }
    public string Title => AiModelCatalog.Describe(Capability);
    public IReadOnlyList<AiModelViewModel> Models { get; }
    /// <summary>O motor de inferência (ONNX Runtime / Whisper) faz parte da instalação; sem ele os modelos não têm uso e o download fica bloqueado.</summary>
    public bool HasEngine { get; }
    public string EngineText => HasEngine ? "Motor de IA instalado." : "Motor de IA ainda não instalado nesta versão: o recurso fica indisponível e nada é baixado.";
    public bool IsEnabled { get => _owner.IsAiEnabled(Capability); set { _owner.SetAiEnabled(Capability, value); OnPropertyChanged(); } }
    public string TotalSize => "≈ " + LibraryStats.FormatBytes(Models.Sum(m => m.Model.ApproxBytes));
}

/// <summary>Configurações: aparência (tema), Biblioteca, mapa, predefinições de exportação e IA local.</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly LibraryViewModel _library;
    private readonly IAiModelManager? _models;

    public SettingsViewModel(LibraryViewModel library)
    {
        _library = library;
        _models = library.Pro?.Models;
        Capabilities = Enum.GetValues<AiCapability>().Select(c => new AiCapabilityViewModel(this, c, library.Pro?.Ai?.Engines, _models)).ToList();
        Presets = new ObservableCollection<ExportPreset>(Preferences.Presets);
        RemovePresetCommand = new RelayCommand(p => { if (p is ExportPreset preset) RemovePreset(preset); });
        RestorePresetsCommand = new RelayCommand(_ => { _library.UpdatePreferences(x => x with { ExportPresets = null }); ResetPresets(); });
        DownloadCommand = new RelayCommand(p => { if (p is AiModelViewModel model) _ = DownloadAsync(model); }, p => p is AiModelViewModel { IsInstalled: false, IsDownloading: false } model && CanDownload(model));
        RemoveModelCommand = new RelayCommand(p => { if (p is AiModelViewModel { IsInstalled: true } model) { _models?.Remove(model.Model); model.Refresh(); } });
        OpenModelsFolderCommand = new RelayCommand(_ => { if (_models is not null) { Directory.CreateDirectory(_models.ModelsFolder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_models.ModelsFolder}\"") { UseShellExecute = true }); } }, _ => _models is not null);
    }

    private LibraryPreferences Preferences => _library.Preferences;
    public bool HasPro => _library.HasPro;

    // ---------- aparência ----------
    public bool IsLight { get => Preferences.Theme == AppTheme.Light; set { if (value) SetTheme(AppTheme.Light); } }
    public bool IsDark { get => Preferences.Theme == AppTheme.Dark; set { if (value) SetTheme(AppTheme.Dark); } }
    public bool IsSystem { get => Preferences.Theme == AppTheme.System; set { if (value) SetTheme(AppTheme.System); } }

    public void SetTheme(AppTheme theme)
    {
        _library.UpdatePreferences(p => p with { Theme = theme });
        ThemeService.Apply(theme);
        OnPropertyChanged(nameof(IsLight)); OnPropertyChanged(nameof(IsDark)); OnPropertyChanged(nameof(IsSystem));
    }

    // ---------- Biblioteca ----------
    public bool JustifiedGrid { get => Preferences.Grid == GridStyle.Justified; set => Update(p => p with { Grid = value ? GridStyle.Justified : GridStyle.Uniform }); }
    public bool ShowFileNames { get => Preferences.ShowFileNames; set => Update(p => p with { ShowFileNames = value }); }
    public bool HoverPreview { get => Preferences.HoverPreview; set => Update(p => p with { HoverPreview = value }); }
    public bool AutoAdvance { get => Preferences.AutoAdvance; set => Update(p => p with { AutoAdvance = value }); }
    public bool GroupByDay { get => Preferences.GroupByDay; set => Update(p => p with { GroupByDay = value }); }
    public bool StackPhotos { get => Preferences.StackPhotos; set => Update(p => p with { StackPhotos = value }); }
    public bool MapAllowed { get => Preferences.MapAllowed; set => Update(p => p with { MapAllowed = value }); }
    public IReadOnlyList<SortChoice> SortChoices => LibraryViewModel.SortChoices;
    public SortChoice DefaultSort { get => _library.SortOption; set { _library.SortOption = value; OnPropertyChanged(); } }

    private void Update(Func<LibraryPreferences, LibraryPreferences> change)
    {
        _library.UpdatePreferences(change);
        foreach (var name in new[] { nameof(JustifiedGrid), nameof(ShowFileNames), nameof(HoverPreview), nameof(AutoAdvance), nameof(GroupByDay), nameof(StackPhotos), nameof(MapAllowed) }) OnPropertyChanged(name);
    }

    // ---------- predefinições de exportação ----------
    public ObservableCollection<ExportPreset> Presets { get; }
    public RelayCommand RemovePresetCommand { get; }
    public RelayCommand RestorePresetsCommand { get; }

    private void RemovePreset(ExportPreset preset)
    {
        var remaining = Preferences.Presets.Where(p => p != preset).ToList();
        _library.UpdatePreferences(p => p with { ExportPresets = remaining.Count == 0 ? null : remaining });
        ResetPresets();
    }

    private void ResetPresets()
    {
        Presets.Clear();
        foreach (var preset in Preferences.Presets) Presets.Add(preset);
    }

    // ---------- IA local ----------
    public IReadOnlyList<AiCapabilityViewModel> Capabilities { get; }
    public RelayCommand DownloadCommand { get; }
    public RelayCommand RemoveModelCommand { get; }
    public RelayCommand OpenModelsFolderCommand { get; }
    public string ModelsFolder => _models?.ModelsFolder ?? "—";

    public bool IsAiEnabled(AiCapability capability) => Preferences.AiEnabled?.Contains(capability) == true;

    public void SetAiEnabled(AiCapability capability, bool enabled)
    {
        var current = (Preferences.AiEnabled ?? []).Where(c => c != capability).ToList();
        if (enabled) current.Add(capability);
        _library.UpdatePreferences(p => p with { AiEnabled = current });
    }

    private bool CanDownload(AiModelViewModel model) => _models is not null && Capabilities.Any(c => c.HasEngine && c.Models.Contains(model));

    private async Task DownloadAsync(AiModelViewModel model)
    {
        if (_models is null) return;
        if (!_library.ConfirmAction($"Baixar “{model.Name}” ({model.SizeText}) da internet?\n\nO arquivo fica em {_models.ModelsFolder} e é usado só neste computador.")) return;
        model.IsDownloading = true;
        model.Error = string.Empty;
        try
        {
            var progress = new Progress<ModelDownloadProgress>(p => model.Progress = p.Total is > 0 ? p.Received * 100.0 / p.Total.Value : 0);
            await _models.DownloadAsync(model.Model, progress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { model.Error = $"Falhou: {ex.Message}"; }
        finally { model.IsDownloading = false; model.Refresh(); DownloadCommand.RaiseCanExecuteChanged(); }
    }
}
