using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Collections;
using PhotoManager.Application.Metadata;
using PhotoManager.Infrastructure.Files;
using PhotoManager.Infrastructure.Images;
using PhotoManager.Persistence;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

/// <summary>Ambiente isolado (banco, cache e fotos em pasta temporária) com os serviços reais das Fases 1-4.</summary>
public sealed class TestEnvironment : IDisposable
{
    public TestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "PhotoManagerTests", Guid.NewGuid().ToString("N"));
        Photos = Path.Combine(Root, "photos");
        Directory.CreateDirectory(Photos);
        Repository = new SqliteCatalogRepository(Path.Combine(Root, "data", "photomanager.db"));
        Repository.InitializeAsync().GetAwaiter().GetResult();
        var thumbs = new ThumbnailService(Path.Combine(Root, "cache"));
        Thumbnails = thumbs;
        Catalog = new CatalogService(Repository, thumbs, NullLogger<CatalogService>.Instance);
        Organization = new OrganizationService(Repository);
        Collections = new CollectionService(Repository);
        FileOperations = new FileOperationService(Repository);
        MetadataEditing = new MetadataEditService(new PhotoManager.Infrastructure.Metadata.MetadataExtractorReader(), new PhotoManager.Infrastructure.Metadata.CompositeMetadataWriter(new PhotoManager.Infrastructure.Metadata.JpegMetadataWriter(), new PhotoManager.Infrastructure.Metadata.SidecarXmpWriter()), Repository, Repository);
        BatchMetadata = new BatchMetadataService(new PhotoManager.Infrastructure.Metadata.MetadataExtractorReader(), MetadataEditing);
    }

    public string Root { get; }
    public string Photos { get; }
    public SqliteCatalogRepository Repository { get; }
    public IThumbnailService Thumbnails { get; }
    public ICatalogService Catalog { get; }
    public IOrganizationService Organization { get; }
    public ICollectionService Collections { get; }
    public IFileOperationService FileOperations { get; }
    public IMetadataEditService MetadataEditing { get; }
    public IBatchMetadataService BatchMetadata { get; }

    public LibraryViewModel CreateLibrary() => new(Catalog, Thumbnails, Organization, FileOperations, duplicateRepository: null, collectionService: Collections);

    public string CreatePng(string relativePath, byte shade = 120, int width = 64, int height = 48)
    {
        var path = Path.Combine(Photos, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = shade; pixels[i + 1] = (byte)(shade / 2); pixels[i + 2] = 200; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    /// <summary>
    /// JPEG com a metade esquerda vermelha e a direita azul e, se pedido, a tag EXIF de orientação (1-8) num APP1 mínimo.
    /// Os pixels ficam sempre "deitados" (como no sensor): quem lê precisa aplicar a orientação.
    /// </summary>
    public string CreateJpeg(string relativePath, int width = 80, int height = 40, int exifOrientation = 0)
    {
        var path = Path.Combine(Photos, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (x < width / 2) { pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 255; }      // BGRA: vermelho
                else { pixels[i] = 255; pixels[i + 1] = 0; pixels[i + 2] = 0; }                     // azul
                pixels[i + 3] = 255;
            }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var jpeg = stream.ToArray();
        if (exifOrientation is >= 1 and <= 8)
        {
            byte[] app1 =
            [
                0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
                (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,          // cabeçalho TIFF (little-endian), IFD0 no offset 8
                0x01, 0x00,                                                         // 1 entrada
                0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)exifOrientation, 0x00, 0x00, 0x00,   // Orientation (SHORT) = valor
                0x00, 0x00, 0x00, 0x00                                              // sem próximo IFD
            ];
            jpeg = [.. jpeg.Take(2), .. app1, .. jpeg.Skip(2)];
        }
        File.WriteAllBytes(path, jpeg);
        return path;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException();
            await Task.Delay(25);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
