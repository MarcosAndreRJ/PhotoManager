namespace PhotoManager.Infrastructure.Images;

/// <summary>
/// Localiza o JPEG de visualização embutido em arquivos RAW baseados em TIFF (CR2). Lê apenas os diretórios (IFDs) e o trecho do JPEG,
/// nunca o arquivo inteiro (RAWs têm dezenas de MB). Não interpreta os dados do sensor.
/// </summary>
public static class RawPreview
{
    public sealed record Result(byte[] Jpeg, int RotationDegrees);

    private const ushort TagCompression = 0x103, TagOrientation = 0x112, TagStripOffsets = 0x111, TagStripByteCounts = 0x117;
    private const ushort TagJpegOffset = 0x201, TagJpegLength = 0x202, TagCr2Slice = 0xC640;

    public static Result? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException) { return null; }
    }

    private static Result? Read(FileStream stream)
    {
        var header = new byte[8];
        stream.ReadExactly(header);
        if (!(header[0] == 'I' && header[1] == 'I' && header[2] == 0x2A && header[3] == 0)) return null; // CR2 é little-endian
        var ifdOffset = BitConverter.ToUInt32(header, 4);

        (long Offset, long Length)? best = null;
        var rotation = 0;
        for (var index = 0; index < 8 && ifdOffset != 0 && ifdOffset < stream.Length; index++)
        {
            var tags = ReadIfd(stream, ifdOffset, out var next);
            ifdOffset = next;
            if (index == 0 && tags.TryGetValue(TagOrientation, out var orientation)) rotation = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
            if (tags.ContainsKey(TagCr2Slice)) continue; // IFD dos dados do sensor (JPEG sem perdas): não é uma imagem exibível

            (long, long)? candidate = null;
            if (tags.TryGetValue(TagStripOffsets, out var offset) && tags.TryGetValue(TagStripByteCounts, out var length) && tags.GetValueOrDefault(TagCompression) == 6) candidate = (offset, length);
            else if (tags.TryGetValue(TagJpegOffset, out offset) && tags.TryGetValue(TagJpegLength, out length)) candidate = (offset, length);
            if (candidate is { } c && c.Item2 > 0 && c.Item1 + c.Item2 <= stream.Length && (best is null || c.Item2 > best.Value.Length)) best = c;
        }
        if (best is null) return null;

        var jpeg = new byte[best.Value.Length];
        stream.Seek(best.Value.Offset, SeekOrigin.Begin);
        stream.ReadExactly(jpeg);
        return jpeg.Length > 3 && jpeg[0] == 0xFF && jpeg[1] == 0xD8 ? new Result(jpeg, rotation) : null;
    }

    /// <summary>Lê as entradas de um IFD; só guarda valores escalares (SHORT/LONG com contagem 1), que é o que interessa aqui.</summary>
    private static Dictionary<ushort, long> ReadIfd(FileStream stream, uint offset, out uint nextIfd)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var countBytes = new byte[2];
        stream.ReadExactly(countBytes);
        var count = BitConverter.ToUInt16(countBytes);
        if (count == 0 || count > 512) throw new InvalidDataException("IFD inválido.");
        var buffer = new byte[count * 12 + 4];
        stream.ReadExactly(buffer);
        var tags = new Dictionary<ushort, long>();
        for (var i = 0; i < count; i++)
        {
            var entry = buffer.AsSpan(i * 12, 12);
            var tag = BitConverter.ToUInt16(entry[..2]);
            var type = BitConverter.ToUInt16(entry.Slice(2, 2));
            var valueCount = BitConverter.ToUInt32(entry.Slice(4, 4));
            if (valueCount == 1 && type == 3) tags[tag] = BitConverter.ToUInt16(entry.Slice(8, 2));
            else if (valueCount == 1 && type == 4) tags[tag] = BitConverter.ToUInt32(entry.Slice(8, 4));
            else tags.TryAdd(tag, 0); // presença (ex.: 0xC640) sem valor escalar
        }
        nextIfd = BitConverter.ToUInt32(buffer, count * 12);
        return tags;
    }
}
