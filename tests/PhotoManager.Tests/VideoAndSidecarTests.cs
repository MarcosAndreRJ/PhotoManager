using System.IO;
using System.Security.Cryptography;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Metadata;
using PhotoManager.Infrastructure.Media;
using PhotoManager.Infrastructure.Metadata;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

public sealed class VideoAndSidecarTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private string CreateVideo(string name, int width = 1920, int height = 1080, double seconds = 75.5)
    {
        var path = Path.GetFullPath(Path.Combine(_env.Photos, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, FakeMp4.Build(width, height, seconds));
        return path;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ---------- formatos e leitura do contêiner ----------

    [Theory]
    [InlineData(".mp4", true)]
    [InlineData("C:\\x\\clip.MOV", true)]
    [InlineData(".mkv", true)]
    [InlineData(".jpg", false)]
    [InlineData(".png", false)]
    [InlineData(".cr2", false)]
    public void MediaFormats_RecognizesVideos(string extension, bool video)
    {
        Assert.Equal(video, MediaFormats.IsVideo(extension));
        Assert.Equal(video, PhotoManager.Domain.Photos.MediaKind.IsVideo(extension));   // as duas listas não podem divergir
        Assert.True(MediaFormats.IsSupported(extension));
    }

    [Fact]
    public void Mp4Info_ReadsSizeDurationAndDate()
    {
        var path = CreateVideo("a.mp4", 1280, 720, 12.5);
        var info = Mp4Info.TryRead(path)!;
        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
        Assert.Equal(12.5, info.DurationSeconds!.Value, 3);
        Assert.NotNull(info.CreatedUtc);
        Assert.True(info.CreatedUtc!.Value.Year >= 2024);
    }

    [Fact]
    public void Mp4Info_ReturnsNullForGarbageOrEmptyFiles_WithoutThrowing()
    {
        var garbage = Path.Combine(_env.Photos, "g.mp4");
        File.WriteAllBytes(garbage, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        Assert.Null(Mp4Info.TryRead(garbage));
        var empty = Path.Combine(_env.Photos, "e.mp4");
        File.WriteAllBytes(empty, []);
        Assert.Null(Mp4Info.TryRead(empty));
        Assert.Null(Mp4Info.TryRead(Path.Combine(_env.Photos, "nao-existe.mp4")));
    }

    // ---------- catálogo ----------

    [Fact]
    public async Task Import_CatalogsVideosAlongsidePhotos_WithDimensionsAndDuration()
    {
        _env.CreatePng("foto.png");
        var video = CreateVideo("sub/clipe.mp4", 1920, 1080, 75.5);

        var result = await _env.Catalog.ImportFolderAsync(_env.Photos);

        Assert.Equal(2, result.Imported);
        var photos = await _env.Catalog.GetPhotosAsync();
        var v = photos.Single(p => p.CurrentPath == video);
        Assert.True(v.IsVideo);
        Assert.Equal(1920, v.Width);
        Assert.Equal(1080, v.Height);
        Assert.Equal(75.5, v.DurationSeconds!.Value, 3);
        Assert.False(photos.Single(p => p.CurrentPath != video).IsVideo);
    }

    [Fact]
    public async Task Import_IgnoresSidecarXmpFiles()
    {
        _env.CreatePng("x.png");
        File.WriteAllText(Path.Combine(_env.Photos, "x.xmp"), "<x/>");
        var result = await _env.Catalog.ImportFolderAsync(_env.Photos);
        Assert.Equal(1, result.Imported);
    }

    [Fact]
    public void Card_ShowsDurationAndDimensionsForVideos()
    {
        var video = new PhotoManager.Domain.Photos.Photo { FileName = "c.mp4", CurrentPath = @"X:\nao\existe\c.mp4", Extension = ".mp4", Width = 1920, Height = 1080, DurationSeconds = 75.5 };
        var card = new PhotoCardViewModel(video);
        Assert.True(card.IsVideo);
        Assert.Equal("1:16", card.DurationText);
        Assert.Contains("1920 × 1080", card.Dimensions);
        Assert.Equal("1:01:05", PhotoCardViewModel.FormatDuration(3665));
        Assert.Equal("–:––", PhotoCardViewModel.FormatDuration(null));

        var noInfo = new PhotoCardViewModel(new PhotoManager.Domain.Photos.Photo { FileName = "d.mkv", CurrentPath = @"X:\d.mkv", Extension = ".mkv" });
        Assert.Equal("Vídeo", noInfo.Dimensions);
        var photo = new PhotoCardViewModel(new PhotoManager.Domain.Photos.Photo { FileName = "p.png", CurrentPath = @"X:\p.png", Extension = ".png", Width = 10, Height = 5 });
        Assert.False(photo.IsVideo);
        Assert.Equal(string.Empty, photo.DurationText);
        Assert.Equal("10 × 5", photo.Dimensions);
    }

    // ---------- sidecar .xmp ----------

    [Theory]
    [InlineData("a.png")]
    [InlineData("a.cr2")]
    [InlineData("a.webp")]
    [InlineData("a.mp4")]
    [InlineData("a.mov")]
    public async Task Sidecar_WritesAndReadsBack_WithoutTouchingTheMediaFile(string name)
    {
        var path = Path.Combine(_env.Photos, name);
        if (name.EndsWith(".mp4") || name.EndsWith(".mov")) File.WriteAllBytes(path, FakeMp4.Build(640, 360, 3));
        else File.WriteAllBytes(path, Enumerable.Range(0, 400).Select(i => (byte)i).ToArray());
        var before = Hash(path);
        var writer = new SidecarXmpWriter();
        Assert.True(writer.CanWrite(path));

        await writer.WriteAsync(path, new MetadataEdit { Title = "Pôr do sol", Description = "Descrição com acentuação", Keywords = ["praia", "sol", "mar"], Author = "Ana", Copyright = "© 2026 Ana" });

        Assert.Equal(before, Hash(path));
        Assert.True(File.Exists(XmpSidecar.PathFor(path)));
        var read = MetadataExtractorReader.Read(path);
        Assert.Equal("Pôr do sol", read.Title);
        Assert.Equal("Descrição com acentuação", read.Description);
        Assert.Equal(["praia", "sol", "mar"], read.Keywords);
        Assert.Equal("Ana", read.Author);
        Assert.Equal("© 2026 Ana", read.Copyright);
        Assert.Contains("XMP (sidecar)", read.Sources);
        Assert.Null(read.Error);
    }

    [Fact]
    public async Task Sidecar_ClearingFieldsRemovesThem_AndKeepsUnrelatedXmpProperties()
    {
        var path = Path.Combine(_env.Photos, "k.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        var sidecar = XmpSidecar.PathFor(path);
        // Sidecar pré-existente de outro programa, com uma propriedade que não é nossa.
        File.WriteAllText(sidecar, "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description rdf:about='' xmlns:xmp='http://ns.adobe.com/xap/1.0/' xmp:Rating='4' xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:title><rdf:Alt><rdf:li xml:lang='x-default'>Antigo</rdf:li></rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end='w'?>");
        var writer = new SidecarXmpWriter();
        Assert.Equal("Antigo", MetadataExtractorReader.Read(path).Title);

        await writer.WriteAsync(path, new MetadataEdit { Title = "Novo", Keywords = ["a"] });
        Assert.Contains("xmp:Rating", File.ReadAllText(sidecar));           // o resto do XMP foi preservado
        await writer.WriteAsync(path, new MetadataEdit());                  // apagar tudo

        var cleared = MetadataExtractorReader.Read(path);
        Assert.Null(cleared.Title);
        Assert.Empty(cleared.Keywords);
        Assert.Contains("xmp:Rating", File.ReadAllText(sidecar));
        Assert.False(File.Exists(sidecar + ".pm-tmp"));
    }

    [Fact]
    public async Task Sidecar_CorruptExistingFile_IsNotOverwritten()
    {
        var path = Path.Combine(_env.Photos, "bad.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        var sidecar = XmpSidecar.PathFor(path);
        File.WriteAllText(sidecar, "isto nao e xml <<<");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SidecarXmpWriter().WriteAsync(path, new MetadataEdit { Title = "x" }));
        Assert.Equal("isto nao e xml <<<", File.ReadAllText(sidecar));
    }

    [Fact]
    public async Task CompositeWriter_UsesJpegForJpegAndSidecarForTheRest()
    {
        var composite = new CompositeMetadataWriter(new JpegMetadataWriter(), new SidecarXmpWriter());
        Assert.True(composite.CanWrite("a.jpg"));
        Assert.True(composite.CanWrite("a.png"));
        Assert.True(composite.CanWrite("a.mp4"));
        Assert.False(composite.CanWrite("a.txt"));
        Assert.False(XmpSidecar.UsesSidecar("a.jpg"));
        Assert.True(MetadataFormats.CanEdit(".cr2") && MetadataFormats.CanEdit(".jpeg") && MetadataFormats.CanEdit(".mkv"));

        var png = _env.CreatePng("w.png");
        await composite.WriteAsync(png, new MetadataEdit { Title = "PNG" });
        Assert.True(File.Exists(XmpSidecar.PathFor(png)));
    }

    // ---------- edição pelo serviço (versões, histórico) ----------

    [Theory]
    [InlineData("png")]
    [InlineData("mp4")]
    public async Task EditService_SavesMetadataForNonJpegAndVideo_WithVersionHistory(string kind)
    {
        var path = kind == "png" ? _env.CreatePng("e.png") : CreateVideo("e.mp4");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photo = (await _env.Catalog.GetPhotosAsync()).Single(p => p.CurrentPath == path);
        var before = Hash(path);

        Assert.True(_env.MetadataEditing.CanEdit(photo));
        var result = await _env.MetadataEditing.SaveAsync(photo, new MetadataEdit { Title = "Título", Keywords = ["k1", "k2"] });

        Assert.True(result.Changed);
        Assert.Equal(1, result.Version);
        Assert.Equal(before, Hash(path));
        var reread = MetadataExtractorReader.Read(path);
        Assert.Equal("Título", reread.Title);
        Assert.Equal(["k1", "k2"], reread.Keywords);
        var history = await _env.MetadataEditing.GetHistoryAsync(photo.Id);
        Assert.Contains(history, h => h.Version == 1);

        var again = await _env.MetadataEditing.SaveAsync(photo, new MetadataEdit { Title = "Título", Keywords = ["k1", "k2"] });
        Assert.False(again.Changed);                                   // sem mudança: nada gravado, sem nova versão
    }

    [Fact]
    public async Task BatchEditor_NowEditsPngAndVideoAsWell()
    {
        var png = _env.CreatePng("b1.png");
        var video = CreateVideo("b2.mp4");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photos = await _env.Catalog.GetPhotosAsync();
        var plan = new BatchMetadataPlan { DescriptionOperation = BatchOperation.Replace, Description = "em lote" };

        var results = await _env.BatchMetadata.ApplyAsync(photos, plan);

        Assert.Equal(2, results.Changed);
        Assert.Equal("em lote", MetadataExtractorReader.Read(png).Description);
        Assert.Equal("em lote", MetadataExtractorReader.Read(video).Description);
    }

    // ---------- operações de arquivo levam o sidecar junto ----------

    [Fact]
    public async Task FileOperations_MoveRenameAndCopy_CarryTheSidecar()
    {
        var path = _env.CreatePng("s1.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photo = (await _env.Catalog.GetPhotosAsync()).Single();
        await _env.MetadataEditing.SaveAsync(photo, new MetadataEdit { Title = "Com sidecar" });
        Assert.True(File.Exists(XmpSidecar.PathFor(path)));

        var dest = Path.Combine(_env.Photos, "dest");
        await _env.FileOperations.MoveAsync(photo, dest);
        var movedPath = Path.Combine(dest, "s1.png");
        Assert.False(File.Exists(XmpSidecar.PathFor(path)));
        Assert.True(File.Exists(XmpSidecar.PathFor(movedPath)));
        Assert.Equal("Com sidecar", MetadataExtractorReader.Read(movedPath).Title);

        await _env.FileOperations.RenameAsync(photo, "novo-nome.png");
        var renamed = Path.Combine(dest, "novo-nome.png");
        Assert.True(File.Exists(XmpSidecar.PathFor(renamed)));
        Assert.False(File.Exists(XmpSidecar.PathFor(movedPath)));

        var copyFolder = Path.Combine(_env.Photos, "copia");
        await _env.FileOperations.CopyAsync(photo, copyFolder, addCopyToCatalog: false);
        Assert.True(File.Exists(XmpSidecar.PathFor(Path.Combine(copyFolder, "novo-nome.png"))));
        Assert.True(File.Exists(XmpSidecar.PathFor(renamed)));          // a cópia não tira o original do lugar
    }

    [Fact]
    public async Task FileOperations_Video_MoveKeepsDurationAndPhotoId()
    {
        var path = CreateVideo("m.mp4", seconds: 9);
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photo = (await _env.Catalog.GetPhotosAsync()).Single();
        await _env.FileOperations.MoveAsync(photo, Path.Combine(_env.Photos, "videos"));

        var after = (await _env.Catalog.GetPhotosAsync()).Single();
        Assert.Equal(photo.Id, after.Id);
        Assert.Equal(9, after.DurationSeconds!.Value, 3);
        Assert.True(File.Exists(Path.Combine(_env.Photos, "videos", "m.mp4")));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Sidebar_HasAVideosSmartList_WithCountAndFilter()
    {
        _env.CreatePng("f1.png");
        CreateVideo("v1.mp4");
        CreateVideo("v2.mov");
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);

        var entry = library.SmartLists.Single(e => e.Key == "videos");
        Assert.Equal(2, entry.Count);
        library.SelectSidebarCommand.Execute(entry);
        Assert.Equal(2, library.Photos.Count);
        Assert.All(library.Photos, c => Assert.True(c.IsVideo));
    }
}
