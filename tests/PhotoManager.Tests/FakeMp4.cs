using System.Buffers.Binary;
using System.Text;

namespace PhotoManager.Tests;

/// <summary>MP4 mínimo, montado à mão (ftyp + moov/mvhd + trak/tkhd + mdia/hdlr): serve para testar catálogo, metadados e sidecar sem um vídeo real.</summary>
public static class FakeMp4
{
    /// <param name="rotate90">grava a matriz de rotação de 90° (como um celular em retrato): largura/altura gravadas continuam deitadas.</param>
    public static byte[] Build(int width, int height, double seconds, uint timescale = 1000, uint creationSince1904 = 3_800_000_000, bool rotate90 = false, int rotationDegrees = 0)
    {
        var ftyp = Box("ftyp", Concat(Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isom")));

        var mvhd = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(4), creationSince1904);
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), (uint)(seconds * timescale));

        var tkhd = new byte[84];
        // matriz 3x3 em 16.16 na ordem do arquivo [a b u; c d v; x y w]: giro horário em graus (0, 90, 180, 270)
        var degrees = rotate90 ? 90 : rotationDegrees;
        var (a, b, c, d) = degrees switch { 90 => (0, 1, -1, 0), 180 => (-1, 0, 0, -1), 270 => (0, -1, 1, 0), _ => (1, 0, 0, 1) };
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(40), a * 0x10000);
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(44), b * 0x10000);
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(52), c * 0x10000);
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(56), d * 0x10000);
        BinaryPrimitives.WriteInt32BigEndian(tkhd.AsSpan(68), 0x40000000);   // w = 1.0 (2.30)
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(76), (uint)width << 16);
        BinaryPrimitives.WriteUInt32BigEndian(tkhd.AsSpan(80), (uint)height << 16);

        var hdlr = new byte[25];
        Encoding.ASCII.GetBytes("vide").CopyTo(hdlr, 8);

        var trak = Box("trak", Concat(Box("tkhd", tkhd), Box("mdia", Box("hdlr", hdlr))));
        var moov = Box("moov", Concat(Box("mvhd", mvhd), trak));
        var mdat = Box("mdat", new byte[16]);
        return Concat(ftyp, moov, mdat);
    }

    private static byte[] Box(string type, byte[] payload) => Concat(U32((uint)(payload.Length + 8)), Encoding.ASCII.GetBytes(type), payload);
    private static byte[] U32(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
