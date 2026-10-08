using Microsoft.Extensions.DependencyInjection;
using System.Windows.Threading;

namespace PhotoManager.Wpf.Views;

/// <summary>Somente integração visual: diálogos de pasta/confirmação, seleção múltipla da ListBox e debounce da busca. Regras ficam na LibraryViewModel.</summary>
public partial class LibraryView : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private LibraryViewModel? _subscribed;
    private bool _restoringSelection;
    private List<PhotoCardViewModel> _pendingSelection = [];
    private System.Windows.Point _dragStartPoint;
    private PhotoCardViewModel? _dragCard;
    private bool _isDragging;
    private PhotoManager.Application.Collections.PhotoClickAction _deferredClick;
    private CollectionNode? _currentDragOverNode;
    private DispatcherTimer? _expandTimer;
    private CollectionNode? _hoverExpandNode;
    private System.Windows.Point _dragCollectionStartPoint;
    private CollectionNode? _dragCollectionNode;
    private bool _isDraggingCollection;

    public LibraryView()
    {
        InitializeComponent();
        InitPro();
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            if (DataContext is LibraryViewModel viewModel) viewModel.ApplyFilters();
            ShowSuggestions();
        };
        Loaded += (_, _) => { if (_subscribed is not null) RestoreSelection(_pendingSelection.Where(c => _subscribed.Photos.Contains(c)).ToList()); else _restoringSelection = false; };
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null) _subscribed.SelectionRestoreRequested -= RestoreSelection;
            _subscribed = DataContext as LibraryViewModel;
            // Ao voltar de outra aba a grade é nova: guarda a seleção múltipla da VM e a restaura depois do carregamento (a ligação de SelectedItem a reduziria a uma foto).
            _pendingSelection = _subscribed?.SelectedCards.ToList() ?? [];
            _restoringSelection = true;
            if (_subscribed is not null) _subscribed.SelectionRestoreRequested += RestoreSelection;
        };
    }

    private IEnumerable<PhotoCardViewModel> SelectedCards => PhotoList.SelectedItems.Cast<PhotoCardViewModel>().ToList();

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void PhotoList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_restoringSelection) return;
        if (DataContext is LibraryViewModel viewModel) viewModel.UpdateSelection(SelectedCards.ToList());
        if (PhotoList.SelectedItem is not null && PhotoList.SelectedItems.Count == 1) PhotoList.ScrollIntoView(PhotoList.SelectedItem);
    }

    /// <summary>A grade foi reconstruída (filtro, recarga ou aplicação em lote): remarca o que continua visível.</summary>
    private void RestoreSelection(IReadOnlyList<PhotoCardViewModel> cards)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _restoringSelection = true;
            try
            {
                PhotoList.SelectedItems.Clear();
                foreach (var card in cards) PhotoList.SelectedItems.Add(card);
            }
            finally { _restoringSelection = false; }
            if (DataContext is LibraryViewModel viewModel) viewModel.UpdateSelection(SelectedCards.ToList());
        }, DispatcherPriority.Background);
    }

    /// <summary>Duplo clique numa foto (fora das caixas e do coração) abre a revisão.</summary>
    private void PhotoList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not LibraryViewModel viewModel) return;
        for (var node = e.OriginalSource as System.Windows.DependencyObject; node is not null && !ReferenceEquals(node, PhotoList); node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;
            if (node is System.Windows.Controls.ListBoxItem { DataContext: PhotoCardViewModel card }) { viewModel.EnterReview(card); e.Handled = true; return; }
        }
    }


    /// <summary>Antes de abrir o menu da miniatura, o ViewModel guarda qual é ela (o submenu "Cor" age sobre ela ou sobre a seleção dela).</summary>
    private void PhotoCard_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel && sender is System.Windows.FrameworkElement { DataContext: PhotoCardViewModel card }) viewModel.SetContextCard(card);
    }

    private static int? DigitOf(System.Windows.Input.Key key) => key switch
    {
        >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => key - System.Windows.Input.Key.D0,
        >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => key - System.Windows.Input.Key.NumPad0,
        _ => null
    };
    private void PhotoList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is LibraryViewModel proViewModel && HandleProGridKey(e, proViewModel)) { e.Handled = true; return; }
        PhotoList_KeyDownCore(sender, e);
    }

    private void PhotoList_KeyDownCore(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None && DigitOf(e.Key) is { } digit && PhotoManager.Application.Catalog.PhotoColors.FromDigit(digit) is { } color
            && DataContext is LibraryViewModel colorViewModel && colorViewModel.SelectedCards.Count > 0)
        {
            _ = colorViewModel.SetColorForSelectionAsync(color);   // teclas 1-6 marcam a cor, 0 remove
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.Delete && DataContext is LibraryViewModel deleteViewModel && deleteViewModel.SelectedCards.FirstOrDefault() is { } first) { deleteViewModel.RecycleFromMenuCommand.Execute(first); e.Handled = true; return; }
        if (e.Key == System.Windows.Input.Key.Enter && DataContext is LibraryViewModel viewModel && viewModel.HasSelectedPhoto) { viewModel.EnterReview(); e.Handled = true; }
    }

    private void SelectAll_Click(object sender, System.Windows.RoutedEventArgs e) => PhotoList.SelectAll();

    private void ClearSelection_Click(object sender, System.Windows.RoutedEventArgs e) => PhotoList.UnselectAll();

    private void FolderTree_SelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode node && DataContext is LibraryViewModel viewModel && !string.Equals(viewModel.CurrentFolderFilter, node.Path, StringComparison.OrdinalIgnoreCase))
            viewModel.SelectFolderCommand.Execute(node);
    }

    private void CollectionTree_SelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is LibraryViewModel viewModel)
            viewModel.SelectCollectionNode(e.NewValue as CollectionNode);
    }


    /// <summary>O item virtual "Todas" de uma coleção não tem menu de edição (não se renomeia, move nem exclui).</summary>
    private void CollectionNode_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: CollectionNode { IsAggregate: true } }) e.Handled = true;
    }
    private void CollectionTree_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not LibraryViewModel viewModel || viewModel.SelectedCollectionNode is null) return;
        if (e.Key == System.Windows.Input.Key.F2)
        {
            viewModel.RenameCollectionCommand.Execute(viewModel.SelectedCollectionNode);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Delete)
        {
            viewModel.DeleteCollectionCommand.Execute(viewModel.SelectedCollectionNode);
            e.Handled = true;
        }
    }

    private async void AddFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Escolha uma pasta para catalogar" };
        if (dialog.ShowDialog() == true && DataContext is LibraryViewModel viewModel)
            await viewModel.ImportFolderAsync(dialog.FolderName);
    }

    private async void SaveOrganization_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel) await viewModel.SaveSelectedAsync();
    }

    private async void MoveFiles_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel viewModel || PhotoList.SelectedItems.Count == 0) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Mover {PhotoList.SelectedItems.Count} foto(s) para…" };
        if (dialog.ShowDialog() == true) await viewModel.MoveSelectedAsync(SelectedCards, dialog.FolderName);
    }

    private async void CopyFiles_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel viewModel || PhotoList.SelectedItems.Count == 0) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Copiar {PhotoList.SelectedItems.Count} foto(s) para…" };
        if (dialog.ShowDialog() == true) await viewModel.CopySelectedAsync(SelectedCards, dialog.FolderName, AddCopyToCatalogBox.IsChecked == true);
    }

    private async void Rename_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel && !string.IsNullOrWhiteSpace(RenameTemplateBox.Text))
            await viewModel.RenameSelectionAsync(RenameTemplateBox.Text);
    }

    private void BatchMetadata_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (PhotoList.SelectedItems.Count == 0) return;
        // O editor de metadados (aba Metadados) abre com as fotos marcadas na grade.
        App.Services.GetRequiredService<PhotoManager.Application.Navigation.INavigationService>().Navigate("Metadata");
    }

    private async void RecycleFiles_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel viewModel || PhotoList.SelectedItems.Count == 0) return;
        var count = PhotoList.SelectedItems.Count;
        var result = System.Windows.MessageBox.Show($"{count} foto(s) serão enviadas para a Lixeira do Windows. Continuar?", "Confirmar exclusão", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result == System.Windows.MessageBoxResult.Yes) await viewModel.RecycleSelectedAsync(SelectedCards);
    }

    private void PhotoList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragCard = null;
        _isDragging = false;
        _deferredClick = PhotoManager.Application.Collections.PhotoClickAction.Default;
        for (var node = e.OriginalSource as System.Windows.DependencyObject; node is not null && !ReferenceEquals(node, PhotoList); node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;
            if (node is System.Windows.Controls.ListBoxItem { DataContext: PhotoCardViewModel card } item)
            {
                _dragCard = card;
                _dragStartPoint = e.GetPosition(PhotoList);
                if (DataContext is not LibraryViewModel viewModel) break;

                var modifiers = System.Windows.Input.Keyboard.Modifiers;
                var action = PhotoManager.Application.Collections.PhotoClickPolicy.Resolve(
                    viewModel.IsMultiSelectMode, item.IsSelected, PhotoList.SelectedItems.Count,
                    ctrl: (modifiers & System.Windows.Input.ModifierKeys.Control) != 0, shift: (modifiers & System.Windows.Input.ModifierKeys.Shift) != 0);
                if (action == PhotoManager.Application.Collections.PhotoClickAction.Default) break;

                // Tratamos o clique nós mesmos: o ListBox reduziria a seleção a esta foto já ao apertar, e o arraste levaria só uma.
                e.Handled = true;
                PhotoList.Focus();
                if (e.ClickCount == 2) viewModel.EnterReview(card);   // o duplo clique deixaria de chegar ao MouseDoubleClick
                else _deferredClick = action;
                break;
            }
        }
    }

    /// <summary>Soltou sem arrastar: aplica o clique adiado (alternar a foto em multi-seleção; ou reduzir a seleção a ela).</summary>
    private void PhotoList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var action = _deferredClick;
        _deferredClick = PhotoManager.Application.Collections.PhotoClickAction.Default;
        if (action == PhotoManager.Application.Collections.PhotoClickAction.Default || _dragCard is null || _isDragging) return;
        if (PhotoList.ItemContainerGenerator.ContainerFromItem(_dragCard) is not System.Windows.Controls.ListBoxItem item) return;

        if (action == PhotoManager.Application.Collections.PhotoClickAction.ToggleOnRelease)
        {
            item.IsSelected = !item.IsSelected;
        }
        else
        {
            PhotoList.SelectedItems.Clear();
            item.IsSelected = true;
        }
        e.Handled = true;
    }

    private void PhotoList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _dragCard is null || _isDragging) return;

        var currentPoint = e.GetPosition(PhotoList);
        var diff = currentPoint - _dragStartPoint;

        if (Math.Abs(diff.X) > System.Windows.SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > System.Windows.SystemParameters.MinimumVerticalDragDistance)
        {
            if (DataContext is not LibraryViewModel viewModel) return;

            var selectedCards = viewModel.SelectedCards;
            var selectedIds = selectedCards.Select(c => c.Photo.Id).ToList();
            var photoIdsToDrag = PhotoManager.Application.Collections.PhotoSelectionDragHelper.ResolvePhotoIdsToDrag(_dragCard.Photo.Id, selectedIds);

            _isDragging = true;
            try
            {
                var dragged = photoIdsToDrag.ToHashSet();
                var data = PhotoDragData.Create(photoIdsToDrag, viewModel.Photos.Where(card => dragged.Contains(card.Photo.Id)).Select(card => card.Photo));
                System.Windows.DragDrop.DoDragDrop(PhotoList, data, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move);
            }
            finally
            {
                _isDragging = false;
                _deferredClick = PhotoManager.Application.Collections.PhotoClickAction.Default;
                _dragCard = null;
                HideFeedbackPopup();
                ClearDragOverHighlight();
                StopExpandTimer();
            }
        }
    }

    private void CollectionTree_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragCollectionNode = null;
        _isDraggingCollection = false;

        // Se clicou em um botão/expansor (ex. seta de expandir/recolher TreeViewItem), não inicia drag
        for (var node = e.OriginalSource as System.Windows.DependencyObject; node is not null && !ReferenceEquals(node, CollectionTree); node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;
        }

        var pt = e.GetPosition(CollectionTree);
        var hitNode = FindCollectionNodeFromPoint(CollectionTree, pt);
        if (hitNode is { IsAggregate: true }) hitNode = null;   // o item virtual "Todas" não é arrastável
        if (hitNode is not null)
        {
            _dragCollectionNode = hitNode;
            _dragCollectionStartPoint = pt;
        }
    }

    private void CollectionTree_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _dragCollectionNode is null || _isDraggingCollection) return;

        var currentPoint = e.GetPosition(CollectionTree);
        var diff = currentPoint - _dragCollectionStartPoint;

        if (Math.Abs(diff.X) > System.Windows.SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > System.Windows.SystemParameters.MinimumVerticalDragDistance)
        {
            _isDraggingCollection = true;
            try
            {
                var data = new System.Windows.DataObject(PhotoManager.Application.Collections.CollectionDragDropFormats.CollectionId, _dragCollectionNode.Id);
                System.Windows.DragDrop.DoDragDrop(CollectionTree, data, System.Windows.DragDropEffects.Move);
            }
            finally
            {
                _isDraggingCollection = false;
                _dragCollectionNode = null;
                HideFeedbackPopup();
                ClearDragOverHighlight();
                StopExpandTimer();
                if (DataContext is LibraryViewModel vm) vm.IsDragOverRoot = false;
            }
        }
    }

    private void CollectionTree_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is not LibraryViewModel viewModel)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            HideFeedbackPopup();
            ClearDragOverHighlight();
            StopExpandTimer();
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.CollectionId))
        {
            var collectionId = (long)e.Data.GetData(PhotoManager.Application.Collections.CollectionDragDropFormats.CollectionId)!;
            var pt = e.GetPosition(CollectionTree);
            var targetNode = FindCollectionNodeFromPoint(CollectionTree, pt);

            AutoScrollSidebar(SidebarScrollViewer, e.GetPosition(SidebarScrollViewer));
            TriggerExpandTimer(targetNode);

            var plan = viewModel.PlanCollectionDrop(
                collectionId,
                targetNode?.Id,
                targetNode?.Name,
                targetIsRoot: false,
                targetIsVirtual: targetNode?.IsAggregate == true);

            SetDragOverHighlight(targetNode);

            e.Effects = plan.CanDrop ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
            ShowFeedbackPopup(e.GetPosition(this), plan.Message);
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.PhotoIds))
        {
            var photoIds = (long[])e.Data.GetData(PhotoManager.Application.Collections.CollectionDragDropFormats.PhotoIds)!;
            var pt = e.GetPosition(CollectionTree);
            var targetNode = FindCollectionNodeFromPoint(CollectionTree, pt);

            AutoScrollSidebar(SidebarScrollViewer, e.GetPosition(SidebarScrollViewer));
            TriggerExpandTimer(targetNode);

            var copyPressed = (e.KeyStates & (System.Windows.DragDropKeyStates.ShiftKey | System.Windows.DragDropKeyStates.ControlKey)) != 0;   // Ctrl ou Shift = copiar
            var plan = viewModel.PlanDrop(
                photoIds,
                targetNode?.Id,
                targetNode?.Name,
                targetIsVirtual: targetNode?.IsAggregate == true,
                isTargetHeaderOrEmpty: targetNode is null,
                copyPressed: copyPressed);

            SetDragOverHighlight(targetNode);

            if (plan.CanDrop)
            {
                e.Effects = plan.Action == PhotoManager.Application.Collections.DropAction.Move
                    ? System.Windows.DragDropEffects.Move
                    : System.Windows.DragDropEffects.Copy;
            }
            else
            {
                e.Effects = System.Windows.DragDropEffects.None;
            }

            ShowFeedbackPopup(e.GetPosition(this), plan.Message);
            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.None;
        HideFeedbackPopup();
        ClearDragOverHighlight();
        StopExpandTimer();
        e.Handled = true;
    }

    private void CollectionTree_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        ClearDragOverHighlight();
        HideFeedbackPopup();
        StopExpandTimer();
    }

    private async void CollectionTree_Drop(object sender, System.Windows.DragEventArgs e)
    {
        ClearDragOverHighlight();
        HideFeedbackPopup();
        StopExpandTimer();

        if (DataContext is not LibraryViewModel viewModel)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.CollectionId))
        {
            var collectionId = (long)e.Data.GetData(PhotoManager.Application.Collections.CollectionDragDropFormats.CollectionId)!;
            var pt = e.GetPosition(CollectionTree);
            var targetNode = FindCollectionNodeFromPoint(CollectionTree, pt);

            var plan = viewModel.PlanCollectionDrop(
                collectionId,
                targetNode?.Id,
                targetNode?.Name,
                targetIsRoot: false,
                targetIsVirtual: targetNode?.IsAggregate == true);

            if (plan.CanDrop)
            {
                await viewModel.ExecuteCollectionDropPlanAsync(plan);
            }
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.PhotoIds))
        {
            var photoIds = (long[])e.Data.GetData(PhotoManager.Application.Collections.CollectionDragDropFormats.PhotoIds)!;
            var pt = e.GetPosition(CollectionTree);
            var targetNode = FindCollectionNodeFromPoint(CollectionTree, pt);
            var copyPressed = (e.KeyStates & (System.Windows.DragDropKeyStates.ShiftKey | System.Windows.DragDropKeyStates.ControlKey)) != 0;   // Ctrl ou Shift = copiar

            var plan = viewModel.PlanDrop(
                photoIds,
                targetNode?.Id,
                targetNode?.Name,
                targetIsVirtual: targetNode?.IsAggregate == true,
                isTargetHeaderOrEmpty: targetNode is null,
                copyPressed: copyPressed);

            if (plan.Action == PhotoManager.Application.Collections.DropAction.AskMenu)
            {
                var screenPt = PointToScreen(e.GetPosition(this));
                ShowAskMenu(screenPt, plan, viewModel);
                e.Handled = true;
                return;
            }

            if (plan.CanDrop || plan.Action == PhotoManager.Application.Collections.DropAction.NoOp)
            {
                await viewModel.ExecuteDropPlanAsync(plan);
            }

            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void CollectionRoot_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is not LibraryViewModel viewModel) return;

        ClearDragOverHighlight();
        StopExpandTimer();

        if (e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.CollectionId))
        {
            viewModel.IsDragOverRoot = true;
            var collectionId = (long)e.Data.GetData(PhotoManager.Application.Collections.CollectionDragDropFormats.CollectionId)!;
            var plan = viewModel.PlanCollectionDrop(
                collectionId,
                targetCollectionId: null,
                targetCollectionName: null,
                targetIsRoot: true,
                targetIsVirtual: false);

            e.Effects = plan.CanDrop ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
            ShowFeedbackPopup(e.GetPosition(this), plan.Message);
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.PhotoIds))
        {
            viewModel.IsDragOverRoot = false;
            e.Effects = System.Windows.DragDropEffects.None;
            ShowFeedbackPopup(e.GetPosition(this), "Não é possível soltar fotos no cabeçalho raiz.");
            e.Handled = true;
            return;
        }

        viewModel.IsDragOverRoot = false;
        e.Effects = System.Windows.DragDropEffects.None;
        HideFeedbackPopup();
        e.Handled = true;
    }

    private void CollectionRoot_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel) viewModel.IsDragOverRoot = false;
        HideFeedbackPopup();
    }

    private async void CollectionRoot_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel) viewModel.IsDragOverRoot = false;
        HideFeedbackPopup();

        if (DataContext is not LibraryViewModel vm)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.CollectionId))
        {
            var collectionId = (long)e.Data.GetData(PhotoManager.Application.Collections.CollectionDragDropFormats.CollectionId)!;
            var plan = vm.PlanCollectionDrop(
                collectionId,
                targetCollectionId: null,
                targetCollectionName: null,
                targetIsRoot: true,
                targetIsVirtual: false);

            if (plan.CanDrop)
            {
                await vm.ExecuteCollectionDropPlanAsync(plan);
            }
            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void VirtualCollections_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.CollectionId))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            ShowFeedbackPopup(e.GetPosition(this), "Não é possível mover coleções para itens virtuais (“Todas” / “Sem coleção”).");
            ClearDragOverHighlight();
            StopExpandTimer();
            if (DataContext is LibraryViewModel vm) vm.IsDragOverRoot = false;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.PhotoIds))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            ShowFeedbackPopup(e.GetPosition(this), "Não é possível soltar em itens virtuais (“Todas” / “Sem coleção”).");
            ClearDragOverHighlight();
            StopExpandTimer();
            if (DataContext is LibraryViewModel vm) vm.IsDragOverRoot = false;
            e.Handled = true;
        }
    }

    private void VirtualCollections_Drop(object sender, System.Windows.DragEventArgs e)
    {
        ClearDragOverHighlight();
        HideFeedbackPopup();
        StopExpandTimer();
        if (DataContext is LibraryViewModel vm) vm.IsDragOverRoot = false;
        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }


    // ---------- soltar fotos numa PASTA da árvore (física): arrastar move o arquivo; Ctrl/Shift copia ----------
    private FolderNode? _currentFolderDragOver;

    private void SetFolderHighlight(FolderNode? node)
    {
        if (ReferenceEquals(_currentFolderDragOver, node)) return;
        if (_currentFolderDragOver is not null) _currentFolderDragOver.IsDragOver = false;
        _currentFolderDragOver = node;
        if (node is not null) node.IsDragOver = true;
    }

    private static FolderNode? FindFolderNodeFromPoint(System.Windows.Controls.TreeView tree, System.Windows.Point pt)
    {
        var hit = tree.InputHitTest(pt) as System.Windows.DependencyObject;
        while (hit != null && !ReferenceEquals(hit, tree))
        {
            if (hit is System.Windows.FrameworkElement { DataContext: FolderNode node }) return node;
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    private static bool IsCopyKey(System.Windows.DragEventArgs e) =>
        (e.KeyStates & (System.Windows.DragDropKeyStates.ShiftKey | System.Windows.DragDropKeyStates.ControlKey)) != 0;

    private void FolderTree_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (IsExternalFileDrop(e) && DataContext is LibraryViewModel externalViewModel)
        {
            var target = FindFolderNodeFromPoint(FolderTree, e.GetPosition(FolderTree));
            var externalPlan = externalViewModel.PlanExternalDrop(DroppedPaths(e), target, CtrlDown(e), ShiftDown(e));
            SetFolderHighlight(externalPlan.CanDrop ? target : null);
            e.Effects = EffectsFor(externalPlan);
            externalViewModel.IsExternalDragOver = false;
            ShowFeedbackPopup(e.GetPosition(this), externalPlan.Message);
            e.Handled = true;
            return;
        }

        if (DataContext is not LibraryViewModel viewModel || !e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.PhotoIds))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            SetFolderHighlight(null);
            HideFeedbackPopup();
            e.Handled = true;
            return;
        }

        var photoIds = (long[])e.Data.GetData(PhotoManager.Application.Collections.CollectionDragDropFormats.PhotoIds)!;
        var node = FindFolderNodeFromPoint(FolderTree, e.GetPosition(FolderTree));
        AutoScrollSidebar(SidebarScrollViewer, e.GetPosition(SidebarScrollViewer));

        var plan = viewModel.PlanFolderDrop(photoIds, node, IsCopyKey(e));
        SetFolderHighlight(plan.CanDrop ? node : null);
        e.Effects = !plan.CanDrop ? System.Windows.DragDropEffects.None
            : plan.Action == PhotoManager.Application.Catalog.FolderDropAction.Copy ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.Move;
        ShowFeedbackPopup(e.GetPosition(this), plan.Message);
        e.Handled = true;
    }

    private void FolderTree_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        SetFolderHighlight(null);
        HideFeedbackPopup();
    }

    private async void FolderTree_Drop(object sender, System.Windows.DragEventArgs e)
    {
        SetFolderHighlight(null);
        HideFeedbackPopup();
        e.Handled = true;
        if (IsExternalFileDrop(e) && DataContext is LibraryViewModel externalViewModel)
        {
            var target = FindFolderNodeFromPoint(FolderTree, e.GetPosition(FolderTree));
            await externalViewModel.ExecuteExternalDropAsync(externalViewModel.PlanExternalDrop(DroppedPaths(e), target, CtrlDown(e), ShiftDown(e)));
            return;
        }
        if (DataContext is not LibraryViewModel viewModel || !e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.PhotoIds)) return;

        var photoIds = (long[])e.Data.GetData(PhotoManager.Application.Collections.CollectionDragDropFormats.PhotoIds)!;
        var node = FindFolderNodeFromPoint(FolderTree, e.GetPosition(FolderTree));
        var plan = viewModel.PlanFolderDrop(photoIds, node, IsCopyKey(e));
        await viewModel.ExecuteFolderDropAsync(plan);
    }

    // ---------- soltar arquivos/pastas vindos do Explorer ----------
    private static bool IsExternalFileDrop(System.Windows.DragEventArgs e) =>
        e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
        && !e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.PhotoIds)
        && !e.Data.GetDataPresent(PhotoManager.Application.Collections.CollectionDragDropFormats.CollectionId);

    private static string[] DroppedPaths(System.Windows.DragEventArgs e) => e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[] ?? [];

    private static bool CtrlDown(System.Windows.DragEventArgs e) => (e.KeyStates & System.Windows.DragDropKeyStates.ControlKey) != 0;
    private static bool ShiftDown(System.Windows.DragEventArgs e) => (e.KeyStates & System.Windows.DragDropKeyStates.ShiftKey) != 0;

    private static System.Windows.DragDropEffects EffectsFor(PhotoManager.Application.Catalog.ExternalDropPlan plan) => !plan.CanDrop ? System.Windows.DragDropEffects.None
        : plan.Action == PhotoManager.Application.Catalog.ExternalDropAction.Move ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.Copy;

    /// <summary>Soltar na área da biblioteca: cataloga os arquivos/pastas no lugar (não copia nem move nada).</summary>
    private void ExternalFiles_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!IsExternalFileDrop(e) || DataContext is not LibraryViewModel viewModel) return;
        var plan = viewModel.PlanExternalDrop(DroppedPaths(e), null, ctrl: false, shift: false);
        viewModel.IsExternalDragOver = plan.CanDrop;
        viewModel.ExternalDragText = plan.Message;
        e.Effects = EffectsFor(plan);
        e.Handled = true;
    }

    private void ExternalFiles_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is LibraryViewModel viewModel) viewModel.IsExternalDragOver = false;
    }

    private async void ExternalFiles_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!IsExternalFileDrop(e) || DataContext is not LibraryViewModel viewModel) return;
        e.Handled = true;
        var plan = viewModel.PlanExternalDrop(DroppedPaths(e), null, ctrl: false, shift: false);
        await viewModel.ExecuteExternalDropAsync(plan);
    }
    private void ShowFeedbackPopup(System.Windows.Point pt, string message)
    {
        if (DragFeedbackPopup == null) return;
        var screenPt = PointToScreen(pt);
        DragFeedbackPopup.HorizontalOffset = screenPt.X + 16;
        DragFeedbackPopup.VerticalOffset = screenPt.Y + 16;
        DragFeedbackText.Text = message;
        if (!DragFeedbackPopup.IsOpen) DragFeedbackPopup.IsOpen = true;
    }

    private void HideFeedbackPopup()
    {
        if (DragFeedbackPopup != null && DragFeedbackPopup.IsOpen)
            DragFeedbackPopup.IsOpen = false;
    }

    private void SetDragOverHighlight(CollectionNode? node)
    {
        if (_currentDragOverNode == node) return;
        if (_currentDragOverNode != null) _currentDragOverNode.IsDragOver = false;
        _currentDragOverNode = node;
        if (_currentDragOverNode != null) _currentDragOverNode.IsDragOver = true;
    }

    private void ClearDragOverHighlight()
    {
        if (_currentDragOverNode != null)
        {
            _currentDragOverNode.IsDragOver = false;
            _currentDragOverNode = null;
        }
    }

    private void TriggerExpandTimer(CollectionNode? node)
    {
        if (_expandTimer == null)
        {
            _expandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _expandTimer.Tick += (_, _) =>
            {
                _expandTimer.Stop();
                if (_hoverExpandNode != null && _hoverExpandNode.Children.Count > 0 && !_hoverExpandNode.IsExpanded)
                {
                    _hoverExpandNode.IsExpanded = true;
                }
            };
        }

        if (node != _hoverExpandNode)
        {
            _expandTimer.Stop();
            _hoverExpandNode = node;
            if (node != null && node.Children.Count > 0 && !node.IsExpanded)
            {
                _expandTimer.Start();
            }
        }
    }

    private void StopExpandTimer()
    {
        _expandTimer?.Stop();
        _hoverExpandNode = null;
    }

    private static void AutoScrollSidebar(System.Windows.Controls.ScrollViewer? scrollViewer, System.Windows.Point pt)
    {
        if (scrollViewer == null) return;
        const double threshold = 30;
        const double step = 10;
        if (pt.Y < threshold)
        {
            scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - step));
        }
        else if (pt.Y > scrollViewer.ActualHeight - threshold)
        {
            scrollViewer.ScrollToVerticalOffset(Math.Min(scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset + step));
        }
    }

    private static CollectionNode? FindCollectionNodeFromPoint(System.Windows.Controls.TreeView tree, System.Windows.Point pt)
    {
        var hit = tree.InputHitTest(pt) as System.Windows.DependencyObject;
        while (hit != null && !ReferenceEquals(hit, tree))
        {
            if (hit is System.Windows.FrameworkElement { DataContext: CollectionNode node })
                return node;
            hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    private void ShowAskMenu(System.Windows.Point screenPoint, PhotoManager.Application.Collections.DropPlan plan, LibraryViewModel viewModel)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var opt in plan.AskMenuOptions)
        {
            var item = new System.Windows.Controls.MenuItem { Header = opt.Header };
            if (opt.Action == PhotoManager.Application.Collections.DropAction.Add)
            {
                item.Click += async (_, _) =>
                {
                    var addPlan = new PhotoManager.Application.Collections.DropPlan(
                        PhotoManager.Application.Collections.DropAction.Add,
                        plan.TargetCollectionId,
                        plan.TargetCollectionName,
                        null,
                        null,
                        plan.PhotoIdsToAdd,
                        plan.PhotoIdsAlreadyInTarget,
                        [],
                        [],
                        plan.Message,
                        true);
                    await viewModel.ExecuteDropPlanAsync(addPlan);
                };
            }
            else if (opt.Action == PhotoManager.Application.Collections.DropAction.Move)
            {
                item.Click += async (_, _) =>
                {
                    var movePlan = new PhotoManager.Application.Collections.DropPlan(
                        PhotoManager.Application.Collections.DropAction.Move,
                        plan.TargetCollectionId,
                        plan.TargetCollectionName,
                        opt.SourceCollectionId,
                        opt.SourceCollectionName,
                        [],
                        [],
                        plan.PhotoIdsToAdd,
                        [],
                        plan.Message,
                        true);
                    await viewModel.ExecuteDropPlanAsync(movePlan);
                };
            }
            menu.Items.Add(item);
        }
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = screenPoint.X;
        menu.VerticalOffset = screenPoint.Y;
        menu.IsOpen = true;
    }
}
