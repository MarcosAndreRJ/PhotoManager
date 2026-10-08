using System.Buffers.Binary;

namespace PhotoManager.Infrastructure.Media;

/// <summary>Dados básicos de um vídeo do contêiner ISO-BMFF (MP4, M4V, MOV).</summary>
/// <param name="RotationDegrees">Giro horário (0, 90, 180 ou 270) que o arquivo manda aplicar ao exibir (matriz do tkhd); Width/Height já vêm como exibidos.</param>
public sealed record VideoInfo(int? Width, int? Height, double? DurationSeconds, DateTime? CreatedUtc, int RotationDegrees = 0);

/// <summary>
/// Lê largura, altura, duração e data de criação direto das "caixas" (atoms) do MP4/MOV, sem biblioteca nem decodificador.
/// Só leitura: percorre <c>moov → mvhd</c> e a primeira faixa de vídeo (<c>trak → tkhd/hdlr</c>). Qualquer problema devolve nulos.
/// </summary>
public static class Mp4Info
{
    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static VideoInfo? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException) { return null; }
    }

    public static VideoInfo? Read(Stream stream)
    {
        double? duration = null;
        DateTime? created = null;
        int? width = null, height = null;
        var rotation = 0;
        var found = false;

        foreach (var box in Boxes(stream, 0, stream.Length))
        {
            if (box.Type != "moov") continue;
            found = true;
            foreach (var child in Boxes(stream, box.PayloadStart, box.End))
            {
                if (child.Type == "mvhd") (duration, created) = ReadMvhd(stream, child);
                else if (child.Type == "trak" && width is null) (width, height, rotation) = ReadVideoTrack(stream, child);
            }
            break;
        }
        return found ? new VideoInfo(width, height, duration, created, rotation) : null;
    }

    /// <summary>Texto ISO 6709 do átomo <c>©xyz</c> (em <c>moov</c> ou <c>moov/udta</c>), gravado por drones DJI e muitos celulares; nulo se não houver.</summary>
    public static string? TryReadLocation(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadLocation(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException) { return null; }
    }

    public static string? ReadLocation(Stream stream)
    {
        const string Xyz = "©xyz";
        foreach (var moov in Boxes(stream, 0, stream.Length).Where(b => b.Type == "moov"))
        {
            foreach (var child in Boxes(stream, moov.PayloadStart, moov.End))
            {
                if (child.Type == Xyz) return ReadText(stream, child);
                if (child.Type != "udta") continue;
                foreach (var inner in Boxes(stream, child.PayloadStart, child.End))
                    if (inner.Type == Xyz) return ReadText(stream, inner);
            }
        }
        return null;
    }

    private static string? ReadText(Stream stream, Box box)
    {
        var length = (int)Math.Min(128, box.End - box.PayloadStart);
        if (length <= 4) return null;
        var data = new byte[length];
        stream.Position = box.PayloadStart;
        if (stream.Read(data, 0, length) < length) return null;
        // 2 bytes de tamanho + 2 de idioma; o resto é o texto (ex.: "-22.476429-42.187541+13.200/").
        return System.Text.Encoding.Latin1.GetString(data, 4, length - 4).Trim('\0', ' ');
    }

    private readonly record struct Box(string Type, long Start, long PayloadStart, long End);

    private static IEnumerable<Box> Boxes(Stream stream, long from, long to)
    {
        var position = from;
        var header = new byte[16];
        while (position + 8 <= to)
        {
            stream.Position = position;
            if (stream.Read(header, 0, 8) < 8) yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.Latin1.GetString(header, 4, 4);
            long payload = position + 8;
            if (size == 1)
            {
                if (stream.Read(header, 8, 8) < 8) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                payload = position + 16;
            }
            else if (size == 0) size = to - position;
            if (size < payload - position || position + size > to) yield break;
            yield return new Box(type, position, payload, position + size);
            position += size;
        }
    }

    private static (double? Duration, DateTime? Created) ReadMvhd(Stream stream, Box box)
    {
        var data = new byte[32];
        stream.Position = box.PayloadStart;
        var read = stream.Read(data, 0, (int)Math.Min(data.Length, box.End - box.PayloadStart));
        if (read < 20) return (null, null);
        var version = data[0];
        ulong creation, duration;
        uint timescale;
        if (version == 1)
        {
            if (read < 32) return (null, null);
            creation = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(4));
            timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));
            duration = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(24));
        }
        else
        {
            creation = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
            timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
            duration = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
        }
        double? seconds = timescale > 0 ? duration / (double)timescale : null;
        DateTime? when = creation > 0 && creation < 4_000_000_000UL ? Epoch1904.AddSeconds(creation) : null;
        return (seconds, when);
    }

    private static (int? Width, int? Height, int Rotation) ReadVideoTrack(Stream stream, Box trak)
    {
        int? width = null, height = null;
        var rotation = 0;
        var isVideo = false;
        foreach (var child in Boxes(stream, trak.PayloadStart, trak.End))
        {
            if (child.Type == "tkhd")
            {
                var length = (int)Math.Min(120, child.End - child.PayloadStart);
                var data = new byte[length];
                stream.Position = child.PayloadStart;
                if (stream.Read(data, 0, length) < length || length < 84) continue;
                var versionOffset = data[0] == 1 ? 96 : 84;       // largura/altura (16.16) são os 8 últimos bytes da caixa
                if (length < versionOffset - 8 + 8) continue;
                var end = length >= versionOffset ? versionOffset : length;
                var w = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(end - 8)) >> 16;
                var h = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(end - 4)) >> 16;
                if (w > 0 && h > 0)
                {
                    // Vídeo de celular: a imagem é gravada deitada e a matriz de transformação gira 90°/270° para exibir (retrato). Sem isso o retrato aparece como 1920×1080.
                    var matrixAt = data[0] == 1 ? 52 : 40;
                    var a = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(matrixAt));
                    var b = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(matrixAt + 4));
                    var rotated = Math.Abs((long)b) > Math.Abs((long)a);
                    (width, height) = rotated ? ((int)h, (int)w) : ((int)w, (int)h);
                    // giro horário em graus a partir da matriz de rotação [a b; c d] (a=1,b=0: 0°; a=0,b=1: 90°; a=-1: 180°; b=-1: 270°)
                    rotation = a > 0 && Math.Abs((long)b) < Math.Abs((long)a) ? 0 : b > 0 && Math.Abs((long)a) < Math.Abs((long)b) ? 90 : a < 0 && Math.Abs((long)b) < Math.Abs((long)a) ? 180 : b < 0 && Math.Abs((long)a) < Math.Abs((long)b) ? 270 : 0;
                }
            }
            else if (child.Type == "mdia")
            {
                foreach (var mdiaChild in Boxes(stream, child.PayloadStart, child.End))
                {
                    if (mdiaChild.Type != "hdlr") continue;
                    var data = new byte[12];
                    stream.Position = mdiaChild.PayloadStart;
                    if (stream.Read(data, 0, 12) == 12) isVideo = System.Text.Encoding.Latin1.GetString(data, 8, 4) == "vide";
                }
            }
        }
        return isVideo ? (width, height, rotation) : (null, null, 0);
    }
}
