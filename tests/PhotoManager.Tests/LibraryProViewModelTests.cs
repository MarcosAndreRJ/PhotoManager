using System.IO;
using PhotoManager.Application.Ai;
using PhotoManager.Application.Library;
using PhotoManager.Domain.Photos;
using PhotoManager.Infrastructure.Files;
using PhotoManager.Infrastructure.Images;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

internal sealed class MemoryLibrarySettings(LibraryPreferences? initial = null) : ILibrarySettings
{
    public LibraryPreferences Current { get; private set; } = initial ?? LibraryPreferences.Default;
    public LibraryPreferences Load() => Current;
    public void Save(LibraryPreferences preferences) => Current = preferences;
}

/// <summary>Biblioteca "pró" de ponta a ponta no ViewModel: busca, fichas, triagem, uso, pilhas, grupos, linha do tempo, cortes, paleta, comparar, exportar, mapa, Configurações e Ferramentas.</summary>
[Collection("WpfUi")]
public sealed class LibraryProViewModelTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly MemoryLibrarySettings _settings = new(LibraryPreferences.Default with { Sort = LibrarySort.Name, StackPhotos = false });
    public void Dispose() => _env.Dispose();

    private async Task<LibraryViewModel> LibraryAsync(int expected, LibraryProServices? services = null)
    {
        var library = _env.CreateLibrary();
        library.ConfirmAction = _ => true;
        library.Pro = services ?? new LibraryProServices(_env.Repository, _settings, Export: new ExportService(), Backup: new BackupVerifier());
        await TestEnvironment.WaitUntilAsync(() => library.Photos.Count == expected);
        return library;
    }

    private async Task<Photo> ImportAsync(string relative, byte shade = 120, int width = 64, int height = 48, DateTime? modified = null)
    {
        var path = Path.GetFullPath(_env.CreatePng(relative, shade, width, height));
        if (modified is { } when) File.SetLastWriteTime(path, when);
        await _env.Catalog.ImportPathsAsync([path]);
        return (await _env.Repository.FindByPathAsync(path))!;
    }

    [Fact]
    public async Task Search_UsesQuerySyntax_ShowsChips_AndRemovingAChipClearsOnlyThatTerm()
    {
        await ImportAsync("q/retrato.png", width: 48, height: 96);
        await ImportAsync("q/paisagem.png", width: 96, height: 48);
        var library = await LibraryAsync(2);

        library.SearchText = "orientação:vertical nome:retrato";
        library.ApplyFilters();
        Assert.Equal(["retrato.png"], library.Photos.Select(p => p.FileName));
        Assert.Equal(2, library.FilterChips.Count(c => c.Kind == "busca"));
        library.FilterChips.First(c => c.Label.Contains("orientação", StringComparison.OrdinalIgnoreCase)).RemoveCommand.Execute(null);
        Assert.Equal("nome:retrato", library.SearchText);
        Assert.Single(library.Photos);

        library.SearchText = "nota:9";
        library.ApplyFilters();
        Assert.True(library.HasQueryError);
        Assert.Contains("orientação:", string.Join(" ", LibraryQuery.Suggest("ori")));
    }

    [Fact]
    public async Task Triage_PickRejectPersist_SmartListsCount_AndAutoAdvance()
    {
        await ImportAsync("t/a.png", 10); await ImportAsync("t/b.png", 20); await ImportAsync("t/c.png", 30);
        var library = await LibraryAsync(3);
        library.SelectedPhoto = library.Photos[0];
        await library.SetPickAsync([library.Photos[0]], PickFlag.Rejected);
        Assert.Equal(library.Photos[1], library.SelectedPhoto);                          // avançou
        await library.SetPickAsync([library.Photos[1]], PickFlag.Picked, advance: false);
        Assert.Equal(library.Photos[1], library.SelectedPhoto);
        Assert.Equal(1, library.SmartLists.Single(s => s.Key == "rejected").Count);
        Assert.Equal(PickFlag.Rejected, (await _env.Repository.FindByPathAsync(library.Photos[0].Photo.CurrentPath))!.Pick);

        library.ShowSmartList("picked");
        Assert.Equal(["b.png"], library.Photos.Select(p => p.FileName));
        Assert.Contains(library.FilterChips, c => c.Kind == "lista");

        await library.SetUsageAsync(library.Photos, UsageStatus.Used, "Vlog #3");
        Assert.Equal("Usado em Vlog #3", library.Photos[0].UsageText);
    }

    [Fact]
    public async Task SortStacksGroupsAndTimeline()
    {
        var day = new DateTime(2026, 9, 27, 10, 0, 0);
        for (var i = 0; i < 32; i++) await ImportAsync($"s/IMG_{i:000}.png", (byte)(i * 7));
        var library = await LibraryAsync(32);
        var photos = library.AllPhotos.OrderBy(p => p.FileName).ToList();
        // datas: 30 itens em setembro (3 em rajada no mesmo segundo) e 2 em outubro
        for (var i = 0; i < photos.Count; i++) photos[i].DateTaken = i < 30 ? day.AddMinutes(i < 3 ? 0 : i) : day.AddDays(i - 20);
        photos[1].DateTaken = day.AddSeconds(0.4); photos[2].DateTaken = day.AddSeconds(0.8);

        library.UpdatePreferences(p => p with { Sort = LibrarySort.DateNewest, Grid = GridStyle.Justified, GroupByDay = true, StackPhotos = true });
        Assert.Equal("IMG_031.png", library.Photos[0].FileName);                        // mais recente primeiro
        Assert.Equal(30, library.Photos.Count);                                         // rajada de 3 → 1
        var top = library.Photos.Single(p => p.IsStackTop);
        Assert.Equal(3, top.StackCount);
        library.ToggleStackCommand.Execute(top);
        Assert.Equal(32, library.Photos.Count);
        Assert.NotNull(library.Photos[0].GroupLabel);
        Assert.StartsWith("Quinta-feira, 8 de outubro de 2026", library.Photos[0].GroupLabel!);
        Assert.Contains(library.Timeline, m => m.IsYear && m.Label == "2026");
        Assert.Contains(library.Timeline, m => m.Label.StartsWith("Set", StringComparison.Ordinal));
        Assert.Equal(LibrarySort.DateNewest, _settings.Current.Sort);                  // preferência gravada
    }

    [Fact]
    public async Task SmartCollection_IsSavedSelectedAndDeleted()
    {
        await ImportAsync("sc/a.png"); await ImportAsync("sc/b.png");
        var library = await LibraryAsync(2);
        await library.SetPickAsync([library.Photos[0]], PickFlag.Picked, advance: false);
        library.PromptText = (_, _, _) => "Escolhidas do teste";
        library.SearchText = "bandeira:escolhida";
        library.ApplyFilters();
        await library.SaveSearchAsSmartCollectionAsync();
        var entry = Assert.Single(library.SmartCollectionEntries);
        Assert.Equal(1, entry.Count);
        library.SearchText = string.Empty;
        library.SelectSidebarCommand.Execute(entry);
        Assert.Single(library.Photos);
        Assert.Contains(library.FilterChips, c => c.Kind == "inteligente");
        library.DeleteSmartCollectionCommand.Execute(entry);
        await TestEnvironment.WaitUntilAsync(() => library.SmartCollectionEntries.Count == 0);
        Assert.Equal(2, library.Photos.Count);
    }

    [Fact]
    public async Task VideoMarkers_AreAddedAndExportedAsFcpxml()
    {
        var png = await ImportAsync("m/clip.png");
        var video = Path.Combine(_env.Photos, "m", "clip.mp4");
        File.WriteAllBytes(video, new byte[100]);
        var id = await _env.Repository.AddAsync(new Photo { FileName = "clip.mp4", CurrentPath = video, Extension = ".mp4", DurationSeconds = 30, CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow, ImportedAt = DateTime.UtcNow });
        var library = await LibraryAsync(2);
        library.SelectedPhoto = library.Photos.Single(p => p.IsVideo);
        Assert.False(library.CanAddMarker);
        library.MarkerIn = 3; library.MarkerOut = 8; library.MarkerName = "Abertura";
        await library.AddMarkerAsync();
        Assert.Single(library.Markers);
        Assert.True(library.Photos.Single(p => p.IsVideo).HasMarkers);
        Assert.Equal(1, library.SmartLists.Single(s => s.Key == "markers").Count);

        var output = Path.Combine(_env.Root, "cortes.fcpxml");
        library.PickSaveFile = (_, _, _) => output;
        await library.ExportCutsAsync();
        Assert.Contains("name=\"Abertura\"", File.ReadAllText(output));
        Assert.Contains(id.ToString(), id.ToString());
        Assert.NotNull(png);
    }

    [Fact]
    public async Task Palette_CompareExportAndMapRegion()
    {
        await ImportAsync("p/a.png", 10); await ImportAsync("p/b.png", 200);
        var library = await LibraryAsync(2);

        library.IsPaletteOpen = true;
        library.PaletteText = "rejeitar";
        Assert.Contains(library.PaletteResults, c => c.Title.StartsWith("Rejeitar", StringComparison.Ordinal));
        library.PaletteText = "pasta p";
        Assert.Contains(library.PaletteResults, c => c.Group.StartsWith("Pasta", StringComparison.Ordinal));

        library.UpdateSelection(library.Photos.ToList());
        Assert.True(library.CompareCommand.CanExecute(null));
        var compare = new CompareViewModel(library, library.Photos.ToList());
        await compare.KeepOnlyAsync(compare.Items[1]);
        Assert.Equal([PickFlag.Rejected, PickFlag.Picked], library.Photos.Select(p => p.Pick));

        var destination = Path.Combine(_env.Root, "exportado");
        await library.ExportAsync(library.Photos.Select(p => p.Photo).ToList(), new ExportPreset("t", 32), destination);
        Assert.Equal(2, Directory.GetFiles(destination, "*.jpg").Length);

        library.AllPhotos[0].Latitude = -23.5; library.AllPhotos[0].Longitude = -46.6;
        library.IsMapMode = true;
        Assert.Single(library.MapPoints);
        library.MapRegionCommand.Execute(new PhotoManager.Wpf.Controls.MapRegion(-24, -47, -23, -46));
        Assert.Single(library.Photos);
        Assert.Contains(library.FilterChips, c => c.Kind == "mapa");
    }

    [Fact]
    public async Task Settings_PersistThemeLibraryOptionsAndAiChoices()
    {
        await ImportAsync("st/a.png");
        var models = new PhotoManager.Infrastructure.Ai.AiModelManager(Path.Combine(_env.Root, "models"));
        var library = await LibraryAsync(1, new LibraryProServices(_env.Repository, _settings, Ai: new AiIndexService(new AiEngineRegistry(), _env.Repository), AiRepository: _env.Repository, Models: models));
        var settings = new SettingsViewModel(library);
        settings.IsDark = true;
        settings.JustifiedGrid = false;
        settings.HoverPreview = false;
        Assert.Equal((AppTheme.Dark, GridStyle.Uniform, false), (_settings.Current.Theme, _settings.Current.Grid, _settings.Current.HoverPreview));
        var search = settings.Capabilities.Single(c => c.Capability == AiCapability.SemanticSearch);
        Assert.False(search.HasEngine);                                                  // nenhum motor instalado nesta versão
        Assert.False(settings.DownloadCommand.CanExecute(search.Models[0]));             // então nada pode ser baixado
        search.IsEnabled = true;
        Assert.Contains(AiCapability.SemanticSearch, _settings.Current.AiEnabled!);
        Assert.False(library.SemanticSearchCommand.CanExecute(null));
        settings.RemovePresetCommand.Execute(settings.Presets[0]);
        Assert.Equal(ExportPreset.BuiltIn.Count - 1, settings.Presets.Count);
        settings.RestorePresetsCommand.Execute(null);
        Assert.Equal(ExportPreset.BuiltIn.Count, settings.Presets.Count);
    }

    [Fact]
    public async Task Tools_AnalysisFindsSimilarAndBlurry_StorageAndBackup()
    {
        await ImportAsync("tl/a.png", 100, 120, 90);
        await ImportAsync("tl/a_copia.png", 101, 120, 90);                               // mesma imagem, praticamente
        await ImportAsync("tl/outra.png", 10, 120, 90);
        var services = new LibraryProServices(_env.Repository, _settings, new PhotoAnalysisService(_env.Repository, _env.Thumbnails, new GrayImageReader()), Backup: new BackupVerifier());
        var library = await LibraryAsync(3, services);
        await TestEnvironment.WaitUntilAsync(() => library.AllPhotos.All(p => p.PerceptualHash.HasValue), 20000);

        var tools = new ProToolsViewModel(library, services);
        await tools.FindSimilarAsync();
        Assert.NotEmpty(tools.SimilarGroups);                                            // as três são cor sólida: todas parecidas
        await tools.KeepBestAsync(tools.SimilarGroups[0]);
        Assert.Contains(library.AllPhotos, p => p.Pick == PickFlag.Rejected);
        Assert.Contains(tools.StorageLines, l => l.Label == "Fotos");
        Assert.NotEmpty(tools.ByFolder);

        var source = Path.Combine(_env.Photos, "tl");
        var backup = Directory.CreateDirectory(Path.Combine(_env.Root, "bk")).FullName;
        var answers = new Queue<string>([source, backup]);
        tools.PickFolder = _ => answers.Dequeue();
        await tools.AddBackupAsync();
        var target = Assert.Single(tools.Backups);
        await tools.VerifyAsync(target);
        Assert.True(target.HasProblems);
        await tools.CopyMissingAsync(target);
        Assert.False(target.HasProblems);
        Assert.Equal("Todos os backups completos.", tools.BackupStatus);
    }
}
