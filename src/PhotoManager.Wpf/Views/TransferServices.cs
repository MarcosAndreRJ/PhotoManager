using System.Collections.Specialized;
using System.IO;
using System.Windows;
using PhotoManager.Application.Transfer;

namespace PhotoManager.Wpf.Views;

/// <summary>Perguntas que a Transferência faz ao usuário. A implementação real abre janelas; os testes respondem direto.</summary>
public interface ITransferDialogs
{
    bool Confirm(string question);
    /// <summary>Itens com o mesmo nome no destino: a regra escolhida vale para todos. Nulo = cancelar.</summary>
    ConflictPolicy? AskConflict(int count, string example, bool move);
    bool BatchRename(BatchRenameViewModel viewModel);
    bool OrganizeByDate(OrganizeByDateViewModel viewModel);
}

/// <summary>Área de transferência de ARQUIVOS (a mesma do Explorer): Ctrl+C/Ctrl+X aqui e Ctrl+V lá, e vice-versa.</summary>
public interface IFileClipboard
{
    void SetFiles(IReadOnlyList<string> paths, bool cut);
    /// <summary>Arquivos copiados/recortados (aqui ou no Explorer); nulo se não houver arquivos.</summary>
    (IReadOnlyList<string> Paths, bool Cut)? GetFiles();
    void Clear();
}

/// <summary>Quem coordena os dois painéis: copiar/mover com progresso, desfazer e a pasta do outro lado.</summary>
public interface ITransferPaneHost
{
    string? OtherPanePath(TransferPaneViewModel pane);
    /// <summary>Copia/move com progresso, pergunta sobre conflitos uma vez, registra para desfazer e atualiza os dois painéis. <paramref name="createMissingDestinations"/>: cria as pastas de destino (organizar por data).</summary>
    Task<TransferBatchResult?> RunTransferAsync(IReadOnlyList<TransferJob> jobs, bool move, string description, bool createMissingDestinations = false);
    void PushUndo(TransferUndoEntry entry);
    Task UndoAsync();
    IFileClipboard Clipboard { get; }
    void TogglePin(TransferPaneViewModel pane);
}

public sealed class WpfTransferDialogs : ITransferDialogs
{
    public bool Confirm(string question) =>
        (Owner() is { } owner
            ? MessageBox.Show(owner, question, "PhotoManager", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(question, "PhotoManager", MessageBoxButton.YesNo, MessageBoxImage.Question)) == MessageBoxResult.Yes;

    public ConflictPolicy? AskConflict(int count, string example, bool move) => ConflictDialog.Ask(Owner(), count, example, move);

    public bool BatchRename(BatchRenameViewModel viewModel) => Show(new BatchRenameDialog { DataContext = viewModel });

    public bool OrganizeByDate(OrganizeByDateViewModel viewModel) => Show(new OrganizeByDateDialog { DataContext = viewModel });

    private static bool Show(Window dialog)
    {
        if (Owner() is { } owner) dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }

    internal static Window? Owner() => System.Windows.Application.Current?.MainWindow is { IsVisible: true } window ? window : null;
}

/// <summary>Formato do Explorer: lista de arquivos + "Preferred DropEffect" (5 = copiar, 2 = mover/recortar).</summary>
public sealed class WpfFileClipboard : IFileClipboard
{
    public void SetFiles(IReadOnlyList<string> paths, bool cut)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData(PhotoDragData.PreferredDropEffectFormat, new MemoryStream(BitConverter.GetBytes(cut ? (int)DragDropEffects.Move : (int)(DragDropEffects.Copy | DragDropEffects.Link))));
        try { Clipboard.SetDataObject(data, copy: true); } catch (System.Runtime.InteropServices.ExternalException) { }
    }

    public (IReadOnlyList<string> Paths, bool Cut)? GetFiles()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return null;
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            var cut = Clipboard.GetData(PhotoDragData.PreferredDropEffectFormat) is MemoryStream stream && stream.Length >= 4
                && (BitConverter.ToInt32(stream.ToArray(), 0) & (int)DragDropEffects.Move) != 0;
            return files.Count == 0 ? null : (files, cut);
        }
        catch (System.Runtime.InteropServices.ExternalException) { return null; }
    }

    public void Clear()
    {
        try { Clipboard.Clear(); } catch (System.Runtime.InteropServices.ExternalException) { }
    }
}
