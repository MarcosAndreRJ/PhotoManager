using System.Text;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Metadata;
using XmpCore;
using XmpCore.Options;

namespace PhotoManager.Infrastructure.Metadata;

/// <summary>
/// Grava título, descrição, palavras-chave, autor e copyright (dc:*) num arquivo <c>nome.xmp</c> ao lado da mídia (RAW, PNG, WebP, vídeo).
/// O arquivo de mídia nunca é aberto para escrita. O sidecar é gravado em temporário, relido para validar e só então substitui o anterior.
/// </summary>
public sealed class SidecarXmpWriter : IMetadataWriter
{
    public bool CanWrite(string path) => XmpSidecar.UsesSidecar(path);

    public Task WriteAsync(string path, MetadataEdit edit, CancellationToken cancellationToken = default)
    {
        if (!CanWrite(path)) throw new NotSupportedException("Este formato não usa sidecar XMP.");
        return Task.Run(() => WriteCore(path, edit.Normalize(), cancellationToken), cancellationToken);
    }

    private static void WriteCore(string mediaPath, MetadataEdit edit, CancellationToken cancellationToken)
    {
        var sidecar = XmpSidecar.PathFor(mediaPath);
        var existing = File.Exists(sidecar) ? File.ReadAllText(sidecar, Encoding.UTF8) : null;
        var xmp = Build(existing, edit);

        var temp = sidecar + ".pm-tmp";
        try
        {
            File.WriteAllText(temp, xmp, new UTF8Encoding(false));
            var check = SidecarXmpReader.Parse(File.ReadAllText(temp, Encoding.UTF8))
                ?? throw new InvalidDataException("O sidecar gerado não pôde ser relido; nada foi gravado.");
            if (check.Title != edit.Title || check.Description != edit.Description || check.Author != edit.Author || check.Copyright != edit.Copyright
                || !check.Keywords.SequenceEqual(edit.Keywords))
                throw new InvalidDataException("A validação do sidecar falhou (os valores relidos diferem); nada foi gravado.");
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(sidecar)) File.Replace(temp, sidecar, null, ignoreMetadataErrors: true);
            else File.Move(temp, sidecar);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
    }

    private static string Build(string? existing, MetadataEdit edit)
    {
        IXmpMeta meta;
        try { meta = string.IsNullOrWhiteSpace(existing) ? XmpMetaFactory.Create() : XmpMetaFactory.ParseFromString(existing.TrimStart('﻿')); }
        catch (XmpException ex) { throw new InvalidDataException("O sidecar .xmp existente não pôde ser interpretado; nada foi alterado. " + ex.Message, ex); }

        const string dc = XmpConstants.NsDC;
        foreach (var property in new[] { "title", "description", "subject", "creator", "rights" }) meta.DeleteProperty(dc, property);
        if (edit.Title is not null) meta.SetLocalizedText(dc, "title", null, "x-default", edit.Title);
        if (edit.Description is not null) meta.SetLocalizedText(dc, "description", null, "x-default", edit.Description);
        foreach (var keyword in edit.Keywords) meta.AppendArrayItem(dc, "subject", new PropertyOptions { IsArray = true }, keyword, null);
        if (edit.Author is not null) meta.AppendArrayItem(dc, "creator", new PropertyOptions { IsArray = true, IsArrayOrdered = true }, edit.Author, null);
        if (edit.Copyright is not null) meta.SetLocalizedText(dc, "rights", null, "x-default", edit.Copyright);
        return XmpMetaFactory.SerializeToString(meta, new SerializeOptions { Padding = 0 }).TrimStart('﻿');
    }
}

/// <summary>Lê os campos dc:* de um sidecar. O sidecar é a fonte única desses campos nos formatos que o usam.</summary>
public static class SidecarXmpReader
{
    public static MetadataEdit? Parse(string xmp)
    {
        try
        {
            var meta = XmpMetaFactory.ParseFromString(xmp.TrimStart('﻿'));
            const string dc = XmpConstants.NsDC;
            var keywords = new List<string>();
            var count = meta.CountArrayItems(dc, "subject");
            for (var i = 1; i <= count; i++) if (meta.GetArrayItem(dc, "subject", i)?.Value is { } k && !string.IsNullOrWhiteSpace(k)) keywords.Add(k.Trim());
            return new MetadataEdit
            {
                Title = Lang(meta, "title"),
                Description = Lang(meta, "description"),
                Keywords = keywords,
                Author = meta.CountArrayItems(dc, "creator") > 0 ? Clean(meta.GetArrayItem(dc, "creator", 1)?.Value) : null,
                Copyright = Lang(meta, "rights")
            };
        }
        catch (XmpException) { return null; }
    }

    public static MetadataEdit? ReadFor(string mediaPath)
    {
        var sidecar = XmpSidecar.PathFor(mediaPath);
        if (!File.Exists(sidecar)) return null;
        try { return Parse(File.ReadAllText(sidecar, Encoding.UTF8)); }
        catch (IOException) { return null; }
    }

    private static string? Lang(IXmpMeta meta, string name)
    {
        const string dc = XmpConstants.NsDC;
        if (!meta.DoesPropertyExist(dc, name)) return null;
        var item = meta.GetLocalizedText(dc, name, null, "x-default");
        return Clean(item?.Value);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>JPEG grava dentro do arquivo; todo o resto (RAW, PNG, WebP, vídeo) grava no sidecar .xmp.</summary>
public sealed class CompositeMetadataWriter(IMetadataWriter jpeg, IMetadataWriter sidecar) : IMetadataWriter
{
    public bool CanWrite(string path) => jpeg.CanWrite(path) || sidecar.CanWrite(path);

    public Task WriteAsync(string path, MetadataEdit edit, CancellationToken cancellationToken = default) =>
        (jpeg.CanWrite(path) ? jpeg : sidecar).WriteAsync(path, edit, cancellationToken);
}
