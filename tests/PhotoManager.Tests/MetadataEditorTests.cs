using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PhotoManager.Application.Metadata;
using PhotoManager.Application.Navigation;
using PhotoManager.Infrastructure.Metadata;
using PhotoManager.Persistence;
using PhotoManager.Wpf;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

[Collection("WpfUi")]
public sealed class MetadataEditorTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private string CreateJpeg(string name, string? camera = null)
    {
        var path = Path.Combine(_env.Photos, name);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(24, 24, 96, 96, PixelFormats.Bgra32, null, new byte[24 * 24 * 4], 24 * 4), null, camera is null ? new BitmapMetadata("jpg") : new BitmapMetadata("jpg") { CameraModel = camera }, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private MetadataEditorViewModel Editor(LibraryViewModel library) => new(library, new MetadataExtractorReader(), _env.MetadataEditing, _env.Repository);

    /// <summary>Biblioteca com JPEGs p1..pN (+ x.png opcional), todos marcados, e o editor já ativo e carregado.</summary>
    private async Task<(LibraryViewModel Library, MetadataEditorViewModel Editor)> OpenAsync(int jpegs = 3, bool png = false)
    {
        for (var i = 1; i <= jpegs; i++) CreateJpeg($"p{i}.jpg", "Cam" + i);
        if (png) _env.CreatePng("x.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        library.UpdateSelection(library.Photos.ToList());
        library.SelectedPhoto = library.Photos[0];
        var editor = Editor(library);
        editor.SetActive(true);
        await editor.EnsureLoadedAsync(editor.Rows);
        return (library, editor);
    }

    private static MetadataRowViewModel Row(MetadataEditorViewModel editor, string name) => editor.Rows.Single(r => r.FileName == name);

    // ---------- alvos e leitura

    [Fact]
    public async Task Rows_AreTheMarkedPhotos_OrTheSelectedOne()
    {
        var (library, editor) = await OpenAsync(3);
        Assert.Equal(["p1.jpg", "p2.jpg", "p3.jpg"], editor.Rows.Select(r => r.FileName));

        library.UpdateSelection([]);                       // sem marcadas: vale a foto selecionada
        library.SelectedPhoto = library.Photos[1];
        editor.RefreshTargets();
        Assert.Equal(["p2.jpg"], editor.Rows.Select(r => r.FileName));
    }

    [Fact]
    public async Task Rows_AreReadLazily_AndStartWithoutChanges()
    {
        for (var i = 1; i <= 2; i++) CreateJpeg($"p{i}.jpg", "Cam" + i);
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        library.UpdateSelection(library.Photos.ToList());
        var editor = Editor(library);
        editor.SetActive(true);
        Assert.All(editor.Rows, r => { Assert.False(r.IsLoaded); Assert.False(r.IsChanged); Assert.Equal("Lendo metadados…", r.StatusText); });

        await editor.EnsureLoadedAsync(editor.Rows[0]);
        Assert.True(editor.Rows[0].IsLoaded); Assert.False(editor.Rows[1].IsLoaded);
        Assert.False(editor.Rows[0].IsChanged); Assert.Equal("Sem alterações", editor.Rows[0].StatusText);
        Assert.Contains("Cam1", editor.Rows[0].Metadata!.Camera);
    }

    [Fact]
    public async Task Png_IsEditableViaSidecar_AndNeverShownAsReadOnly()
    {
        var (library, editor) = await OpenAsync(1, png: true);
        var png = Row(editor, "x.png");
        Assert.True(png.IsEditable); Assert.True(png.CanEdit); Assert.DoesNotContain("Somente leitura", png.StatusText);
        Assert.Equal(0, editor.ReadOnlyCount);
        png.TitleText = "x";
        Assert.True(png.IsChanged);                         // vira rascunho gravável (vai para o sidecar)
    }

    // ---------- edição por linha

    [Fact]
    public async Task EditingARow_MarksFieldsChanged_CountsAndRevert()
    {
        var (_, editor) = await OpenAsync(2);
        var row = Row(editor, "p1.jpg");
        row.TitleText = "Pôr do sol"; row.NewKeyword = "praia, sol; mar"; row.AddKeywordCommand.Execute(null);
        Assert.True(row.IsChanged); Assert.Equal(["Título", "Palavras-chave"], row.ChangedFields);
        Assert.True(row.TitleChanged); Assert.True(row.KeywordsChanged); Assert.False(row.AuthorChanged);
        Assert.Equal("10/200", row.TitleCounter); Assert.Equal("3/50", row.KeywordCounter);
        Assert.Equal(1, editor.ChangedCount); Assert.Equal("Salvar 1 alterada(s)", editor.SaveButtonText);
        Assert.StartsWith("Alterada:", row.StatusText);

        row.RevertCommand.Execute(null);
        Assert.False(row.IsChanged); Assert.Equal(string.Empty, row.TitleText); Assert.Empty(row.Keywords);
        Assert.Equal(0, editor.ChangedCount);
    }

    [Fact]
    public async Task OverLimitCounters_AreFlagged_ButStillSavable()
    {
        var (_, editor) = await OpenAsync(1);
        var row = editor.Rows[0];
        row.TitleText = new string('a', 201);
        Assert.True(row.TitleOver); Assert.True(editor.CanSaveAll);
    }

    // ---------- salvar

    [Fact]
    public async Task SaveAll_WritesOnlyChangedRows_BumpsVersions_AndReloadsFromTheFile()
    {
        var (_, editor) = await OpenAsync(3);
        Row(editor, "p1.jpg").TitleText = "Um"; Row(editor, "p3.jpg").AuthorText = "Ana";
        var untouched = File.ReadAllBytes(Row(editor, "p2.jpg").Photo.CurrentPath);

        await editor.SaveAllAsync();

        Assert.Equal("Um", MetadataExtractorReader.Read(Row(editor, "p1.jpg").Photo.CurrentPath).Title);
        Assert.Equal("Ana", MetadataExtractorReader.Read(Row(editor, "p3.jpg").Photo.CurrentPath).Author);
        Assert.Equal(untouched, File.ReadAllBytes(Row(editor, "p2.jpg").Photo.CurrentPath));     // p2 não foi tocada
        Assert.Equal([1, 0, 1], editor.Rows.Select(r => r.Photo.MetadataVersion));
        Assert.All(editor.Rows, r => Assert.False(r.IsChanged));
        Assert.Contains("v1", Row(editor, "p1.jpg").StatusText);
        Assert.Contains("2 foto(s) salva(s)", editor.StatusText); Assert.False(editor.StatusIsError);
        Assert.False(editor.IsBusy); Assert.Equal("Salvar", editor.SaveButtonText);
        Assert.Equal("Cam1", Row(editor, "p1.jpg").Metadata!.Camera);                           // EXIF preservado
    }

    [Fact]
    public async Task OneFailure_DoesNotStopTheOthers_AndStaysAsDraft()
    {
        var (_, editor) = await OpenAsync(3);
        foreach (var row in editor.Rows) row.TitleText = "T";
        File.WriteAllText(Row(editor, "p2.jpg").Photo.CurrentPath, "corrompido");                // vira inválido depois de lido

        await editor.SaveAllAsync();

        var bad = Row(editor, "p2.jpg");
        Assert.True(bad.SaveFailed); Assert.True(bad.IsChanged); Assert.Contains("não foi alterado", bad.StatusText);
        Assert.Equal("corrompido", File.ReadAllText(bad.Photo.CurrentPath));
        Assert.Equal("T", MetadataExtractorReader.Read(Row(editor, "p1.jpg").Photo.CurrentPath).Title);
        Assert.Equal("T", MetadataExtractorReader.Read(Row(editor, "p3.jpg").Photo.CurrentPath).Title);
        Assert.True(editor.StatusIsError); Assert.Contains("1 com erro", editor.StatusText);
        Assert.Equal(1, editor.ChangedCount);                                                    // só a falha continua pendente
    }

    [Fact]
    public async Task SaveRow_WritesOnlyThatPhoto()
    {
        var (_, editor) = await OpenAsync(2);
        foreach (var row in editor.Rows) row.TitleText = "T";
        Assert.True(editor.SaveRowCommand.CanExecute(Row(editor, "p1.jpg")));
        await editor.SaveRowsAsync([Row(editor, "p1.jpg")]);
        Assert.Equal("T", MetadataExtractorReader.Read(Row(editor, "p1.jpg").Photo.CurrentPath).Title);
        Assert.Null(MetadataExtractorReader.Read(Row(editor, "p2.jpg").Photo.CurrentPath).Title);
        Assert.True(Row(editor, "p2.jpg").IsChanged);
    }

    // ---------- operações globais (sobre rascunhos)

    [Fact]
    public async Task GlobalOperations_ChangeDraftsOnly_AndKeepEachPhotosOwnValues()
    {
        var (_, editor) = await OpenAsync(3);
        Row(editor, "p1.jpg").TitleText = "Foto 1"; Row(editor, "p2.jpg").TitleText = "Foto 2";
        Row(editor, "p1.jpg").NewKeyword = "rio, sol"; Row(editor, "p1.jpg").AddKeywordCommand.Execute(null);
        await editor.SaveAllAsync();
        var before = editor.Rows.Select(r => File.ReadAllBytes(r.Photo.CurrentPath)).ToList();

        editor.AuthorOperation = BatchOperation.Replace; editor.GlobalAuthor = "Marcos André";
        editor.CopyrightOperation = BatchOperation.Replace; editor.GlobalCopyright = "© Marcos";
        editor.KeywordsOperation = BatchOperation.Add; editor.NewKeyword = "Brazil, travel"; editor.AddKeywordCommand.Execute(null);
        Assert.True(editor.CanApplyGlobal); Assert.Contains("3 foto(s) marcada(s)", editor.GlobalImpactText);
        await editor.ApplyGlobalAsync();

        Assert.Equal(["Foto 1", "Foto 2", string.Empty], editor.Rows.Select(r => r.TitleText));          // título mantido por foto
        Assert.Equal(["rio", "sol", "Brazil", "travel"], Row(editor, "p1.jpg").Keywords);
        Assert.Equal(["Brazil", "travel"], Row(editor, "p3.jpg").Keywords);
        Assert.All(editor.Rows, r => { Assert.Equal("Marcos André", r.AuthorText); Assert.True(r.IsChanged); Assert.True(r.AuthorChanged); });
        Assert.Equal(before, editor.Rows.Select(r => File.ReadAllBytes(r.Photo.CurrentPath)).ToList());   // nada gravado ainda
        Assert.Equal(3, editor.ChangedCount);

        await editor.SaveAllAsync();
        Assert.All(editor.Rows, r => Assert.Equal("Marcos André", MetadataExtractorReader.Read(r.Photo.CurrentPath).Author));
    }

    [Fact]
    public async Task OnlyMarkedPhotosReceiveGlobalOperations()
    {
        var (_, editor) = await OpenAsync(3);
        Row(editor, "p2.jpg").IsIncluded = false;
        Assert.Equal(2, editor.IncludedCount);
        editor.TitleOperation = BatchOperation.Replace; editor.GlobalTitle = "Marcadas";
        await editor.ApplyGlobalAsync();
        Assert.Equal(["Marcadas", string.Empty, "Marcadas"], editor.Rows.Select(r => r.TitleText));

        editor.IncludeNoneCommand.Execute(null);
        Assert.Equal(0, editor.IncludedCount); Assert.False(editor.CanApplyGlobal);
        editor.IncludeAllCommand.Execute(null);
        Assert.Equal(3, editor.IncludedCount);
    }

    [Fact]
    public async Task KeywordOperations_ReplaceRemoveClear_DescriptionAppend_WithoutDuplicates()
    {
        var (_, editor) = await OpenAsync(2);
        foreach (var row in editor.Rows) { row.NewKeyword = "a, b, c"; row.AddKeywordCommand.Execute(null); row.DescriptionText = "Texto"; }
        await editor.SaveAllAsync();

        editor.KeywordsOperation = BatchOperation.Remove; editor.NewKeyword = "B, zzz"; editor.AddKeywordCommand.Execute(null);
        await editor.ApplyGlobalAsync();
        Assert.All(editor.Rows, r => Assert.Equal(["a", "c"], r.Keywords));

        editor.Keywords.Clear(); editor.KeywordsOperation = BatchOperation.Add; editor.NewKeyword = "c, d, D"; editor.AddKeywordCommand.Execute(null);
        await editor.ApplyGlobalAsync();
        Assert.All(editor.Rows, r => Assert.Equal(["a", "c", "d"], r.Keywords));

        editor.KeywordsOperation = BatchOperation.Keep; editor.DescriptionOperation = BatchOperation.Append; editor.GlobalDescription = "extra";
        await editor.ApplyGlobalAsync();
        Assert.All(editor.Rows, r => Assert.Equal("Texto extra", r.DescriptionText));

        editor.DescriptionOperation = BatchOperation.Keep; editor.KeywordsOperation = BatchOperation.Clear;
        await editor.ApplyGlobalAsync();
        Assert.All(editor.Rows, r => Assert.Empty(r.Keywords));
    }

    [Fact]
    public async Task InvalidForm_CannotBeApplied_AndClearResetsIt()
    {
        var (_, editor) = await OpenAsync(1);
        Assert.False(editor.CanApplyGlobal); Assert.Contains("Escolha uma operação", editor.GlobalImpactText);
        editor.AuthorOperation = BatchOperation.Replace;
        Assert.False(editor.CanApplyGlobal); Assert.Contains("Autor", editor.GlobalImpactText);     // falta o valor
        editor.GlobalAuthor = "A";
        Assert.True(editor.CanApplyGlobal);
        editor.ClearFormCommand.Execute(null);
        Assert.Equal(BatchOperation.Keep, editor.AuthorOperation); Assert.Equal(string.Empty, editor.GlobalAuthor); Assert.False(editor.CanApplyGlobal);
    }

    [Fact]
    public async Task CopyFromFocused_ReplacesTheOthersWithTheFocusedMetadata()
    {
        var (_, editor) = await OpenAsync(3);
        var source = Row(editor, "p1.jpg");
        source.TitleText = "Modelo"; source.AuthorText = "Eu"; source.NewKeyword = "k1, k2"; source.AddKeywordCommand.Execute(null);
        editor.Focused = source;
        Assert.True(editor.CopyFromFocusedCommand.CanExecute(null));
        await editor.CopyFromFocusedAsync();
        foreach (var row in editor.Rows.Where(r => r != source))
        {
            Assert.Equal("Modelo", row.TitleText); Assert.Equal("Eu", row.AuthorText); Assert.Equal(["k1", "k2"], row.Keywords);
        }
        Assert.Contains("copiados para 2", editor.StatusText);
    }

    [Fact]
    public async Task DedupeKeywords_RemovesRepeatedWordsFromMarkedRows()
    {
        var (_, editor) = await OpenAsync(1);
        var row = editor.Rows[0];
        row.Keywords.Add("sol"); row.Keywords.Add("Sol"); row.Keywords.Add("mar");
        editor.DedupeKeywordsCommand.Execute(null);
        Assert.Equal(["sol", "mar"], row.Keywords);
    }

    // ---------- rascunhos, reverter, presets

    [Fact]
    public async Task Drafts_SurviveSelectionChanges_AndCleanRowsAreReplaced()
    {
        var (library, editor) = await OpenAsync(3);
        Row(editor, "p1.jpg").TitleText = "rascunho";
        library.UpdateSelection([library.Photos[2]]);
        library.SelectedPhoto = library.Photos[2];
        editor.RefreshTargets();
        Assert.Equal(["p3.jpg", "p1.jpg"], editor.Rows.Select(r => r.FileName));                 // p1 (alterada) permanece; p2 (limpa) sumiu
        Assert.Equal("rascunho", Row(editor, "p1.jpg").TitleText);
        Assert.Equal(1, editor.ChangedCount);
    }

    [Fact]
    public async Task RevertAll_AsksFirst_AndOnlyDiscardsDrafts()
    {
        var (_, editor) = await OpenAsync(2);
        foreach (var row in editor.Rows) row.TitleText = "T";
        editor.ConfirmRevert = _ => false;
        editor.RevertAllCommand.Execute(null);
        Assert.Equal(2, editor.ChangedCount);
        editor.ConfirmRevert = count => count == 2;
        editor.RevertAllCommand.Execute(null);
        Assert.Equal(0, editor.ChangedCount);
        Assert.All(editor.Rows, r => Assert.Null(MetadataExtractorReader.Read(r.Photo.CurrentPath).Title));
    }

    [Fact]
    public async Task Presets_SaveLoadDelete_RestoreTheForm_AndPersist()
    {
        var (library, editor) = await OpenAsync(1);
        editor.AuthorOperation = BatchOperation.Replace; editor.GlobalAuthor = "Marcos André";
        editor.KeywordsOperation = BatchOperation.Add; editor.Keywords.Add("Brazil"); editor.Keywords.Add("travel");
        editor.PresetName = "Microstock Marcos";
        Assert.True(editor.SavePresetCommand.CanExecute(null));
        editor.SavePresetCommand.Execute(null);
        await TestEnvironment.WaitUntilAsync(() => editor.Presets.Count == 1);

        editor.ClearFormCommand.Execute(null);
        editor.SelectedPreset = editor.Presets[0];
        editor.LoadPresetCommand.Execute(null);
        Assert.Equal(BatchOperation.Replace, editor.AuthorOperation); Assert.Equal("Marcos André", editor.GlobalAuthor);
        Assert.Equal(["Brazil", "travel"], editor.Keywords); Assert.Equal(BatchOperation.Add, editor.KeywordsOperation);

        var again = Editor(library);
        await TestEnvironment.WaitUntilAsync(() => again.Presets.Count == 1);
        editor.DeletePresetCommand.Execute(null);
        await TestEnvironment.WaitUntilAsync(() => editor.Presets.Count == 0);
    }

    [Fact]
    public async Task FocusedRow_ShowsExifAndHistory()
    {
        var (_, editor) = await OpenAsync(2);
        Row(editor, "p1.jpg").TitleText = "v1";
        await editor.SaveAllAsync();
        editor.Focused = Row(editor, "p1.jpg");
        await TestEnvironment.WaitUntilAsync(() => editor.FocusedHistory.Count == 2);          // v1 + Original
        Assert.Contains("Cam1", editor.FocusedCamera);
        editor.Focused = Row(editor, "p2.jpg");
        await TestEnvironment.WaitUntilAsync(() => editor.FocusedHistory.Count == 0);
        Assert.Equal("Sem localização", editor.FocusedGps);
    }

    // ---------- migração (bancos antigos)

    [Fact]
    public async Task OldDatabaseWithoutMetadataVersionColumn_IsUpgradedKeepingData()
    {
        var dbPath = Path.Combine(_env.Root, "old", "old.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Photos (Id INTEGER PRIMARY KEY AUTOINCREMENT, FileName TEXT NOT NULL, CurrentPath TEXT NOT NULL UNIQUE, Extension TEXT NOT NULL, FileSize INTEGER NOT NULL, Width INTEGER NULL, Height INTEGER NULL,
                    CreatedAt TEXT NOT NULL, ModifiedAt TEXT NOT NULL, DateTaken TEXT NULL, ImportedAt TEXT NOT NULL, ContentHash TEXT NULL, IsMissing INTEGER NOT NULL DEFAULT 0,
                    CategoryName TEXT NULL, PersonalNote TEXT NULL, Rating INTEGER NOT NULL DEFAULT 0, IsFavorite INTEGER NOT NULL DEFAULT 0);
                INSERT INTO Photos (FileName, CurrentPath, Extension, FileSize, CreatedAt, ModifiedAt, ImportedAt, CategoryName, Rating, IsFavorite)
                VALUES ('x.jpg', 'C:\x.jpg', '.jpg', 10, '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z', 'Viagens', 4, 1);
                """;
            await command.ExecuteNonQueryAsync();
        }
        var repository = new SqliteCatalogRepository(dbPath);
        await repository.InitializeAsync();
        await repository.InitializeAsync();
        var photo = (await repository.GetAllAsync()).Single();
        Assert.Equal("Viagens", photo.CategoryName); Assert.Equal(4, photo.Rating); Assert.True(photo.IsFavorite); Assert.Equal(0, photo.MetadataVersion);
    }

    // ---------- tela real

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

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

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

    [Fact]
    public void RealWindow_SelectionTravelsToTheEditor_RowsLoadLazily_AndSelectionIsRestoredOnReturn()
    {
        for (var i = 1; i <= 3; i++) CreateJpeg($"p{i}.jpg", "Cam" + i);
        RunSta(() =>
        {
            var app = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (app.Resources.MergedDictionaries.Count == 0)
                foreach (var name in new[] { "Colors", "Typography", "Buttons", "Inputs", "Cards", "Tabs", "Tree" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/PhotoManager;component/Themes/{name}.xaml") });
            var navigation = new NavigationService();
            var main = new MainViewModel(navigation, _env.Catalog, _env.Thumbnails, _env.Organization, _env.FileOperations, new MetadataExtractorReader(), _env.MetadataEditing,
                metadataPresets: _env.Repository);
            var window = new MainWindow(main);
            var library = (LibraryViewModel)main.CurrentView;
            window.Show();
            try
            {
                var import = library.ImportFolderAsync(_env.Photos);
                var f = new DispatcherFrame(); import.ContinueWith(_ => f.Continue = false); if (!import.IsCompleted) Dispatcher.PushFrame(f);
                Pump(300); window.UpdateLayout();

                var grid = Find<ListBox>(window, l => l.Name == "PhotoList")!;
                var boxes = Enumerable.Range(0, 3).Select(i => Find<CheckBox>((DependencyObject)grid.ItemContainerGenerator.ContainerFromIndex(i))!).ToList();
                boxes[0].IsChecked = true; boxes[2].IsChecked = true; Pump(50);
                Assert.Equal(2, library.SelectionCount);

                main.NavigateCommand.Execute("Metadata");                            // o que o botão da aba Metadados faz
                Pump(500); window.UpdateLayout();
                var editor = (MetadataEditorViewModel)main.CurrentView;
                Assert.Equal(["p1.jpg", "p3.jpg"], editor.Rows.Select(r => r.FileName));
                var list = Find<ListBox>(window, l => l.Name == "RowsList")!;
                Assert.Equal(2, list.Items.Count);
                for (var wait = 0; editor.Rows.Any(r => !r.IsLoaded) && wait < 80; wait++) Pump(100);   // leitura preguiçosa disparada pelas linhas visíveis
                Assert.All(editor.Rows, r => Assert.True(r.IsLoaded));

                var title = Find<TextBox>(window, t => t.Text == string.Empty && t.IsEnabled && t.ToolTip is null);   // um campo editável da primeira linha
                Assert.NotNull(title);
                editor.Rows[0].TitleText = "Digitado"; Pump(50);
                Assert.Equal(1, editor.ChangedCount);
                Assert.NotNull(Find<Button>(window, b => b.Content as string == "Salvar" && b.IsVisible));        // botão por linha aparece quando alterada
                if (Environment.GetEnvironmentVariable("PM_SNAPSHOT_DIR") is { Length: > 0 } dir)       // captura opcional para inspeção visual
                {
                    editor.AuthorOperation = BatchOperation.Replace; editor.GlobalAuthor = "Marcos André";
                    editor.KeywordsOperation = BatchOperation.Add; editor.Keywords.Add("Brazil"); editor.Keywords.Add("travel");
                    var apply = editor.ApplyGlobalAsync(); var f3 = new DispatcherFrame(); apply.ContinueWith(_ => f3.Continue = false); if (!apply.IsCompleted) Dispatcher.PushFrame(f3);
                    editor.Rows[1].DescriptionText = "Vista aérea ao pôr do sol"; editor.Rows[1].Keywords.Add("cidade");
                    Pump(300); window.UpdateLayout();
                    var target = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    target.Render(window);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(target));
                    using var file = File.Create(Path.Combine(dir, "editor.png")); png.Save(file);
                }

                main.NavigateCommand.Execute("Library");                              // volta: a grade nova restaura a seleção múltipla
                Pump(600); window.UpdateLayout();
                var grid2 = Find<ListBox>(window, l => l.Name == "PhotoList")!;
                for (var wait = 0; (grid2.SelectedItems.Count != 2 || library.SelectionCount != 2) && wait < 80; wait++) Pump(100);   // a restauração roda em prioridade de fundo
                Assert.True(grid2.SelectedItems.Count == 2, $"grade: {grid2.SelectedItems.Count}, vm: {library.SelectionCount}, pend: {library.SelectedCards.Count}");
                Assert.Equal(2, library.SelectionCount);
            }
            finally { window.Close(); }
        });
    }
}
