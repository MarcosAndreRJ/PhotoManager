using System.IO;
using PhotoManager.Application.Transfer;
using PhotoManager.Domain.Photos;
using PhotoManager.Infrastructure.Files;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

/// <summary>Respostas prontas para as perguntas da Transferência (sem janelas).</summary>
internal sealed class FakeTransferDialogs : ITransferDialogs
{
    public List<string> Questions { get; } = [];
    public bool ConfirmAnswer { get; set; } = true;
    public ConflictPolicy? ConflictAnswer { get; set; } = ConflictPolicy.Skip;
    public int ConflictCount { get; private set; }
    public Func<BatchRenameViewModel, bool> OnBatchRename { get; set; } = _ => false;
    public Func<OrganizeByDateViewModel, bool> OnOrganize { get; set; } = _ => false;

    public bool Confirm(string question) { Questions.Add(question); return ConfirmAnswer; }
    public ConflictPolicy? AskConflict(int count, string example, bool move) { ConflictCount = count; return ConflictAnswer; }
    public bool BatchRename(BatchRenameViewModel viewModel) => OnBatchRename(viewModel);
    public bool OrganizeByDate(OrganizeByDateViewModel viewModel) => OnOrganize(viewModel);
}

internal sealed class FakeClipboard : IFileClipboard
{
    public (IReadOnlyList<string> Paths, bool Cut)? Content { get; set; }
    public void SetFiles(IReadOnlyList<string> paths, bool cut) => Content = (paths, cut);
    public (IReadOnlyList<string> Paths, bool Cut)? GetFiles() => Content;
    public void Clear() => Content = null;
}

internal sealed class MemorySettings : ITransferSettings
{
    public TransferLayout? Saved { get; private set; }
    public TransferLayout Load() => Saved ?? TransferLayout.Default;
    public void Save(TransferLayout layout) => Saved = layout;
}

