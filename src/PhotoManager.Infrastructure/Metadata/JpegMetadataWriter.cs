using System.Text;
using System.Windows.Media.Imaging;
using PhotoManager.Application.Metadata;
using XmpCore;
using XmpCore.Options;

namespace PhotoManager.Infrastructure.Metadata;

/// <summary>
/// Grava título, descrição, palavras-chave, autor e copyright como XMP (dc:*) e IPTC-IIM em arquivos JPEG.
/// Trabalha no nível de segmentos: só os segmentos XMP (APP1) e IPTC (recurso 0x0404 do APP13) são substituídos;
/// EXIF, ICC, miniatura e os dados da imagem permanecem byte a byte iguais (e isso é verificado antes de substituir o original).
/// Fluxo: arquivo temporário → validação → <c>File.Replace</c> (com backup temporário) → remoção do backup.
/// </summary>
public sealed class JpegMetadataWriter : IMetadataWriter
{
    private static readonly byte[] XmpId = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
    private static readonly byte[] PhotoshopId = Encoding.ASCII.GetBytes("Photoshop 3.0\0");
    private static readonly byte[] ExifId = Encoding.ASCII.GetBytes("Exif\0\0");
    private const int MaxSegmentPayload = 65533;

    public bool CanWrite(string path) => Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    public async Task WriteAsync(string path, MetadataEdit edit, CancellationToken cancellationToken = default)
    {
        if (!CanWrite(path)) throw new NotSupportedException("A gravação de metadados está disponível apenas para arquivos JPEG.");
        await Task.Run(() => WriteCore(path, edit.Normalize(), cancellationToken), cancellationToken);
    }

    private static void WriteCore(string path, MetadataEdit edit, CancellationToken cancellationToken)
    {
        var infoBefore = new FileInfo(path);
        var original = File.ReadAllBytes(path);
        var rewritten = Rewrite(original, edit);
        var temp = path + ".pm-tmp";
        var backup = path + ".pm-bak";
        try
        {
            File.WriteAllBytes(temp, rewritten);
            Validate(temp, original, rewritten, edit);
            cancellationToken.ThrowIfCancellationRequested();

            var infoNow = new FileInfo(path);
            if (infoNow.Length != infoBefore.Length || infoNow.LastWriteTimeUtc != infoBefore.LastWriteTimeUtc)
                throw new IOException("O arquivo foi alterado por outro programa durante a edição. Nada foi gravado; tente novamente.");

            ReplaceWithRetry(temp, path, backup);
            TryDelete(backup);
        }
        finally { TryDelete(temp); }
    }

