using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoManager.Infrastructure.Metadata;

namespace PhotoManager.Tests;

public sealed class MetadataReaderTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private static BitmapSource Pixels() =>
        BitmapSource.Create(32, 24, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 24 * 4], 32 * 4);

    private string CreateJpeg(string name, Action<BitmapMetadata>? configure = null)
    {
        var path = Path.Combine(_env.Photos, name);
        var metadata = new BitmapMetadata("jpg");
        configure?.Invoke(metadata);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Pixels(), null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. payload];
    }

    private static byte[] IptcDataSet(byte number, string value)
    {
        var data = Encoding.UTF8.GetBytes(value);
        return [0x1C, 0x02, number, (byte)(data.Length >> 8), (byte)data.Length, .. data];
    }

    /// <summary>Insere segmentos XMP (APP1) e IPTC (APP13) logo após o SOI de um JPEG existente.</summary>
    private static void InjectXmpAndIptc(string path, string xmpXml, byte[] iptc)
    {
        var xmpPayload = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0").Concat(Encoding.UTF8.GetBytes(xmpXml)).ToArray();
        var iptcPadded = iptc.Length % 2 == 0 ? iptc : [.. iptc, (byte)0];
        var photoshop = Encoding.ASCII.GetBytes("Photoshop 3.0\0").Concat(Encoding.ASCII.GetBytes("8BIM"))
            .Concat(new byte[] { 0x04, 0x04, 0x00, 0x00 })
            .Concat(new byte[] { (byte)(iptc.Length >> 24), (byte)(iptc.Length >> 16), (byte)(iptc.Length >> 8), (byte)iptc.Length })
            .Concat(iptcPadded).ToArray();
        var original = File.ReadAllBytes(path);
        File.WriteAllBytes(path, [.. original[..2], .. Segment(0xE1, xmpPayload), .. Segment(0xED, photoshop), .. original[2..]]);
    }

    [Fact]
    public void ReadsExifFields_WrittenByTheEncoder()
    {
        var path = CreateJpeg("exif.jpg", m =>
        {
            m.CameraManufacturer = "Canon";
            m.CameraModel = "EOS R5";
            m.DateTaken = "2026-01-18T18:42:15";
            m.Author = new System.Collections.ObjectModel.ReadOnlyCollection<string>(["Marcos"]);
            m.Copyright = "© 2026 Marcos";
            m.Title = "Pôr do sol";
            m.Keywords = new System.Collections.ObjectModel.ReadOnlyCollection<string>(["praia", "sol"]);
        });
        var result = MetadataExtractorReader.Read(path);
        Assert.Null(result.Error);
        Assert.Equal("Canon EOS R5", result.Camera);
        Assert.Equal(new DateTime(2026, 1, 18, 18, 42, 15), result.DateTaken);
        Assert.Contains("EXIF", result.Sources);
        Assert.Equal("© 2026 Marcos", result.Copyright);
        Assert.Equal("Pôr do sol", result.Title);
        Assert.Equal("Marcos", result.Author);
        Assert.Equal(["praia", "sol"], result.Keywords.Order());
    }

    [Fact]
    public void ReadsIsoFromExifSubIfd()
    {
        var path = CreateJpeg("iso.jpg", m => m.SetQuery("/app1/ifd/exif/{ushort=34855}", (ushort)400));
        Assert.Equal(400, MetadataExtractorReader.Read(path).Iso);
    }

    [Fact]
    public void ReadsIptcAndXmp_AndXmpTakesPriority()
    {
        var path = CreateJpeg("iptc-xmp.jpg");
        const string xmp = """
            <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Título XMP</rdf:li></rdf:Alt></dc:title>
            <dc:subject><rdf:Bag><rdf:li>cidade</rdf:li><rdf:li>noite</rdf:li></rdf:Bag></dc:subject>
            <dc:creator><rdf:Seq><rdf:li>Autor XMP</rdf:li></rdf:Seq></dc:creator>
            </rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end="w"?>
            """;
        byte[] iptc = [.. IptcDataSet(5, "Título IPTC"), .. IptcDataSet(25, "noite"), .. IptcDataSet(25, "porto"), .. IptcDataSet(80, "Autor IPTC"), .. IptcDataSet(116, "© IPTC"), .. IptcDataSet(120, "Legenda IPTC")];
        InjectXmpAndIptc(path, xmp, iptc);

        var result = MetadataExtractorReader.Read(path);
        Assert.Null(result.Error);
        Assert.Contains("XMP", result.Sources);
        Assert.Contains("IPTC", result.Sources);
        Assert.Equal("Título XMP", result.Title);
        Assert.Equal("Autor XMP", result.Author);
        Assert.Equal("© IPTC", result.Copyright);           // só existe no IPTC
        Assert.Equal("Legenda IPTC", result.Description);   // só existe no IPTC
        Assert.Equal(["cidade", "noite"], result.Keywords.Order());  // XMP tem prioridade; não mistura com o IPTC
    }

    [Fact]
    public void FileWithoutMetadata_ReturnsEmptyFieldsWithoutError()
    {
        var result = MetadataExtractorReader.Read(_env.CreatePng("plain.png"));
        Assert.Null(result.Error);
        Assert.Null(result.Title);
        Assert.Empty(result.Keywords);
        Assert.False(result.HasExif);
        Assert.False(result.HasGps);
    }

    [Fact]
    public void CorruptOrMissingFile_ReturnsErrorMessageInsteadOfThrowing()
    {
        var corrupt = Path.Combine(_env.Photos, "bad.jpg");
        File.WriteAllText(corrupt, "isto não é uma imagem");
        Assert.NotNull(MetadataExtractorReader.Read(corrupt).Error);
        Assert.NotNull(MetadataExtractorReader.Read(Path.Combine(_env.Photos, "nope.jpg")).Error);
    }

    [Fact]
    public void ReadingDoesNotModifyTheFile()
    {
        var path = CreateJpeg("ro.jpg", m => m.CameraModel = "X");
        var before = File.ReadAllBytes(path);
        MetadataExtractorReader.Read(path);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