/// <summary>Transferência como tela de organização: pastas, cores, copiar/mover, desfazer, lote, data, comparação, locais, layout, área de transferência, duplicatas e detalhes.</summary>
[Collection("WpfUi")]
public sealed class TransferOrganizeTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly TransferOrganizer _organizer;
    private readonly FakeTransferDialogs _dialogs = new();
    private readonly FakeClipboard _clipboard = new();

    public TransferOrganizeTests() => _organizer = new TransferOrganizer(_env.Repository, _env.Catalog, metadataReader: new PhotoManager.Infrastructure.Metadata.MetadataExtractorReader(), infoReader: (PhotoManager.Application.Catalog.IPhotoInfoReader)_env.Thumbnails);
    public void Dispose() => _env.Dispose();

    private string Root => Path.Combine(_env.Root, "org");
    private string Dir(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;
    private string Png(string relative, byte shade = 120) => Path.GetFullPath(_env.CreatePng(Path.Combine("..", "org", relative), shade));
    private string File(string relative, string content = "x")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private TransferPaneViewModel Pane() => new(organizer: _organizer, dialogs: _dialogs);
    private TransferViewModel Two(string left, string right, ITransferSettings? settings = null) => new(null, left, right, _organizer, _dialogs, settings, _clipboard);

    private static async Task Ready(TransferViewModel view, string left, string right) =>
        await TestEnvironment.WaitUntilAsync(() => view.LeftPane.CurrentPath == left && view.RightPane.CurrentPath == right && !view.LeftPane.IsLoading && !view.RightPane.IsLoading
            && view.LeftPane.Items.Count == view.LeftPane.Entries.Count && view.RightPane.Items.Count == view.RightPane.Entries.Count);

    // ---------- regras puras ----------

    [Fact]
    public void Names_AreValidatedLikeWindows_AndSuggestedUnique()
    {
        string[] siblings = ["Fotos", "Nova pasta", "Nova pasta (2)"];
        Assert.Null(TransferNames.Validate("Viagem 2026", siblings));
        Assert.NotNull(TransferNames.Validate("  ", siblings));
        Assert.NotNull(TransferNames.Validate("a/b", siblings));
        Assert.NotNull(TransferNames.Validate("CON", siblings));
        Assert.NotNull(TransferNames.Validate("termina.", siblings));
        Assert.NotNull(TransferNames.Validate("fotos", siblings));
        Assert.Null(TransferNames.Validate("FOTOS", siblings, current: "Fotos"));
        Assert.Equal("Nova pasta (3)", TransferNames.Unique(siblings));
        Assert.Equal("praia.JPG", TransferNames.WithOriginalExtension("praia", "IMG_1.JPG"));
        Assert.Equal(@"D:\Novo\sub\a.jpg", TransferNames.Rebase(@"D:\Velho\sub\a.jpg", @"D:\Velho", @"D:\Novo"));
        Assert.True(TransferNames.IsSameOrInside(@"D:\Velho\sub", @"D:\velho"));
        Assert.False(TransferNames.IsSameOrInside(@"D:\Velho2", @"D:\Velho"));
    }

    [Fact]
    public void Conflicts_NormalizeDropsSidecarsAndSelfMoves_AndNumberCopies()
    {
        string[] sources = [@"D:\A\foto.cr2", @"D:\A\foto.xmp", @"D:\B\x.jpg", @"D:\Dest\ja.jpg", @"D:\Dest"];
        Assert.Equal([@"D:\A\foto.cr2", @"D:\B\x.jpg"], TransferConflicts.Normalize(sources, @"D:\Dest", move: true));
        Assert.Contains(@"D:\Dest\ja.jpg", TransferConflicts.Normalize(sources, @"D:\Dest", move: false));      // copiar para a mesma pasta = duplicar
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\Dest\foto (2).jpg" };
        Assert.Equal(@"D:\Dest\foto (3).jpg", TransferConflicts.KeepBothName(@"D:\Dest", "foto.jpg", taken.Contains));
        Assert.Equal(@"D:\Dest\2026.10 (2)", TransferConflicts.KeepBothName(@"D:\Dest", "2026.10", _ => false, isFolder: true));
    }

    [Fact]
    public void Comparer_MarksOnlyHereSameAndDifferent()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        TransferEntry E(string side, string name, long size, int seconds = 0, bool folder = false) =>
            new($@"{side}:\{name}", name, folder ? TransferEntryKind.Folder : TransferEntryKind.Photo, size, t.AddSeconds(seconds));
        var (left, right) = FolderComparer.Compare(
            [E("L", "a.jpg", 10), E("L", "b.jpg", 10), E("L", "c.jpg", 10), E("L", "Pasta", 0, folder: true)],
            [E("R", "A.JPG", 10, seconds: 1), E("R", "b.jpg", 11), E("R", "d.jpg", 5), E("R", "pasta", 0, folder: true)]);
        Assert.Equal(CompareState.Same, left[@"L:\a.jpg"]);                     // 1 s de diferença é tolerado (FAT/exFAT)
        Assert.Equal(CompareState.Different, left[@"L:\b.jpg"]);
        Assert.Equal(CompareState.OnlyHere, left[@"L:\c.jpg"]);
        Assert.Equal(CompareState.Same, left[@"L:\Pasta"]);
        Assert.Equal(CompareState.OnlyHere, right[@"R:\d.jpg"]);
    }

    [Fact]
    public void DatePlanner_And_BatchRename_AndUndoStack_AndRecents()
    {
        var plan = DateFolderPlanner.Plan([(@"X:\a.jpg", new DateTime(2026, 10, 5)), (@"X:\b.jpg", new DateTime(2026, 10, 20)), (@"X:\c.jpg", new DateTime(2025, 1, 2))], @"D:\Fotos", DateFolderPlanner.Patterns[0]);
        Assert.Equal([(@"D:\Fotos\2025\01", 1), (@"D:\Fotos\2026\10", 2)], plan.Select(p => (p.Folder, p.Files.Count)));
        Assert.Contains("Outubro", DateFolderPlanner.Patterns[1].RelativeFolder(new DateTime(2026, 10, 5)));

        var files = new[] { (@"D:\F\IMG_1.jpg", new DateTime(2026, 10, 5, 14, 30, 5)), (@"D:\F\IMG_2.JPG", new DateTime(2026, 10, 6)) };
        var (renames, error) = BatchRenamePlanner.Plan(files, "{data}_viagem_{seq}", 1, ["outro.jpg"]);
        Assert.Null(error);
        Assert.Equal([@"D:\F\2026-10-05_viagem_001.jpg", @"D:\F\2026-10-06_viagem_002.JPG"], renames.Select(r => r.To));
        Assert.NotNull(BatchRenamePlanner.Plan(files, "mesmo", 1, []).Error);                 // dois iguais
        Assert.NotNull(BatchRenamePlanner.Plan(files, "outro_{seq}", 1, ["outro_001.jpg"]).Error);

        var stack = new TransferUndoStack(capacity: 2);
        stack.Push(TransferUndoEntry.Rename("a", "b")); stack.Push(TransferUndoEntry.Rename("c", "d")); stack.Push(TransferUndoEntry.Rename("e", "f"));
        Assert.Equal(2, stack.Count);
        Assert.Equal("f", stack.Pop()!.Moves[0].To);
        Assert.Equal([@"C:\b", @"C:\a"], TransferPlaces.AddRecent([@"C:\a", @"C:\B\"], @"C:\b", max: 5));
    }

    // ---------- serviço: disco + catálogo ----------

    [Fact]
    public async Task Repository_ListsEntriesDirectlyOrRecursively_EvenWithLikeWildcardsInNames()
    {
        var a = Png("100%_fotos/a.png");
        var deep = Png("100%_fotos/sub/b.png");
        Png("100x_fotos/c.png");
        await _env.Catalog.ImportPathsAsync([Root]);
        var folder = Path.Combine(Root, "100%_fotos");
        Assert.Equal([a], (await _env.Repository.GetEntriesUnderAsync(folder, recursive: false)).Select(e => e.Path));
        Assert.Equal([a, deep], (await _env.Repository.GetEntriesUnderAsync(folder, recursive: true)).Select(e => e.Path).OrderBy(p => p.Length));
    }

    [Fact]
    public async Task RenameFolder_CatalogAndFolderColorsFollow()
    {
        var photo = Png("Viagem/dia1/praia.png");
        await _env.Catalog.ImportPathsAsync([photo]);
        var item = await _env.Repository.FindByPathAsync(photo);
        await _env.Repository.UpdateColorLabelAsync([item!.Id], PhotoColor.Green);
        await _organizer.SetFolderColorAsync([Path.Combine(Root, "Viagem", "dia1")], PhotoColor.Blue);

        var result = await _organizer.RenameAsync(Path.Combine(Root, "Viagem"), "Viagem Nordeste");

        var moved = Path.Combine(Root, "Viagem Nordeste", "dia1", "praia.png");
        Assert.Equal(1, result.CatalogItemsUpdated);
        var after = await _env.Repository.FindByPathAsync(moved);
        Assert.Equal((item.Id, PhotoColor.Green, false), (after!.Id, after.ColorLabel, after.IsMissing));
        Assert.Equal(PhotoColor.Blue, (await _organizer.GetFolderColorsAsync(Path.Combine(Root, "Viagem Nordeste")))[Path.Combine(Root, "Viagem Nordeste", "dia1")]);

        await _organizer.RenameAsync(Path.Combine(Root, "Viagem Nordeste"), "VIAGEM NORDESTE");   // só maiúsculas
        Assert.Equal("VIAGEM NORDESTE", new DirectoryInfo(Root).GetDirectories().Single().Name);
    }

    [Fact]
    public async Task RenameFile_KeepsExtension_TakesSidecar_AndRejectsDuplicates()
    {
        var photo = Png("R/IMG_1.png");
        System.IO.File.WriteAllText(Path.ChangeExtension(photo, ".xmp"), "<x/>");
        Png("R/existe.png");
        var result = await _organizer.RenameAsync(photo, "pôr do sol");
        Assert.Equal(Path.Combine(Path.GetDirectoryName(photo)!, "pôr do sol.png"), result.NewPath);
        Assert.True(System.IO.File.Exists(Path.ChangeExtension(result.NewPath, ".xmp")));
        await Assert.ThrowsAsync<IOException>(() => _organizer.RenameAsync(result.NewPath, "existe"));
        await Assert.ThrowsAsync<IOException>(() => _organizer.CreateFolderAsync(Path.GetDirectoryName(photo)!, "a?b"));
    }

    [Fact]
    public async Task SetColor_OnFilesOutsideTheCatalog_AddsThemOnlyWhenAllowed()
    {
        var inside = Png("C/dentro.png");
        var outside = Png("C/fora.png");
        var notes = File("C/notas.txt");
        await _env.Catalog.ImportPathsAsync([inside]);
        var onlyCataloged = await _organizer.SetColorAsync([inside, outside], PhotoColor.Red, addMissing: false);
        Assert.Equal((1, 0, 1), (onlyCataloged.Colored, onlyCataloged.Added, onlyCataloged.Skipped));
        var all = await _organizer.SetColorAsync([inside, outside, notes], PhotoColor.Blue, addMissing: true);
        Assert.Equal((2, 1, 1), (all.Colored, all.Added, all.Skipped));
        Assert.Equal(PhotoColor.Blue, (await _organizer.GetColorsAsync(Path.Combine(Root, "C")))[outside]);
    }

    [Fact]
    public async Task Transfer_CopiesWithProgress_KeepingDatesAndSidecar_AndHonorsConflictPolicies()
    {
        var raw = Png("S/foto.png");
        System.IO.File.WriteAllText(Path.ChangeExtension(raw, ".xmp"), "<x/>");
        var when = new DateTime(2020, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        System.IO.File.SetLastWriteTimeUtc(raw, when);
        File("S/sub/notas.txt", "abc");
        var dest = Dir("D");
        var reports = new List<TransferProgress>();
        var progress = new SyncProgress(reports.Add);

        var copy = await _organizer.TransferAsync(new([raw, Path.ChangeExtension(raw, ".xmp"), Path.Combine(Root, "S", "sub")], dest, Move: false), progress);
        Assert.Equal(2, copy.Files);
        Assert.Equal(when, System.IO.File.GetLastWriteTimeUtc(Path.Combine(dest, "foto.png")));
        Assert.True(System.IO.File.Exists(Path.Combine(dest, "foto.xmp")));
        Assert.True(System.IO.File.Exists(Path.Combine(dest, "sub", "notas.txt")));
        Assert.True(System.IO.File.Exists(raw));
        Assert.Equal([Path.Combine(dest, "foto.png"), Path.Combine(dest, "sub")], copy.Created);
        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1].Fraction, 2);
        Assert.Empty(Directory.GetFiles(dest, "*.pmpart", SearchOption.AllDirectories));

        Assert.Equal(1, (await _organizer.TransferAsync(new([raw], dest, false, ConflictPolicy.Skip))).Skipped);
        await _organizer.TransferAsync(new([raw], dest, false, ConflictPolicy.KeepBoth));
        Assert.True(System.IO.File.Exists(Path.Combine(dest, "foto (2).png")));
        System.IO.File.WriteAllText(Path.Combine(dest, "sub", "notas.txt"), "antigo");
        await _organizer.TransferAsync(new([Path.Combine(Root, "S", "sub")], dest, false, ConflictPolicy.Replace));   // pasta mescla; arquivo substitui
        Assert.Equal("abc", System.IO.File.ReadAllText(Path.Combine(dest, "sub", "notas.txt")));
        await _organizer.TransferAsync(new([Path.Combine(Root, "S", "sub")], dest, false, ConflictPolicy.KeepBoth));  // pasta ganha número
        Assert.True(Directory.Exists(Path.Combine(dest, "sub (2)")));

        await _organizer.TransferAsync(new([raw], Path.GetDirectoryName(raw)!, false, ConflictPolicy.Skip));          // copiar para a própria pasta = duplicar
        Assert.True(System.IO.File.Exists(Path.Combine(Root, "S", "foto (2).png")));
    }

    [Fact]
    public async Task Transfer_MoveFolder_CatalogFollows_AndMergeMovesFileByFile()
    {
        var photo = Png("M/Album/a.png");
        await _env.Catalog.ImportPathsAsync([photo]);
        var dest = Dir("Destino");
        var moved = await _organizer.TransferAsync(new([Path.Combine(Root, "M", "Album")], dest, Move: true));
        Assert.Equal([(Path.Combine(Root, "M", "Album"), Path.Combine(dest, "Album"))], moved.Moves);
        Assert.NotNull(await _env.Repository.FindByPathAsync(Path.Combine(dest, "Album", "a.png")));

        Png("M2/Album/b.png");
        var merge = await _organizer.TransferAsync(new([Path.Combine(Root, "M2", "Album")], dest, Move: true));
        Assert.True(System.IO.File.Exists(Path.Combine(dest, "Album", "b.png")));
        Assert.False(Directory.Exists(Path.Combine(Root, "M2", "Album")));                  // origem vazia some
        Assert.Contains(merge.Moves, m => m.To == Path.Combine(dest, "Album", "b.png"));   // mescla registra cada arquivo (para desfazer)
    }

    [Fact]
    public async Task Transfer_Cancelled_LeavesNoPartialFile()
    {
        var big = File("Big/video.mp4", new string('x', 3 * 1024 * 1024));
        var dest = Dir("Out");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.BytesDone > 0) cts.Cancel(); });
        var result = await _organizer.TransferAsync(new([big], dest, Move: false), progress, cts.Token);
        Assert.True(result.Cancelled);
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Fact]
    public async Task MovePaths_SwapsNames_AndCatalogFollows()
    {
        var a = Png("W/a.png", 10);
        var b = Png("W/b.png", 200);
        await _env.Catalog.ImportPathsAsync([a, b]);
        var idA = (await _env.Repository.FindByPathAsync(a))!.Id;
        var failed = await _organizer.MovePathsAsync([(a, b), (b, a)]);
        Assert.Empty(failed);
        Assert.Equal(idA, (await _env.Repository.FindByPathAsync(b))!.Id);
        Assert.Empty(Directory.GetFiles(Path.Combine(Root, "W"), "*pmswap*"));
    }

    [Fact]
    public async Task Duplicates_DatesAndDetails()
    {
        var original = Png("Lib/original.png", 50);
        await _env.Catalog.ImportPathsAsync([original]);
        var copy = Path.Combine(Dir("Card"), "IMG_0001.png");
        System.IO.File.Copy(original, copy);
        var other = Png("Card/outra.png", 51);                                              // mesmo tamanho, conteúdo diferente
        var found = await _organizer.FindDuplicatesAsync([copy, other, original]);
        Assert.Equal([original], found[copy]);
        Assert.False(found.ContainsKey(other));
        Assert.False(found.ContainsKey(original));

        var date = await _organizer.GetCaptureDateAsync(copy);
        Assert.False(date.FromMetadata);                                                    // PNG sem EXIF: data de modificação
        var details = await _organizer.GetDetailsAsync(original);
        Assert.Contains(details.Rows, r => r.Label == "Resolução" && r.Value.StartsWith("64 × 48"));
        Assert.Contains(details.Rows, r => r.Label == "Catálogo" && r.Value.StartsWith("Sim"));
        Assert.Contains((await _organizer.GetDetailsAsync(Path.Combine(Root, "Card"))).Rows, r => r.Label == "Conteúdo");
    }

    // ---------- painéis ----------

    [Fact]
    public async Task Pane_ColorsFilesAndFolders_AskingBeforeCataloging_AndFilters()
    {
        var inside = Png("P/dentro.png");
        var outside = Png("P/fora.png");
        Dir("P/sub");
        File("P/notas.txt");
        await _env.Catalog.ImportPathsAsync([inside]);
        await _env.Catalog.SetColorLabelAsync([(await _env.Repository.FindByPathAsync(inside))!], PhotoColor.Yellow);

        var pane = Pane();
        await pane.NavigateAsync(Path.Combine(Root, "P"));
        var fora = pane.Items.Single(i => i.Name == "fora.png");
        Assert.Equal(PhotoColor.Yellow, pane.Items.Single(i => i.Name == "dentro.png").ColorLabel);
        Assert.False(fora.IsCataloged);

        pane.SetSelection([fora]);
        await pane.SetColorForSelectionAsync(PhotoColor.Purple);
        Assert.Single(_dialogs.Questions);
        Assert.Equal(PhotoColor.Purple, fora.ColorLabel);
        Assert.True(fora.IsCataloged);

        pane.SetSelection([pane.Items.Single(i => i.IsFolder)]);
        Assert.True(pane.CanColor);
        await pane.SetColorForSelectionAsync(PhotoColor.Green);                             // cor de pasta
        Assert.Equal(PhotoColor.Green, pane.Items.Single(i => i.IsFolder).ColorLabel);
        await pane.RefreshAsync();
        Assert.Equal(PhotoColor.Green, pane.Items.Single(i => i.IsFolder).ColorLabel);       // persistida

        pane.TypeFilter = TransferPaneViewModel.FoldersOnly;
        Assert.Equal(["sub"], pane.Items.Select(i => i.Name));
        pane.TypeFilter = TransferPaneViewModel.AllTypes;
        pane.ColorFilter = "Roxo";
        Assert.Equal(["fora.png"], pane.Items.Select(i => i.Name));
        pane.ClearFiltersCommand.Execute(null);
        Assert.Equal(4, pane.Items.Count);
    }

    [Fact]
    public async Task NewFolder_IsCreatedAndRenamedInPlace_OtherPaneFollows_AndUndoRemovesIt()
    {
        var root = Dir("F");
        Png("F/Album/a.png");
        var view = Two(root, root);
        await Ready(view, root, root);
        var left = view.LeftPane; var right = view.RightPane;

        await left.CreateFolderAsync();
        var created = left.Items.Single(i => i.Name == "Nova pasta");
        Assert.True(created.IsEditing);
        created.EditText = "Casamento";
        await left.CommitInlineRenameAsync(created);
        Assert.True(Directory.Exists(Path.Combine(root, "Casamento")));
        await TestEnvironment.WaitUntilAsync(() => right.Items.Any(i => i.Name == "Casamento"));

        // renomear no lugar enquanto o painel direito está DENTRO da pasta: ele segue o novo nome
        await right.NavigateAsync(Path.Combine(root, "Album"));
        left.SetSelection([left.Items.Single(i => i.Name == "Album")]);
        left.RenameCommand.Execute(null);
        var album = left.Items.Single(i => i.Name == "Album");
        Assert.True(album.IsEditing);
        Assert.Equal("Album", album.EditText);
        album.EditText = "Álbum 2026";
        await left.CommitInlineRenameAsync(album);
        await TestEnvironment.WaitUntilAsync(() => right.CurrentPath == Path.Combine(root, "Álbum 2026"));

        // Ctrl+Z: desfaz o renomear e depois a criação da pasta
        await view.UndoAsync();
        Assert.True(Directory.Exists(Path.Combine(root, "Album")));
        await TestEnvironment.WaitUntilAsync(() => right.CurrentPath == Path.Combine(root, "Album"));
        await view.UndoAsync();
        Assert.False(Directory.Exists(Path.Combine(root, "Casamento")));
        Assert.False(view.CanUndo);
    }

    [Fact]
    public async Task CenterButtons_CopyAndMove_WithConflictPrompt_AndUndo()
    {
        var left = Dir("L"); var right = Dir("R");
        Png("L/a.png", 10); Png("L/b.png", 20);
        File("R/b.png", "já existe");
        var view = Two(left, right);
        await Ready(view, left, right);
        view.LeftPane.SetSelection(view.LeftPane.Items);
        _dialogs.ConflictAnswer = ConflictPolicy.KeepBoth;

        Assert.True(view.CopyToRightCommand.CanExecute(null));
        await view.SendAsync(view.LeftPane, view.RightPane, move: false);
        Assert.Equal(1, _dialogs.ConflictCount);
        Assert.Equal(["a.png", "b (2).png", "b.png"], Directory.GetFiles(right).Select(Path.GetFileName).OrderBy(n => n));
        Assert.Contains("2 arquivo(s) copiado(s)", view.TransferMessage);
        await TestEnvironment.WaitUntilAsync(() => view.RightPane.Items.Count == 3);

        await view.UndoAsync();                                                              // cópias vão para a Lixeira
        Assert.Equal(["b.png"], Directory.GetFiles(right).Select(Path.GetFileName));

        view.LeftPane.SetSelection(view.LeftPane.Items.Where(i => i.Name == "a.png"));
        await view.SendAsync(view.LeftPane, view.RightPane, move: true);
        Assert.False(System.IO.File.Exists(Path.Combine(left, "a.png")));
        await view.UndoAsync();                                                              // volta para a esquerda
        Assert.True(System.IO.File.Exists(Path.Combine(left, "a.png")));
        Assert.False(System.IO.File.Exists(Path.Combine(right, "a.png")));

        _dialogs.ConflictAnswer = null;                                                      // cancelar na pergunta
        await view.LeftPane.RefreshAsync();
        view.LeftPane.SetSelection(view.LeftPane.Items.Where(i => i.Name == "b.png"));
        Assert.Null(await view.RunTransferAsync([new TransferJob([Path.Combine(left, "b.png")], right)], move: false, "teste"));
    }

    [Fact]
    public async Task Clipboard_CopyCutPaste_BetweenPanes()
    {
        var left = Dir("CL"); var right = Dir("CR");
        Png("CL/a.png");
        var view = Two(left, right);
        await Ready(view, left, right);
        view.LeftPane.SetSelection(view.LeftPane.Items);
        view.LeftPane.CopyCommand.Execute(null);
        Assert.False(_clipboard.Content!.Value.Cut);
        await view.RightPane.PasteAsync();
        Assert.True(System.IO.File.Exists(Path.Combine(right, "a.png")));
        Assert.NotNull(_clipboard.Content);                                                  // cópia pode ser colada de novo

        view.LeftPane.SetSelection(view.LeftPane.Items);
        view.LeftPane.CutCommand.Execute(null);
        await view.RightPane.NavigateAsync(Dir("CR/sub"));
        await view.RightPane.PasteAsync();
        Assert.True(System.IO.File.Exists(Path.Combine(right, "sub", "a.png")));
        Assert.False(System.IO.File.Exists(Path.Combine(left, "a.png")));
        Assert.Null(_clipboard.Content);                                                     // recortar+colar esvazia
    }

    [Fact]
    public async Task BatchRename_AndOrganizeByDate_ThroughDialogs_AreUndoable()
    {
        var folder = Dir("B");
        var a = Png("B/IMG_1.png", 10); var b = Png("B/IMG_2.png", 20);
        System.IO.File.SetLastWriteTime(a, new DateTime(2025, 12, 31, 10, 0, 0));
        System.IO.File.SetLastWriteTime(b, new DateTime(2026, 1, 2, 9, 0, 0));
        var other = Dir("Arquivo");
        var view = Two(folder, other);
        await Ready(view, folder, other);
        var pane = view.LeftPane;

        pane.SetSelection(pane.Items);
        _dialogs.OnBatchRename = vm => { vm.Template = "viagem_{seq}"; return vm.IsValid; };
        pane.RenameCommand.Execute(null);                                                    // vários arquivos → lote
        await TestEnvironment.WaitUntilAsync(() => pane.Items.Any(i => i.Name == "viagem_001.png") && !pane.IsBusy);
        await view.UndoAsync();
        Assert.True(System.IO.File.Exists(a));

        await pane.RefreshAsync();
        _dialogs.OnOrganize = vm => { vm.UseOtherPane = true; vm.Move = true; return true; };
        await pane.OrganizeByDateAsync();
        Assert.True(System.IO.File.Exists(Path.Combine(other, "2025", "12", "IMG_1.png")));
        Assert.True(System.IO.File.Exists(Path.Combine(other, "2026", "01", "IMG_2.png")));
        await view.UndoAsync();                                                              // arquivos voltam e as pastas criadas somem
        Assert.True(System.IO.File.Exists(a) && System.IO.File.Exists(b));
        Assert.Empty(Directory.GetFileSystemEntries(other));
    }

    [Fact]
    public async Task Compare_MarksBothSides_AndHidesEqualItems()
    {
        var left = Dir("XL"); var right = Dir("XR");
        var same = Png("XL/igual.png");
        System.IO.File.Copy(same, Path.Combine(right, "igual.png"));
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(right, "igual.png"), System.IO.File.GetLastWriteTimeUtc(same));
        Png("XL/so-esquerda.png");
        File("XR/so-direita.txt");
        var view = Two(left, right);
        await Ready(view, left, right);
        view.IsComparing = true;
        Assert.Equal(CompareState.Same, view.LeftPane.Items.Single(i => i.Name == "igual.png").CompareState);
        Assert.Equal(CompareState.OnlyHere, view.LeftPane.Items.Single(i => i.Name == "so-esquerda.png").CompareState);
        Assert.Equal(CompareState.OnlyHere, view.RightPane.Items.Single(i => i.Name == "so-direita.txt").CompareState);
        Assert.Contains("Só à esquerda: 1", view.CompareSummary);
        view.ShowOnlyDifferences = true;
        Assert.Equal(["so-esquerda.png"], view.LeftPane.Items.Select(i => i.Name));
        view.IsComparing = false;
        Assert.Equal(CompareState.None, view.RightPane.Items[0].CompareState);
    }

    [Fact]
    public async Task Layout_PinnedAndRecent_AreRememberedBetweenSessions()
    {
        var left = Dir("Y1"); var right = Dir("Y2");
        var settings = new MemorySettings();
        var first = Two(left, right, settings);
        await Ready(first, left, right);
        first.RightPane.ViewMode = TransferViewMode.List;
        first.LeftPane.SortDescending = true;
        first.LeftPane.ShowDetails = true;
        first.TogglePin(first.RightPane);
        Assert.True(first.RightPane.IsPinned);
        first.LeftRatio = 0.3;
        first.SaveLayoutNow();

        var second = new TransferViewModel(null, null, null, _organizer, _dialogs, settings, _clipboard);
        await Ready(second, left, right);
        Assert.Equal(TransferViewMode.List, second.RightPane.ViewMode);
        Assert.True(second.LeftPane.SortDescending);
        Assert.True(second.LeftPane.ShowDetails);
        Assert.Equal([right], second.Pinned.Select(p => p.Path));
        Assert.True(second.RightPane.IsPinned);
        Assert.Contains(second.Recent, r => r.Path == left);
        Assert.Equal(0.3, second.LeftRatio, 3);
        second.OpenPlaceCommand.Execute(second.Pinned[0]);                                   // Local abre no painel ativo (esquerdo)
        await TestEnvironment.WaitUntilAsync(() => second.LeftPane.CurrentPath == right);
    }

    [Fact]
    public async Task Pane_FlagsDuplicates_AndShowsDetails()
    {
        var original = Png("Lib2/original.png", 70);
        await _env.Catalog.ImportPathsAsync([original]);
        var card = Dir("Card2");
        System.IO.File.Copy(original, Path.Combine(card, "copia.png"));
        var pane = Pane();
        await pane.NavigateAsync(card);
        await TestEnvironment.WaitUntilAsync(() => pane.Items.Single().IsDuplicate);
        Assert.Contains(original, pane.Items.Single().DuplicateToolTip);
        pane.TypeFilter = TransferPaneViewModel.DuplicatesOnly;
        Assert.Single(pane.Items);

        pane.ShowDetails = true;
        pane.SetSelection(pane.Items);
        await TestEnvironment.WaitUntilAsync(() => pane.Details.Any(d => d.Label == "Duplicata"));
        Assert.Contains(pane.Details, d => d.Label == "Resolução");
    }

    [Fact]
    public async Task Pane_WithoutOrganizer_OrOnOfflineFolder_DisablesOrganizing()
    {
        var plain = new TransferPaneViewModel(dialogs: _dialogs);
        await plain.NavigateAsync(Dir("Z"));
        Assert.False(plain.NewFolderCommand.CanExecute(null));
        var offline = Pane();
        await offline.NavigateAsync(Path.Combine(Root, "nao-existe"));
        Assert.False(offline.NewFolderCommand.CanExecute(null));
        await offline.NavigateAsync(Dir("Z"));
        Assert.True(offline.NewFolderCommand.CanExecute(null));
        Assert.False(offline.RenameCommand.CanExecute(null));
        Assert.False(offline.PasteCommand.CanExecute(null));                                // painel isolado: sem coordenador, sem colar
    }

    /// <summary>Progress&lt;T&gt; posta no contexto; este chama na hora (o teste vê cada relatório).</summary>
    private sealed class SyncProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}
