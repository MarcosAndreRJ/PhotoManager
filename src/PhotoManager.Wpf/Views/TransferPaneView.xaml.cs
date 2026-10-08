using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Transfer;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Wpf.Views;

/// <summary>
/// Somente integração visual de um painel de Transferência: seleção, duplo clique, cabeçalhos, teclas, renomear no lugar e arrastar/soltar.
/// Toda decisão (o que copiar, para onde, conflitos, desfazer) está nos ViewModels.
/// </summary>
public partial class TransferPaneView : UserControl
{
    /// <summary>Formato interno do arraste: a pasta de origem (soltar na mesma pasta não faz nada).</summary>
    public const string SourceFolderFormat = "PhotoManager.TransferSourceFolder";

    private Point? _dragStart;
    private TransferItemViewModel? _dragItem;

    public TransferPaneView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is TransferPaneViewModel old) old.RevealRequested -= Pane_RevealRequested;
            if (e.NewValue is TransferPaneViewModel pane) pane.RevealRequested += Pane_RevealRequested;
        };
    }

    private TransferPaneViewModel? Pane => DataContext as TransferPaneViewModel;
    private ListBox ActiveList => Pane?.IsListMode == true ? DetailList : GridList;

    // ---------- seleção, abrir, foco ----------

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Pane is null || sender is not ListBox { IsVisible: true } list) return;      // o controle escondido não manda na seleção
        Pane.SetSelection(list.SelectedItems.OfType<TransferItemViewModel>());
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Pane is null || ItemOf(e.OriginalSource as DependencyObject) is not { IsEditing: false } item) return;
        e.Handled = true;
        Open(item);
    }

    private void Open(TransferItemViewModel item)
    {
        if (Pane is null) return;
        if (item.IsFolder) Pane.OpenItemCommand.Execute(item);
        else if (item.IsMedia && File.Exists(item.Path))
            MediaViewerWindow.Open(new Photo { FileName = item.Name, CurrentPath = item.Path, Extension = item.Entry.Extension });   // o visualizador existente, com um item transitório
    }

    private static TransferItemViewModel? ItemOf(DependencyObject? source)
    {
        for (var node = source; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is FrameworkElement { DataContext: TransferItemViewModel item } and ListBoxItem) return item;
        return null;
    }

    /// <summary>Clique ou foco num painel o torna o "ativo": os Locais da barra lateral abrem nele.</summary>
    private void Pane_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => MarkActive();
    private void Pane_PreviewMouseDown(object sender, MouseButtonEventArgs e) => MarkActive();

    private void MarkActive()
    {
        if (Pane is null) return;
        for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { DataContext: TransferViewModel transfer }) { transfer.ActivePane = Pane; return; }
    }

    /// <summary>Pasta criada/renomeada: fica selecionada e visível, com o foco do teclado nela (F2/Del/Enter já agem sobre ela).</summary>
    private void Pane_RevealRequested(object? sender, string path)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Pane?.Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) is not { } item) return;
            var list = ActiveList;
            list.SelectedItems.Clear();
            list.SelectedItem = item;
            list.ScrollIntoView(item);
            if (!item.IsEditing) (list.ItemContainerGenerator.ContainerFromItem(item) as UIElement)?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Botão direito como no Explorer: sobre um item fora da seleção, seleciona só ele; no vazio, limpa a seleção (o menu oferece "Nova pasta" e "Colar").</summary>
    private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        var item = ItemOf(e.OriginalSource as DependencyObject);
        if (item is null) list.UnselectAll();
        else if (!list.SelectedItems.Contains(item)) { list.SelectedItems.Clear(); list.SelectedItem = item; }
        list.Focus();
    }

    private void OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Pane is { SelectedItems.Count: 1 } pane) Open(pane.SelectedItems[0]);
    }

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.DataContext = Pane;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (Pane is null || sender is not GridViewColumnHeader { Tag: string tag }) return;
        if (Enum.TryParse<TransferSortField>(tag, out var field)) Pane.SortByCommand.Execute(field);
    }

    private void GridMode_Click(object sender, RoutedEventArgs e) => Pane?.SetViewModeCommand.Execute(TransferViewMode.Grid);
    private void ListMode_Click(object sender, RoutedEventArgs e) => Pane?.SetViewModeCommand.Execute(TransferViewMode.List);

    // ---------- teclado ----------

    private void Pane_KeyDown(object sender, KeyEventArgs e)
    {
        if (Pane is null || e.OriginalSource is TextBox) return;
        var modifiers = Keyboard.Modifiers;
        var ctrl = modifiers == ModifierKeys.Control;
        if (e.Key == Key.Back && Pane.BackCommand.CanExecute(null)) Pane.BackCommand.Execute(null);
        else if (e.Key == Key.F5) Pane.RefreshCommand.Execute(null);
        else if (e.Key == Key.Enter && Pane.SelectedItems.Count == 1) Open(Pane.SelectedItems[0]);
        else if (e.Key == Key.F2) Execute(Pane.RenameCommand);
        else if (e.Key == Key.Delete && modifiers == ModifierKeys.None) Execute(Pane.DeleteCommand);
        else if (e.Key == Key.N && modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) Execute(Pane.NewFolderCommand);
        else if (ctrl && e.Key == Key.C) Execute(Pane.CopyCommand);
        else if (ctrl && e.Key == Key.X) Execute(Pane.CutCommand);
        else if (ctrl && e.Key == Key.V) Execute(Pane.PasteCommand);
        else if (ctrl && e.Key == Key.Z) Execute(Pane.UndoCommand);
        else if (modifiers == ModifierKeys.None && DigitOf(e.Key) is { } digit && PhotoColors.FromDigit(digit) is { } color) _ = Pane.SetColorForSelectionAsync(color);
        else return;
        e.Handled = true;
    }

    private static void Execute(ICommand command) { if (command.CanExecute(null)) command.Execute(null); }

    private static int? DigitOf(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null
    };

    // ---------- renomear no lugar ----------

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box) Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Pane is null || sender is not TextBox { DataContext: TransferItemViewModel item }) return;
        if (e.Key == Key.Enter) { e.Handled = true; _ = Pane.CommitInlineRenameAsync(item); ActiveList.Focus(); }
        else if (e.Key == Key.Escape) { e.Handled = true; Pane.CancelInlineRename(item); ActiveList.Focus(); }
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Pane is not null && sender is TextBox { DataContext: TransferItemViewModel { IsEditing: true } item }) _ = Pane.CommitInlineRenameAsync(item);
    }

    // ---------- arrastar e soltar ----------

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = e.OriginalSource is DependencyObject source && !IsInsideTextBox(source) ? ItemOf(source) : null;
        _dragItem = item;
        _dragStart = item is null ? null : e.GetPosition(this);
    }

    /// <summary>Arrastar a seleção leva os ARQUIVOS (formato do Explorer, com o .xmp): solta no outro painel, numa pasta, na Biblioteca ou no Explorer.</summary>
    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (Pane is null || _dragStart is not { } start || _dragItem is null || e.LeftButton != MouseButtonState.Pressed || sender is not ListBox list) return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!list.SelectedItems.Contains(_dragItem)) { list.SelectedItems.Clear(); list.SelectedItem = _dragItem; }
        _dragStart = null;
        var paths = Pane.SelectedPathsWithSidecars();
        if (paths.Count == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
        data.SetData(SourceFolderFormat, Pane.CurrentPath);
        data.SetData(PhotoDragData.PreferredDropEffectFormat, new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        var result = DragDrop.DoDragDrop(list, data, DragDropEffects.Copy | DragDropEffects.Move);
        // Soltar no Explorer com Shift move os arquivos para fora: a pasta precisa ser relida.
        if ((result & DragDropEffects.Move) != 0) _ = Pane.RefreshAsync();
    }

    private void List_DragOver(object sender, DragEventArgs e) => UpdateDrop(e, TargetFolderOf(e.OriginalSource as DependencyObject));
    private void Breadcrumb_DragOver(object sender, DragEventArgs e) => UpdateDrop(e, (sender as FrameworkElement)?.DataContext is BreadcrumbPart part ? part.Path : null);
    private void List_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private void List_Drop(object sender, DragEventArgs e) => HandleDrop(e, TargetFolderOf(e.OriginalSource as DependencyObject));
    private void Breadcrumb_Drop(object sender, DragEventArgs e) => HandleDrop(e, (sender as FrameworkElement)?.DataContext is BreadcrumbPart part ? part.Path : null);

    /// <summary>Normal = copiar (o cartão/HD de origem nunca perde nada por acidente); Shift = mover; Ctrl = copiar.</summary>
    private void UpdateDrop(DragEventArgs e, string? folderUnderCursor)
    {
        e.Handled = true;
        var destination = folderUnderCursor ?? Pane?.CurrentPath;
        if (Pane is null || !Pane.CanOrganize || destination is null || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0
            || (e.Data.GetData(SourceFolderFormat) is string source && folderUnderCursor is null && string.Equals(source, Pane.CurrentPath, StringComparison.OrdinalIgnoreCase))
            || files.Any(f => string.Equals(f, destination, StringComparison.OrdinalIgnoreCase)))
        {
            e.Effects = DragDropEffects.None;
            DropHint.Visibility = Visibility.Collapsed;
            return;
        }
        var move = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        e.Effects = move ? DragDropEffects.Move : DragDropEffects.Copy;
        var name = System.IO.Path.GetFileName(destination.TrimEnd('\\'));
        DropHintText.Text = $"{(move ? "Mover" : "Copiar")} {files.Length} item(ns) para “{(name.Length > 0 ? name : destination)}”" + (move ? string.Empty : "   (Shift = mover)");
        DropHint.Visibility = Visibility.Visible;
    }

    private void HandleDrop(DragEventArgs e, string? folderUnderCursor)
    {
        DropHint.Visibility = Visibility.Collapsed;
        UpdateDrop(e, folderUnderCursor);
        DropHint.Visibility = Visibility.Collapsed;
        if (Pane is null || e.Effects == DragDropEffects.None || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        _ = Pane.DropAsync(files, folderUnderCursor, move: e.Effects == DragDropEffects.Move);
    }

    /// <summary>Soltar sobre uma PASTA da lista entra nela; no resto, vai para a pasta aberta.</summary>
    private static string? TargetFolderOf(DependencyObject? source) => ItemOf(source) is { IsFolder: true } folder ? folder.Path : null;

    private static bool IsInsideTextBox(DependencyObject source)
    {
        for (var node = source; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is TextBox) return true;
        return false;
    }

    // ---------- caminho digitado ----------

    private void PathBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box) { box.Focus(); box.SelectAll(); }
    }

    private void PathBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Pane is { IsEditingPath: true }) Pane.CancelEditPathCommand.Execute(null);
    }
}
