using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Directory = System.IO.Directory;
using MetadataExtractor;
using MetadataExtractor.Formats.Iptc;
using PhotoManager.Application.Metadata;
using PhotoManager.Infrastructure.Metadata;

namespace PhotoManager.Tests;

public sealed class MetadataWriterTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly JpegMetadataWriter _writer = new();
    public void Dispose() => _env.Dispose();

    private string CreateJpeg(string name, Action<BitmapMetadata>? configure = null)
    {
        var path = Path.Combine(_env.Photos, name);
        var metadata = new BitmapMetadata("jpg");
        configure?.Invoke(metadata);
        var pixels = new byte[48 * 32 * 4];
        new Random(3).NextBytes(pixels);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(48, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 48 * 4), null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static byte[] Segment(byte marker, byte[] payload) => [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];
    private static byte[] Ds(int record, int number, string value) { var d = Encoding.UTF8.GetBytes(value); return [0x1C, (byte)record, (byte)number, (byte)(d.Length >> 8), (byte)d.Length, .. d]; }

    private static void Inject(string path, string xmp, byte[] iptc)
    {
        var xmpPayload = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0").Concat(Encoding.UTF8.GetBytes(xmp)).ToArray();
        var ps = Encoding.ASCII.GetBytes("Photoshop 3.0\0").Concat("8BIM"u8.ToArray()).Concat(new byte[] { 4, 4, 0, 0 })
            .Concat(new byte[] { 0, 0, (byte)(iptc.Length >> 8), (byte)iptc.Length }).Concat(iptc).Concat(iptc.Length % 2 == 1 ? new byte[] { 0 } : []).ToArray();
        var original = File.ReadAllBytes(path);
        File.WriteAllBytes(path, [.. original[..2], .. Segment(0xE1, xmpPayload), .. Segment(0xED, ps), .. original[2..]]);
    }

    private static MetadataEdit Edit(string? title = "Pôr do sol", string? description = "Vista da cidade — água & luz", string[]? keywords = null, string? author = "Marcos André", string? copyright = "© 2026 Marcos André") =>
        new() { Title = title, Description = description, Keywords = keywords ?? ["cidade", "pôr do sol", "drone"], Author = author, Copyright = copyright };

    private static byte[] ScanData(byte[] jpeg)
    {
        var layout = JpegMetadataWriter.Parse(jpeg);
        return jpeg[layout.ScanStart..];
    }

    [Fact]
    public async Task WritesAllFields_AndPreservesExifAndPixelsByteForByte()
    {
        var path = CreateJpeg("a.jpg", m => { m.CameraManufacturer = "Canon"; m.CameraModel = "EOS R5"; m.DateTaken = "2026-01-18T18:42:15"; });
        var before = File.ReadAllBytes(path);
        await _writer.WriteAsync(path, Edit());

        var after = File.ReadAllBytes(path);
        var result = MetadataExtractorReader.Read(path);
        Assert.Equal("Pôr do sol", result.Title);
        Assert.Equal("Vista da cidade — água & luz", result.Description);
        Assert.Equal(["cidade", "pôr do sol", "drone"], result.Keywords);
        Assert.Equal("Marcos André", result.Author);
        Assert.Equal("© 2026 Marcos André", result.Copyright);
        Assert.Equal("Canon EOS R5", result.Camera);
        Assert.Equal(new DateTime(2026, 1, 18, 18, 42, 15), result.DateTaken);
        Assert.Equal(ScanData(before), ScanData(after));
        foreach (var s in JpegMetadataWriter.Parse(before).Segments.Where(s => s.Marker is 0xE0 or 0xDB or 0xC0 or 0xC4 || (s.Marker == 0xE1 && before.AsSpan(s.PayloadStart, 4).SequenceEqual("Exif"u8)))) // APP0/EXIF, tabelas e quadro: idênticos
            Assert.True(after.AsSpan().IndexOf(before.AsSpan(s.Start, s.Length)) >= 0, $"segmento 0x{s.Marker:X2} alterado");
        Assert.Contains("XMP", result.Sources); Assert.Contains("IPTC", result.Sources);
    }

    [Fact]
    public async Task WritesIptcAsUtf8_AlongsideXmp()
    {
        var path = CreateJpeg("iptc.jpg");
        await _writer.WriteAsync(path, Edit(keywords: ["ação", "água"]));
        var directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(path);
        var iptc = directories.OfType<IptcDirectory>().Single();
        Assert.Equal("Pôr do sol", iptc.GetString(IptcDirectory.TagObjectName));
        Assert.Equal("Marcos André", iptc.GetString(IptcDirectory.TagByLine));
        Assert.Equal("© 2026 Marcos André", iptc.GetString(IptcDirectory.TagCopyrightNotice));
        Assert.Equal(["ação", "água"], iptc.GetStringValueArray(IptcDirectory.TagKeywords)!.Select(v => v.ToString()));
    }

    [Fact]
    public async Task PreservesUnrelatedXmpPropertiesAndIptcDatasets_AndReplacesOldValues()
    {
        var path = CreateJpeg("existing.jpg");
        const string xmp = """
            <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/" photoshop:City="Rio">
            <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Antigo</rdf:li></rdf:Alt></dc:title>
            <dc:subject><rdf:Bag><rdf:li>velha</rdf:li></rdf:Bag></dc:subject>
            </rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end="w"?>
            """;
        Inject(path, xmp, [.. Ds(2, 5, "Antigo"), .. Ds(2, 25, "velha"), .. Ds(2, 90, "Rio de Janeiro"), .. Ds(2, 101, "Brasil")]);

        await _writer.WriteAsync(path, Edit());

        var bytes = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("photoshop:City=\"Rio\"", text);                   // XMP não relacionado preservado
        Assert.DoesNotContain("velha", text);                              // palavra-chave antiga removida do XMP e do IPTC
        var iptc = MetadataExtractor.ImageMetadataReader.ReadMetadata(path).OfType<IptcDirectory>().Single();
        Assert.Equal("Rio de Janeiro", iptc.GetString(IptcDirectory.TagCity));
        Assert.Equal("Brasil", iptc.GetString(IptcDirectory.TagCountryOrPrimaryLocationName));
        Assert.Equal("Pôr do sol", MetadataExtractorReader.Read(path).Title);
    }

    [Fact]
    public async Task ClearingFields_RemovesThemFromXmpAndIptc()
    {
        var path = CreateJpeg("clear.jpg");
        await _writer.WriteAsync(path, Edit());
        await _writer.WriteAsync(path, new MetadataEdit());
        var result = MetadataExtractorReader.Read(path, includeExifTextFallback: false);
        Assert.Null(result.Title); Assert.Null(result.Description); Assert.Null(result.Author); Assert.Null(result.Copyright);
        Assert.Empty(result.Keywords);
    }

    [Fact]
    public async Task Failure_LeavesOriginalUntouched_AndNoTemporaryFiles()
    {
        var path = CreateJpeg("big.jpg");
        var before = File.ReadAllBytes(path);
        var tooBig = Edit(description: new string('x', 70000));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _writer.WriteAsync(path, tooBig));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_env.Photos, "*.pm-*"));

        var notJpeg = _env.CreatePng("x.png");
        await Assert.ThrowsAsync<NotSupportedException>(() => _writer.WriteAsync(notJpeg, Edit()));
        var corrupt = Path.Combine(_env.Photos, "bad.jpg");
        File.WriteAllText(corrupt, "não sou jpeg");
        await Assert.ThrowsAsync<InvalidDataException>(() => _writer.WriteAsync(corrupt, Edit()));
        Assert.Equal("não sou jpeg", File.ReadAllText(corrupt));
    }

    [Fact]
    public async Task RefusesToWrite_WhenFileChangesDuringEdit_Simulated_ByLockedFile()
    {
        var path = CreateJpeg("lock.jpg");
        var before = File.ReadAllBytes(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => _writer.WriteAsync(path, Edit()));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_env.Photos, "*.pm-*"));
    }

    [Fact]
    public async Task ImageStillDecodes_AfterWrite()
    {
        var path = CreateJpeg("decode.jpg");
        await _writer.WriteAsync(path, Edit());
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal((48, 32), (frame.PixelWidth, frame.PixelHeight));
    }

    [Fact]
    public void CanWrite_OnlyJpeg()
    {
        Assert.True(_writer.CanWrite("a.JPG")); Assert.True(_writer.CanWrite("a.jpeg"));
        Assert.False(_writer.CanWrite("a.png")); Assert.False(_writer.CanWrite("a.CR2")); Assert.False(_writer.CanWrite("a.webp"));
    }
}
