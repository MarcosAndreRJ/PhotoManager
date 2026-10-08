using System.IO;
using System.Windows;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Collections;
using PhotoManager.Application.Metadata;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

public sealed class ExplorerDragDropTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private string External(string name, byte[]? content = null)
    {
        var folder = Path.Combine(_env.Root, "explorer");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        if (content is null)
        {
            var temp = _env.CreatePng("tmp_" + name);      // só para gerar um PNG válido: não pode ficar na pasta catalogada
            content = File.ReadAllBytes(temp);
            File.Delete(temp);
        }
        File.WriteAllBytes(path, content);
        return path;
    }

    // ---------- plano (função pura) ----------

    private static ExternalDropPlan Plan(string[] paths, string? target, bool ctrl = false, bool shift = false, Func<string, bool>? isDir = null) =>
        ExternalDropPlanner.Plan(paths, target, target is null ? "" : Path.GetFileName(target), ctrl, shift, isDir ?? (p => !Path.HasExtension(p)));

    [Fact]
    public void Planner_OnTheGrid_AddsToTheCatalog_FilesAndFolders_IgnoringUnsupported()
    {
        var plan = Plan([@"D:\x\a.jpg", @"D:\x\b.mp4", @"D:\x\leia.txt", @"D:\x\a.xmp", @"D:\fotos"], null);
        Assert.Equal(ExternalDropAction.AddToCatalog, plan.Action);
        Assert.True(plan.CanDrop);
        Assert.Equal(2, plan.Files.Count);
        Assert.Single(plan.Folders);
        Assert.Equal(1, plan.Unsupported);                               // o .xmp não conta: acompanha a mídia
        Assert.Contains("Adicionar", plan.Message);

        Assert.Equal(ExternalDropAction.Blocked, Plan([@"D:\x\leia.txt"], null).Action);
        Assert.False(Plan([], null).CanDrop);
    }

    [Fact]
    public void Planner_OnAFolder_CopyWithCtrl_MoveWithShift_OtherwiseAsks()
    {
        string[] files = [@"D:\a\1.jpg", @"D:\a\2.png"];
        Assert.Equal(ExternalDropAction.Copy, Plan(files, @"E:\Destino", ctrl: true).Action);
        Assert.Equal(ExternalDropAction.Move, Plan(files, @"E:\Destino", shift: true).Action);
        var ask = Plan(files, @"E:\Destino");
        Assert.Equal(ExternalDropAction.AskCopyOrMove, ask.Action);
        Assert.True(ask.CanDrop);
        Assert.Contains("Copiar 2 arquivos", Plan(files, @"E:\Destino", ctrl: true).Message);
    }

    [Fact]
    public void Planner_OnAFolder_BlocksWholeFolders_AndFilesAlreadyThere()
    {
        Assert.False(Plan([@"D:\pasta"], @"E:\Destino").CanDrop);                        // pastas inteiras só na grade
        var already = Plan([@"E:\Destino\1.jpg"], @"E:\Destino\", ctrl: true);
        Assert.False(already.CanDrop);
        Assert.Contains("já estão", already.Message);
        var mixed = Plan([@"E:\Destino\1.jpg", @"D:\a\2.jpg"], @"E:\Destino", ctrl: true);
        Assert.Equal([@"D:\a\2.jpg"], mixed.Files);
    }

    // ---------- transferência de arquivos ----------

    [Fact]
    public async Task Transfer_CopiesAndMoves_CarryingTheSidecar_AndNeverOverwrites()
    {
        var a = External("a.png");
        var sidecar = XmpSidecar.PathFor(a);
        File.WriteAllText(sidecar, "<x/>");
        var b = External("b.png");
        var dest = Path.Combine(_env.Photos, "alvo");

        var copy = await _env.FileOperations.TransferExternalAsync([a, b], dest, move: false);
        Assert.Equal(2, copy.Created.Count);
        Assert.True(File.Exists(a) && File.Exists(b));                                   // originais ficam
        Assert.True(File.Exists(XmpSidecar.PathFor(Path.Combine(dest, "a.png"))));       // sidecar copiado

        var again = await _env.FileOperations.TransferExternalAsync([a], dest, move: false);
        Assert.Empty(again.Created);
        Assert.Equal([a], again.SkippedExisting);                                        // nome já existe: não sobrescreve

        var moveDest = Path.Combine(_env.Photos, "alvo2");
        var move = await _env.FileOperations.TransferExternalAsync([a, Path.Combine(_env.Root, "nao-existe.png")], moveDest, move: true);
        Assert.Single(move.Created);
        Assert.Single(move.Failed);
        Assert.False(File.Exists(a) || File.Exists(sidecar));                            // moveu o arquivo e o sidecar
        Assert.True(File.Exists(XmpSidecar.PathFor(Path.Combine(moveDest, "a.png"))));
    }

    // ---------- soltar no ViewModel ----------

    [Fact]
    public async Task Drop_OnTheGrid_CatalogsInPlace_WithoutTouchingTheDisk()
    {
        var file = External("g1.png");
        var folder = Path.Combine(_env.Root, "pasta-solta");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "g2.png"), File.ReadAllBytes(file));
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        Assert.Empty(library.Photos);

        var plan = library.PlanExternalDrop([file, folder], null, false, false);
        await library.ExecuteExternalDropAsync(plan);

        Assert.Equal(2, library.Photos.Count);
        Assert.Contains("2 novo(s)", library.StatusText);
        Assert.True(File.Exists(file) && File.Exists(Path.Combine(folder, "g2.png")));   // nada foi movido nem copiado

        await library.ExecuteExternalDropAsync(library.PlanExternalDrop([file], null, false, false));
        Assert.Equal(2, library.Photos.Count);                                          // já catalogado: não duplica
        Assert.Contains("já estava", library.StatusText);
    }

    [Fact]
    public async Task Drop_OnAFolder_CopyKeepsTheOriginal_AndCatalogsTheCopy()
    {
        var source = External("c1.png");
        var dest = Path.Combine(_env.Photos, "Destino");
        Directory.CreateDirectory(dest);
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        var plan = library.PlanExternalDrop([source], new FolderNode(dest, "Destino"), ctrl: true, shift: false);
        Assert.Equal(ExternalDropAction.Copy, plan.Action);
        await library.ExecuteExternalDropAsync(plan);

        Assert.True(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(dest, "c1.png")));
        Assert.Equal([Path.Combine(dest, "c1.png")], library.Photos.Select(c => c.Photo.CurrentPath));
        Assert.Contains("copiado", library.StatusText);
    }

    [Fact]
    public async Task Drop_OnAFolder_AsksWhenThereIsNoModifier_AndCancelDoesNothing()
    {
        var source = External("q1.png");
        var dest = Path.Combine(_env.Photos, "Alvo");
        Directory.CreateDirectory(dest);
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var plan = library.PlanExternalDrop([source], new FolderNode(dest, "Alvo"), false, false);
        Assert.Equal(ExternalDropAction.AskCopyOrMove, plan.Action);

        library.AskCopyOrMove = _ => null;                                               // Cancelar
        await library.ExecuteExternalDropAsync(plan);
        Assert.False(File.Exists(Path.Combine(dest, "q1.png")));

        library.AskCopyOrMove = _ => ExternalDropAction.Move;                            // Mover
        await library.ExecuteExternalDropAsync(plan);
        Assert.True(File.Exists(Path.Combine(dest, "q1.png")));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task Drop_MoveOfAFileAlreadyInTheCatalog_KeepsThePhotoIdAndMetadata()
    {
        var path = _env.CreatePng("m1.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var before = library.Photos.Single().Photo;
        var id = before.Id;
        await _env.MetadataEditing.SaveAsync(before, new MetadataEdit { Title = "Mantido" });
        var dest = Path.Combine(_env.Photos, "Novo");
        Directory.CreateDirectory(dest);

        var plan = library.PlanExternalDrop([path], new FolderNode(dest, "Novo"), ctrl: false, shift: true);
        await library.ExecuteExternalDropAsync(plan);

        var card = library.Photos.Single();
        Assert.Equal(id, card.Photo.Id);                                                 // a mesma linha acompanhou o arquivo
        Assert.Equal(Path.Combine(dest, "m1.png"), card.Photo.CurrentPath);
        Assert.True(File.Exists(XmpSidecar.PathFor(card.Photo.CurrentPath)));
    }

    [Fact]
    public async Task Drop_NameConflict_KeepsTheExistingFile_AndReportsIt()
    {
        var source = External("dup.png");
        var dest = Path.Combine(_env.Photos, "Dest");
        Directory.CreateDirectory(dest);
        File.WriteAllBytes(Path.Combine(dest, "dup.png"), [1, 2, 3]);
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        await library.ExecuteExternalDropAsync(library.PlanExternalDrop([source], new FolderNode(dest, "Dest"), ctrl: true, shift: false));

        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(dest, "dup.png")));      // não sobrescreveu
        Assert.Contains("já existiam", library.StatusText);
    }

    // ---------- arrastar PARA o Explorer ----------

    [Fact]
    public async Task DragOut_CarriesTheFilesAndSidecars_AndPrefersCopy_WhileKeepingTheInternalIds()
    {
        var withSidecar = _env.CreatePng("o1.png");
        var plain = _env.CreatePng("o2.png");
        var gone = _env.CreatePng("o3.png");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var photos = library.Photos.Select(c => c.Photo).ToList();
        await _env.MetadataEditing.SaveAsync(photos.Single(p => p.CurrentPath == withSidecar), new MetadataEdit { Title = "t" });
        File.Delete(gone);
        photos = (await _env.Catalog.GetPhotosAsync()).ToList();

        var data = PhotoDragData.Create(photos.Select(p => p.Id).ToList(), photos);

        var files = (string[])data.GetData(DataFormats.FileDrop)!;
        Assert.Contains(withSidecar, files);
        Assert.Contains(XmpSidecar.PathFor(withSidecar), files);                         // o sidecar vai junto
        Assert.Contains(plain, files);
        Assert.DoesNotContain(gone, files);                                              // arquivo ausente não vai
        Assert.Equal(photos.Count, ((long[])data.GetData(CollectionDragDropFormats.PhotoIds)!).Length);   // coleções/pastas internas continuam funcionando

        var effect = BitConverter.ToInt32(((MemoryStream)data.GetData(PhotoDragData.PreferredDropEffectFormat)!).ToArray());
        Assert.Equal((int)DragDropEffects.Copy, effect);
    }

    [Fact]
    public void DragOut_WithOnlyMissingFiles_OmitsTheFileFormat()
    {
        var photo = new PhotoManager.Domain.Photos.Photo { Id = 5, CurrentPath = @"X:\nada.png", Extension = ".png", IsMissing = true };
        var data = PhotoDragData.Create([5], [photo]);
        Assert.False(data.GetDataPresent(DataFormats.FileDrop));
        Assert.True(data.GetDataPresent(CollectionDragDropFormats.PhotoIds));
    }
}
