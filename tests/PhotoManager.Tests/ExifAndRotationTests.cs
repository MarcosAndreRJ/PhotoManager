using System.IO;
using System.Windows.Media.Imaging;
using PhotoManager.Application.Catalog;
using PhotoManager.Domain.Photos;
using PhotoManager.Infrastructure.Images;
using PhotoManager.Infrastructure.Media;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

public sealed class ExifAndRotationTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    /// <summary>Cor do pixel (BGRA) numa posição relativa da imagem.</summary>
    private static (byte B, byte G, byte R) PixelAt(BitmapSource image, double relativeX, double relativeY)
    {
        var converted = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var x = Math.Clamp((int)(relativeX * converted.PixelWidth), 0, converted.PixelWidth - 1);
        var y = Math.Clamp((int)(relativeY * converted.PixelHeight), 0, converted.PixelHeight - 1);
        var pixel = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[0], pixel[1], pixel[2]);
    }

    private static bool IsRed((byte B, byte G, byte R) p) => p.R > 180 && p.B < 90;
    private static bool IsBlue((byte B, byte G, byte R) p) => p.B > 180 && p.R < 90;

    [Theory]
    [InlineData(0, 1)]            // sem EXIF
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(6, 6)]
    [InlineData(8, 8)]
    public void ExifOrientation_ReadsTheTag(int written, int expected) =>
        Assert.Equal(expected, ExifOrientation.Read(_env.CreateJpeg($"o{written}.jpg", exifOrientation: written)));

    [Fact]
    public void ExifOrientation_IgnoresNonJpegAndBrokenFiles()
    {
        Assert.Equal(1, ExifOrientation.Read(_env.CreatePng("p.png")));
        var broken = Path.Combine(_env.Photos, "quebrado.jpg");
        File.WriteAllBytes(broken, [1, 2, 3]);
        Assert.Equal(1, ExifOrientation.Read(broken));
        Assert.Equal(1, ExifOrientation.Read(Path.Combine(_env.Photos, "nao-existe.jpg")));
        Assert.True(ExifOrientation.SwapsDimensions(6) && ExifOrientation.SwapsDimensions(5) && !ExifOrientation.SwapsDimensions(3));
    }

    [Theory]
    [InlineData(1, 80, 40)]
    [InlineData(2, 80, 40)]
    [InlineData(3, 80, 40)]
    [InlineData(5, 40, 80)]
    [InlineData(6, 40, 80)]
    [InlineData(7, 40, 80)]
    [InlineData(8, 40, 80)]
    public void ImageLoader_ReportsSizeAndDecodesPixelsAsDisplayed(int orientation, int expectedWidth, int expectedHeight)
    {
        var path = _env.CreateJpeg($"d{orientation}.jpg", 80, 40, orientation);
        Assert.Equal((expectedWidth, expectedHeight), ImageLoader.ReadSize(path));

        var preview = ImageLoader.LoadPreview(path, 0)!;
        Assert.Equal((expectedWidth, expectedHeight), (preview.PixelWidth, preview.PixelHeight));
        var frame = ImageLoader.LoadFrame(path);
        Assert.Equal((expectedWidth, expectedHeight), (frame.PixelWidth, frame.PixelHeight));
        var full = ImageLoader.LoadFull(path)!;
        Assert.Equal((expectedWidth, expectedHeight), (full.PixelWidth, full.PixelHeight));
    }

    [Fact]
    public void ImageLoader_AppliesTheRightTransformForEachOrientation()
    {
        // Original (deitado): esquerda vermelha, direita azul.
        // 6 = girar 90° horário: a esquerda vai para o TOPO.   8 = 270° horário: a esquerda vai para BAIXO.
        var rotated90 = ImageLoader.LoadPreview(_env.CreateJpeg("r6.jpg", exifOrientation: 6), 0)!;
        Assert.True(IsRed(PixelAt(rotated90, 0.5, 0.15)) && IsBlue(PixelAt(rotated90, 0.5, 0.85)));

        var rotated270 = ImageLoader.LoadPreview(_env.CreateJpeg("r8.jpg", exifOrientation: 8), 0)!;
        Assert.True(IsBlue(PixelAt(rotated270, 0.5, 0.15)) && IsRed(PixelAt(rotated270, 0.5, 0.85)));

        var upsideDown = ImageLoader.LoadPreview(_env.CreateJpeg("r3.jpg", exifOrientation: 3), 0)!;   // 180°: esquerda ↔ direita
        Assert.True(IsBlue(PixelAt(upsideDown, 0.15, 0.5)) && IsRed(PixelAt(upsideDown, 0.85, 0.5)));

        var mirrored = ImageLoader.LoadPreview(_env.CreateJpeg("r2.jpg", exifOrientation: 2), 0)!;      // espelho horizontal
        Assert.True(IsBlue(PixelAt(mirrored, 0.15, 0.5)) && IsRed(PixelAt(mirrored, 0.85, 0.5)));

        var normal = ImageLoader.LoadPreview(_env.CreateJpeg("r1.jpg", exifOrientation: 1), 0)!;
        Assert.True(IsRed(PixelAt(normal, 0.15, 0.5)) && IsBlue(PixelAt(normal, 0.85, 0.5)));
    }

    [Fact]
    public async Task Thumbnail_IsGeneratedUpright_ForARotatedJpeg()
    {
        var path = _env.CreateJpeg("t.jpg", 80, 40, exifOrientation: 6);
        var thumb = await _env.Thumbnails.GetOrCreateAsync(1, path);
        var decoded = BitmapFrame.Create(new Uri(thumb!), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.True(decoded.PixelHeight > decoded.PixelWidth);                    // retrato, não deitada
        Assert.True(IsRed(PixelAt(decoded, 0.5, 0.15)));
    }

    [Fact]
    public async Task Import_StoresDimensionsAsDisplayed_AndMarksTheRevision()
    {
        var path = _env.CreateJpeg("celular.jpg", 80, 40, exifOrientation: 6);
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photo = (await _env.Catalog.GetPhotosAsync()).Single(p => p.CurrentPath == path);

        Assert.Equal((40, 80), (photo.Width, photo.Height));
        Assert.Equal(PhotoOrientation.Portrait, photo.Orientation);
        Assert.Equal(CatalogService.CurrentMediaInfoRevision, photo.MediaInfoRevision);
    }

    [Fact]
    public async Task OldCatalogRows_AreCorrectedOnce_AndOnlyRotatedOnesNeedANewThumbnail()
    {
        var rotated = _env.CreateJpeg("a.jpg", 80, 40, exifOrientation: 6);
        var normal = _env.CreateJpeg("b.jpg", 80, 40, exifOrientation: 1);
        var png = _env.CreatePng("c.png", width: 60, height: 30);
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photos = (await _env.Catalog.GetPhotosAsync()).ToList();
        // Simula o catálogo antigo: pixels brutos e revisão 0.
        foreach (var photo in photos) { photo.MediaInfoRevision = 0; if (photo.CurrentPath == rotated) { photo.Width = 80; photo.Height = 40; } await _env.Repository.UpdateMediaInfoAsync(photo); }
        photos = (await _env.Catalog.GetPhotosAsync()).ToList();

        var regenerate = await _env.Catalog.RefreshMediaInfoAsync(photos);

        Assert.Equal([rotated], regenerate.Select(p => p.CurrentPath));
        var after = (await _env.Catalog.GetPhotosAsync()).ToDictionary(p => p.CurrentPath);
        Assert.Equal((40, 80), (after[rotated].Width, after[rotated].Height));
        Assert.Equal((80, 40), (after[normal].Width, after[normal].Height));
        Assert.Equal(CatalogService.CurrentMediaInfoRevision, after[rotated].MediaInfoRevision);
        Assert.Equal(CatalogService.CurrentMediaInfoRevision, after[normal].MediaInfoRevision);
        Assert.Equal(0, after[png].MediaInfoRevision);                               // PNG não tem orientação EXIF a conferir

        var again = await _env.Catalog.RefreshMediaInfoAsync((await _env.Catalog.GetPhotosAsync()).ToList());
        Assert.Empty(again);                                                         // não repete
        Assert.Equal((40, 80), ((await _env.Catalog.GetPhotosAsync()).Single(p => p.CurrentPath == rotated) is var p2 ? (p2.Width, p2.Height) : (0, 0)));
    }

    // ---------- vídeo: rotação do contêiner ----------

    [Theory]
    [InlineData(0, 0, 1920, 1080)]
    [InlineData(90, 90, 1080, 1920)]
    [InlineData(180, 180, 1920, 1080)]
    [InlineData(270, 270, 1080, 1920)]
    public void Mp4Info_ReportsTheContainerRotation(int written, int expected, int width, int height)
    {
        var path = Path.Combine(_env.Photos, $"v{written}.mp4");
        File.WriteAllBytes(path, FakeMp4.Build(1920, 1080, 5, rotationDegrees: written));
        var info = Mp4Info.TryRead(path)!;
        Assert.Equal(expected, info.RotationDegrees);
        Assert.Equal((width, height), (info.Width, info.Height));
    }

    [Fact]
    public async Task Video_ImportAndRefresh_StoreTheAutoRotation()
    {
        var path = Path.GetFullPath(Path.Combine(_env.Photos, "drone.mp4"));
        File.WriteAllBytes(path, FakeMp4.Build(1920, 1080, 8, rotationDegrees: 90));
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photo = (await _env.Catalog.GetPhotosAsync()).Single();
        Assert.Equal((90, 1080, 1920), (photo.AutoRotation, photo.Width, photo.Height));
        Assert.Equal(PhotoOrientation.Portrait, photo.Orientation);

        // Item da versão anterior (sem AutoRotation, revisão 1): é relido e passa a pedir miniatura nova.
        photo.AutoRotation = 0; photo.MediaInfoRevision = 1; photo.Width = 1920; photo.Height = 1080;
        await _env.Repository.UpdateMediaInfoAsync(photo);
        var regenerate = await _env.Catalog.RefreshMediaInfoAsync((await _env.Catalog.GetPhotosAsync()).ToList());
        Assert.Single(regenerate);
        var fixedPhoto = (await _env.Catalog.GetPhotosAsync()).Single();
        Assert.Equal((90, 1080, 1920, CatalogService.CurrentMediaInfoRevision), (fixedPhoto.AutoRotation, fixedPhoto.Width, fixedPhoto.Height, fixedPhoto.MediaInfoRevision));
    }

    // ---------- giro manual ----------

    [Fact]
    public async Task UserRotation_IsPersisted_FlipsOrientation_AndAddsToTheVideoRotation()
    {
        var path = _env.CreatePng("g.png", width: 80, height: 40);
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photo = (await _env.Catalog.GetPhotosAsync()).Single();
        Assert.Equal(PhotoOrientation.Landscape, photo.Orientation);

        await _env.Catalog.SetUserRotationAsync([photo], 90);
        var reloaded = (await _env.Catalog.GetPhotosAsync()).Single();
        Assert.Equal(90, reloaded.UserRotation);
        Assert.Equal(PhotoOrientation.Portrait, reloaded.Orientation);               // girou 90°: deitada vira em pé

        await _env.Catalog.SetUserRotationAsync([reloaded], 180);
        Assert.Equal(PhotoOrientation.Landscape, (await _env.Catalog.GetPhotosAsync()).Single().Orientation);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _env.Catalog.SetUserRotationAsync([reloaded], 45));
        Assert.True(File.Exists(path));                                              // nada aconteceu com o arquivo

        var video = new Photo { Extension = ".mp4", AutoRotation = 90, UserRotation = 270 };
        Assert.Equal(0, video.VideoDisplayRotation);                                 // 90 + 270
        Assert.Equal(180, new Photo { Extension = ".mp4", AutoRotation = 90, UserRotation = 90 }.VideoDisplayRotation);
    }

    [Fact]
    public async Task LibraryRotate_AppliesToTheSelection_UpdatesCardsAndFilters_AndSurvivesReload()
    {
        _env.CreatePng("l1.png", width: 80, height: 40);
        _env.CreatePng("l2.png", width: 80, height: 40);
        _env.CreatePng("l3.png", width: 80, height: 40);
        var library = _env.CreateLibrary();
        await library.ImportFolderAsync(_env.Photos);
        var cards = library.Photos.OrderBy(c => c.FileName).ToList();
        library.UpdateSelection([cards[0], cards[1]]);

        await library.RotateAsync(cards[0], 90);                                     // o clicado está na seleção: vale para as duas
        Assert.Equal([90.0, 90.0, 0.0], cards.Select(c => c.UserRotation));
        Assert.True(cards[0].IsPortraitOrSquare && cards[1].IsPortraitOrSquare && !cards[2].IsPortraitOrSquare);

        await library.RotateAsync(cards[2], -90);                                    // fora da seleção: só ela, para a esquerda
        Assert.Equal(270.0, cards[2].UserRotation);

        library.OrientationChoice = "Retrato";
        Assert.Equal(3, library.Photos.Count);                                       // as três estão em pé agora

        await library.RotateAsync(cards[0], -90);                                    // a seleção ainda é [0, 1]: as duas voltam a 0
        Assert.Equal([0.0, 0.0, 270.0], cards.Select(c => c.UserRotation));

        var fresh = _env.CreateLibrary();
        await fresh.ImportFolderAsync(_env.Photos);
        Assert.Equal([0.0, 0.0, 270.0], fresh.Photos.OrderBy(c => c.FileName).Select(c => c.UserRotation));
    }

    // ---------- importar caminhos (arquivos e pastas) ----------

    [Fact]
    public async Task ImportPaths_AcceptsFilesAndFolders_IgnoresUnsupportedAndDuplicates()
    {
        var single = _env.CreatePng("avulsa.png");
        _env.CreatePng("pasta/a.png");
        _env.CreatePng("pasta/sub/b.png");
        File.WriteAllText(Path.Combine(_env.Photos, "pasta", "notas.txt"), "x");
        var video = Path.Combine(_env.Photos, "pasta", "c.mp4");
        File.WriteAllBytes(video, FakeMp4.Build(320, 240, 2));

        var result = await _env.Catalog.ImportPathsAsync([single, Path.Combine(_env.Photos, "pasta"), single, Path.Combine(_env.Photos, "nao-existe")]);

        Assert.Equal(4, result.Imported);                                            // avulsa + a + b + c.mp4
        Assert.Equal(4, result.Scanned);
        Assert.Equal(4, (await _env.Catalog.GetPhotosAsync()).Count);

        var again = await _env.Catalog.ImportPathsAsync([Path.Combine(_env.Photos, "pasta")]);
        Assert.Equal(0, again.Imported);                                             // já catalogados
    }
}