    private static void ReplaceWithRetry(string temp, string path, string backup)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Replace(temp, path, backup, ignoreMetadataErrors: true); return; }
            catch (IOException) when (attempt < 6 && File.Exists(temp) && File.Exists(path)) { Thread.Sleep(100 * attempt); }
        }
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- estrutura JPEG

    internal sealed record Segment(byte Marker, int Start, int Length)
    {
        public int PayloadStart => Start + 4;
        public int PayloadLength => Length - 4;
    }

    internal sealed record Layout(List<Segment> Segments, int ScanStart);

    internal static Layout Parse(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new InvalidDataException("O arquivo não é um JPEG válido.");
        var segments = new List<Segment>();
        var position = 2;
        while (position + 1 < jpeg.Length)
        {
            if (jpeg[position] != 0xFF) throw new InvalidDataException("Estrutura JPEG inesperada.");
            while (position + 1 < jpeg.Length && jpeg[position + 1] == 0xFF) position++; // bytes de preenchimento
            var marker = jpeg[position + 1];
            if (marker == 0xDA || marker == 0xD9) return new Layout(segments, position);
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { segments.Add(new Segment(marker, position, 2)); position += 2; continue; }
            if (position + 3 >= jpeg.Length) throw new InvalidDataException("JPEG truncado.");
            var length = (jpeg[position + 2] << 8) | jpeg[position + 3];
            if (length < 2 || position + 2 + length > jpeg.Length) throw new InvalidDataException("Segmento JPEG truncado.");
            segments.Add(new Segment(marker, position, 2 + length));
            position += 2 + length;
        }
        throw new InvalidDataException("JPEG sem dados de imagem.");
    }

    private static bool HasPayloadPrefix(byte[] jpeg, Segment segment, byte[] prefix) =>
        segment.PayloadLength >= prefix.Length && jpeg.AsSpan(segment.PayloadStart, prefix.Length).SequenceEqual(prefix);

    private static bool IsXmp(byte[] jpeg, Segment s) => s.Marker == 0xE1 && HasPayloadPrefix(jpeg, s, XmpId);
    private static bool IsPhotoshop(byte[] jpeg, Segment s) => s.Marker == 0xED && HasPayloadPrefix(jpeg, s, PhotoshopId);
    private static bool IsManaged(byte[] jpeg, Segment s) => IsXmp(jpeg, s) || IsPhotoshop(jpeg, s);

    private static byte[] MakeSegment(byte marker, byte[] payload)
    {
        if (payload.Length > MaxSegmentPayload) throw new InvalidOperationException("Os metadados são grandes demais para caber em um segmento JPEG. Reduza descrição ou palavras-chave.");
        var length = payload.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. payload];
    }

    // ---------------------------------------------------------------- reescrita

    internal static byte[] Rewrite(byte[] jpeg, MetadataEdit edit)
    {
        var layout = Parse(jpeg);
        var segments = layout.Segments;
        var xmpIndex = segments.FindIndex(s => IsXmp(jpeg, s));
        var photoshopIndex = segments.FindIndex(s => IsPhotoshop(jpeg, s));

        var newXmp = (xmpIndex >= 0 || !edit.IsEmpty)
            ? MakeSegment(0xE1, [.. XmpId, .. Encoding.UTF8.GetBytes(BuildXmp(xmpIndex >= 0 ? ExistingXmp(jpeg, segments[xmpIndex]) : null, edit))])
            : null;
        var newPhotoshop = (photoshopIndex >= 0 || !edit.IsEmpty)
            ? MakeSegment(0xED, BuildPhotoshop(photoshopIndex >= 0 ? jpeg.AsSpan(segments[photoshopIndex].PayloadStart + PhotoshopId.Length, segments[photoshopIndex].PayloadLength - PhotoshopId.Length).ToArray() : [], edit))
            : null;

        // Onde inserir o que ainda não existe: logo após o último APP0/APP1-Exif inicial.
        var insertAfter = -1;
        for (var i = 0; i < segments.Count; i++)
            if (segments[i].Marker == 0xE0 || (segments[i].Marker == 0xE1 && HasPayloadPrefix(jpeg, segments[i], ExifId))) insertAfter = i;

        using var output = new MemoryStream(jpeg.Length + 4096);
        output.Write(jpeg, 0, 2);
        void EmitMissing()
        {
            if (xmpIndex < 0 && newXmp is not null) output.Write(newXmp);
            if (photoshopIndex < 0 && newPhotoshop is not null) output.Write(newPhotoshop);
        }
        if (insertAfter == -1) EmitMissing();
        for (var i = 0; i < segments.Count; i++)
        {
            if (i == xmpIndex && newXmp is not null) output.Write(newXmp);
            else if (i == photoshopIndex && newPhotoshop is not null) output.Write(newPhotoshop);
            else output.Write(jpeg, segments[i].Start, segments[i].Length);
            if (i == insertAfter) EmitMissing();
        }
        output.Write(jpeg, layout.ScanStart, jpeg.Length - layout.ScanStart);
        return output.ToArray();
    }

    // ---------------------------------------------------------------- XMP

    private static string ExistingXmp(byte[] jpeg, Segment segment) =>
        Encoding.UTF8.GetString(jpeg, segment.PayloadStart + XmpId.Length, segment.PayloadLength - XmpId.Length);

    private static string BuildXmp(string? existing, MetadataEdit edit)
    {
        IXmpMeta meta;
        try { meta = existing is null ? XmpMetaFactory.Create() : XmpMetaFactory.ParseFromString(existing); }
        catch (XmpException ex) { throw new InvalidDataException("O XMP existente no arquivo não pôde ser interpretado; nada foi alterado. " + ex.Message, ex); }

        const string dc = XmpConstants.NsDC;
        foreach (var property in new[] { "title", "description", "subject", "creator", "rights" }) meta.DeleteProperty(dc, property);
        if (edit.Title is not null) meta.SetLocalizedText(dc, "title", null, "x-default", edit.Title);
        if (edit.Description is not null) meta.SetLocalizedText(dc, "description", null, "x-default", edit.Description);
        foreach (var keyword in edit.Keywords) meta.AppendArrayItem(dc, "subject", new PropertyOptions { IsArray = true }, keyword, null);
        if (edit.Author is not null) meta.AppendArrayItem(dc, "creator", new PropertyOptions { IsArray = true, IsArrayOrdered = true }, edit.Author, null);
        if (edit.Copyright is not null) meta.SetLocalizedText(dc, "rights", null, "x-default", edit.Copyright);
        // O serializador antepõe um BOM (U+FEFF) à string; dentro do segmento JPEG isso invalida o arquivo para o decodificador do Windows.
        return XmpMetaFactory.SerializeToString(meta, new SerializeOptions { Padding = 128 }).TrimStart('﻿');
    }

    // ---------------------------------------------------------------- IPTC (recurso 0x0404 do bloco Photoshop)

    private static byte[] BuildPhotoshop(byte[] resources, MetadataEdit edit)
    {
        var result = new List<byte>(PhotoshopId);
        var position = 0;
        var wroteIptc = false;
        while (position + 12 <= resources.Length && resources.AsSpan(position, 4).SequenceEqual("8BIM"u8))
        {
            var id = (resources[position + 4] << 8) | resources[position + 5];
            var nameBytes = ((1 + resources[position + 6]) + 1) & ~1;
            var sizeOffset = position + 6 + nameBytes;
            if (sizeOffset + 4 > resources.Length) throw new InvalidDataException("Bloco Photoshop corrompido.");
            var size = (resources[sizeOffset] << 24) | (resources[sizeOffset + 1] << 16) | (resources[sizeOffset + 2] << 8) | resources[sizeOffset + 3];
            var dataStart = sizeOffset + 4;
            var total = dataStart + size + (size & 1) - position;
            if (size < 0 || position + total > resources.Length + 1) throw new InvalidDataException("Bloco Photoshop corrompido.");
            if (id == 0x0404)
            {
                AppendIptcResource(result, resources.AsSpan(dataStart, size).ToArray(), edit, resources.AsSpan(position + 6, nameBytes).ToArray());
                wroteIptc = true;
            }
            else result.AddRange(resources.AsSpan(position, Math.Min(total, resources.Length - position)).ToArray());
            position += total;
        }
        if (position < resources.Length) result.AddRange(resources.AsSpan(position).ToArray()); // resto desconhecido, preservado
        if (!wroteIptc) AppendIptcResource(result, [], edit, [0, 0]);
        return [.. result];
    }

    private static void AppendIptcResource(List<byte> output, byte[] existingIptc, MetadataEdit edit, byte[] nameField)
    {
        var data = BuildIptc(existingIptc, edit);
        output.AddRange("8BIM"u8.ToArray());
        output.AddRange(new byte[] { 0x04, 0x04 });
        output.AddRange(nameField);
        output.AddRange(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length });
        output.AddRange(data);
        if ((data.Length & 1) == 1) output.Add(0);
    }

    private static readonly HashSet<int> ManagedIptcRecord2 = [5, 25, 80, 116, 120];

    private static byte[] BuildIptc(byte[] existing, MetadataEdit edit)
    {
        var datasets = new List<(int Record, int Number, byte[] Value)>();
        var position = 0;
        while (position + 5 <= existing.Length && existing[position] == 0x1C)
        {
            var record = existing[position + 1];
            var number = existing[position + 2];
            var length = (existing[position + 3] << 8) | existing[position + 4];
            if ((length & 0x8000) != 0) throw new NotSupportedException("O IPTC do arquivo usa campos de tamanho estendido, que ainda não são suportados; nada foi alterado.");
            if (position + 5 + length > existing.Length) throw new InvalidDataException("IPTC corrompido.");
            var managed = record == 2 && ManagedIptcRecord2.Contains(number);
            var charset = record == 1 && number == 90;
            if (!managed && !charset) datasets.Add((record, number, existing.AsSpan(position + 5, length).ToArray()));
            position += 5 + length;
        }
        for (var i = position; i < existing.Length; i++)
            if (existing[i] != 0) throw new InvalidDataException("IPTC com dados inesperados; nada foi alterado.");

        // Os textos são gravados em UTF-8 e declarados em 1:90 (ESC % G).
        datasets.Add((1, 90, [0x1B, 0x25, 0x47]));
        void Add(int number, string? value) { if (value is not null) datasets.Add((2, number, Truncate(Encoding.UTF8.GetBytes(value)))); }
        Add(5, edit.Title);
        Add(120, edit.Description);
        foreach (var keyword in edit.Keywords) Add(25, keyword);
        Add(80, edit.Author);
        Add(116, edit.Copyright);

        using var stream = new MemoryStream();
        foreach (var (record, number, value) in datasets.Select((d, i) => (d, i)).OrderBy(x => x.d.Record).ThenBy(x => x.d.Number).ThenBy(x => x.i).Select(x => x.d))
        {
            stream.Write([0x1C, (byte)record, (byte)number, (byte)(value.Length >> 8), (byte)value.Length]);
            stream.Write(value);
        }
        return stream.ToArray();
    }

    private static byte[] Truncate(byte[] value) => value.Length <= 32767 ? value : value[..32767];

    // ---------------------------------------------------------------- validação antes de substituir

    private static void Validate(string tempPath, byte[] original, byte[] rewritten, MetadataEdit edit)
    {
        var after = File.ReadAllBytes(tempPath);
        if (!after.AsSpan().SequenceEqual(rewritten)) throw new IOException("Falha de gravação: o arquivo temporário não confere com o esperado.");

        // 1. Tudo que não é XMP/IPTC permanece idêntico, na mesma ordem, e os dados da imagem também.
        var before = Parse(original);
        var afterLayout = Parse(after);
        var keptBefore = before.Segments.Where(s => !IsManaged(original, s)).Select(s => original.AsMemory(s.Start, s.Length)).ToList();
        var keptAfter = afterLayout.Segments.Where(s => !IsManaged(after, s)).Select(s => after.AsMemory(s.Start, s.Length)).ToList();
        if (keptBefore.Count != keptAfter.Count || keptBefore.Zip(keptAfter).Any(p => !p.First.Span.SequenceEqual(p.Second.Span)))
            throw new IOException("Validação falhou: segmentos fora de XMP/IPTC (EXIF, ICC…) seriam alterados.");
        if (!original.AsSpan(before.ScanStart).SequenceEqual(after.AsSpan(afterLayout.ScanStart)))
            throw new IOException("Validação falhou: os dados da imagem seriam alterados.");

        // 2. A imagem continua decodificável com as mesmas dimensões.
        if (DecodeSize(original) != DecodeSize(after)) throw new IOException("Validação falhou: dimensões da imagem mudaram.");

        // 3. Relendo com o leitor independente (só XMP+IPTC), os campos são exatamente os pedidos.
        var written = MetadataEdit.From(MetadataExtractorReader.Read(tempPath, includeExifTextFallback: false));
        if (!written.SameAs(edit)) throw new IOException("Validação falhou: os metadados relidos do arquivo temporário não coincidem com os informados.");
    }

    private static (int Width, int Height) DecodeSize(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }
}
