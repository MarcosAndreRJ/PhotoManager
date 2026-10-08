using PhotoManager.Application.Library;
using PhotoManager.Wpf.Commands;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

/// <summary>Diálogo de exportação: escolhe uma predefinição, ajusta (tamanho, formato, qualidade, marca d'água, nome) e o destino.</summary>
public sealed class ExportDialogViewModel : ViewModelBase
{
    private ExportPreset _preset;
    private string? _destination;
    private bool _saveAsPreset;

    public ExportDialogViewModel(IReadOnlyList<ExportPreset> presets, int itemCount, int videoCount)
    {
        Presets = presets;
        _preset = presets[0];
        ItemCount = itemCount;
        VideoCount = videoCount;
        ChooseFolderCommand = new RelayCommand(_ =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Exportar para…" };
            if (dialog.ShowDialog() == true) Destination = dialog.FolderName;
        });
    }

    public IReadOnlyList<ExportPreset> Presets { get; }
    public int ItemCount { get; }
    public int VideoCount { get; }
    public string Title => $"Exportar {ItemCount} item(ns)" + (VideoCount > 0 ? $" ({VideoCount} vídeo(s): copiados como estão)" : string.Empty);
    public RelayCommand ChooseFolderCommand { get; }

    public ExportPreset Preset
    {
        get => _preset;
        set { if (value is null) return; _preset = value; foreach (var name in new[] { nameof(Preset), nameof(PresetName), nameof(LongEdgeText), nameof(Format), nameof(Quality), nameof(WatermarkText), nameof(WatermarkPosition), nameof(NameTemplate), nameof(IncludeVideos), nameof(Summary) }) OnPropertyChanged(name); }
    }

    public string PresetName { get => _preset.Name; set => Edit(_preset with { Name = value ?? string.Empty }); }
    public string LongEdgeText { get => _preset.LongEdge?.ToString() ?? string.Empty; set => Edit(_preset with { LongEdge = int.TryParse(value, out var edge) && edge > 0 ? edge : null }); }
    public static IReadOnlyList<string> Formats { get; } = ["jpg", "png", "original"];
    public string Format { get => _preset.Format; set => Edit(_preset with { Format = value ?? "jpg" }); }
    public int Quality { get => _preset.Quality; set => Edit(_preset with { Quality = Math.Clamp(value, 10, 100) }); }
    public string WatermarkText { get => _preset.WatermarkText ?? string.Empty; set => Edit(_preset with { WatermarkText = string.IsNullOrWhiteSpace(value) ? null : value }); }
    public static IReadOnlyList<WatermarkPosition> Positions { get; } = Enum.GetValues<WatermarkPosition>();
    public WatermarkPosition WatermarkPosition { get => _preset.WatermarkPosition; set => Edit(_preset with { WatermarkPosition = value }); }
    public string NameTemplate { get => _preset.NameTemplate; set => Edit(_preset with { NameTemplate = string.IsNullOrWhiteSpace(value) ? "{nome}" : value }); }
    public bool IncludeVideos { get => _preset.IncludeVideos; set => Edit(_preset with { IncludeVideos = value }); }
    public string Summary => _preset.Summary;
    public string NameHelp => PhotoManager.Application.Transfer.BatchRenamePlanner.Help;

    public string? Destination { get => _destination; set { _destination = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanExport)); } }
    public bool SaveAsPreset { get => _saveAsPreset; set { _saveAsPreset = value; OnPropertyChanged(); } }
    public bool CanExport => !string.IsNullOrWhiteSpace(Destination);

    private void Edit(ExportPreset preset)
    {
        _preset = preset;
        OnPropertyChanged(nameof(Summary));
    }
}
