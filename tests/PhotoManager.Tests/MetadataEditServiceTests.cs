using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoManager.Application.Metadata;
using PhotoManager.Infrastructure.Metadata;

namespace PhotoManager.Tests;

public sealed class MetadataEditServiceTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private async Task<Domain.Photos.Photo> ImportedJpegAsync()
    {
        var path = Path.Combine(_env.Photos, "a.jpg");
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 32 * 4], 32 * 4), null, new BitmapMetadata("jpg") { CameraModel = "X1" }, null));
        using (var stream = File.Create(path)) encoder.Save(stream);
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        return (await _env.Catalog.GetPhotosAsync()).Single();
    }

    [Fact]
    public async Task Save_WritesFile_UpdatesCatalog_AndVersionsIncrementOnlyOnRealChanges()
    {
        var photo = await ImportedJpegAsync();
        var sizeBefore = photo.FileSize;
        var service = _env.MetadataEditing;
        Assert.True(service.CanEdit(photo));

        var first = await service.SaveAsync(photo, new MetadataEdit { Title = "Um", Keywords = ["a", "b"] });
        Assert.True(first.Changed); Assert.Equal(1, first.Version);
        Assert.Equal(["Título", "Palavras-chave"], first.ChangedFields);
        Assert.NotEqual(sizeBefore, photo.FileSize);

        var same = await service.SaveAsync(photo, new MetadataEdit { Title = " Um ", Keywords = ["a", "b", "A"] }); // só espaços/duplicata: nada mudou
        Assert.False(same.Changed); Assert.Equal(1, same.Version);

        var second = await service.SaveAsync(photo, new MetadataEdit { Title = "Dois", Keywords = ["a", "b"], Author = "Ana" });
        Assert.Equal(2, second.Version);

        var reloaded = (await _env.Catalog.GetPhotosAsync()).Single();
        Assert.Equal(2, reloaded.MetadataVersion);
        Assert.Equal(photo.FileSize, reloaded.FileSize);
        Assert.Equal("Dois", MetadataExtractorReader.Read(photo.CurrentPath).Title);
        Assert.Equal("X1", MetadataExtractorReader.Read(photo.CurrentPath).Camera);

        var history = await service.GetHistoryAsync(photo.Id);
        Assert.Equal([2, 1, 0], history.Select(h => h.Version));
        Assert.Equal("Original", history[2].Note);
        Assert.Null(history[2].Values.Title);
        Assert.Equal("Um", history[1].Values.Title);
        Assert.Equal("Título, Autor", history[0].Note);
    }

    [Fact]
    public async Task Save_Fails_ForMissingFiles_WithoutVersioning()
    {
        var path = _env.CreatePng("p.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        File.Delete(path);
        var png = (await _env.Catalog.GetPhotosAsync()).Single();   // recarregar marca o arquivo como ausente
        Assert.False(_env.MetadataEditing.CanEdit(png));
        await Assert.ThrowsAsync<NotSupportedException>(() => _env.MetadataEditing.SaveAsync(png, new MetadataEdit { Title = "x" }));
        Assert.Equal(0, png.MetadataVersion);
        Assert.Empty(await _env.MetadataEditing.GetHistoryAsync(png.Id));
    }

    [Fact]
    public async Task FailedWrite_DoesNotCreateVersion_OrChangeFile()
    {
        var photo = await ImportedJpegAsync();
        var before = File.ReadAllBytes(photo.CurrentPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _env.MetadataEditing.SaveAsync(photo, new MetadataEdit { Description = new string('x', 70000) }));
        Assert.Equal(before, File.ReadAllBytes(photo.CurrentPath));
        Assert.Equal(0, photo.MetadataVersion);
    }
}
