using System.IO;
using System.Windows;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Collections;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Wpf.Views;

/// <summary>
/// Dados do arraste de fotos da grade. Além do formato interno (Ids, para coleções e pastas da própria árvore), leva os ARQUIVOS (formato do Explorer),
/// com o sidecar .xmp junto, para soltar numa pasta do Explorer. "Preferred DropEffect" = copiar: o Explorer copia por padrão (mover só com Shift).
/// </summary>
public static class PhotoDragData
{
    public const string PreferredDropEffectFormat = "Preferred DropEffect";

    public static DataObject Create(IReadOnlyList<long> photoIds, IEnumerable<Photo> photos)
    {
        var data = new DataObject(CollectionDragDropFormats.PhotoIds, photoIds.ToArray());
        var files = new List<string>();
        foreach (var photo in photos)
        {
            if (photo.IsMissing || !File.Exists(photo.CurrentPath)) continue;
            files.Add(photo.CurrentPath);
            if (XmpSidecar.UsesSidecar(photo.CurrentPath) && File.Exists(XmpSidecar.PathFor(photo.CurrentPath))) files.Add(XmpSidecar.PathFor(photo.CurrentPath));
        }
        if (files.Count > 0)
        {
            data.SetData(DataFormats.FileDrop, files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            data.SetData(PreferredDropEffectFormat, new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        }
        return data;
    }
}
