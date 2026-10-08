using System.Windows.Media;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Commands;
using PhotoManager.Wpf.ViewModels;

namespace PhotoManager.Wpf.Views;

/// <summary>Um item na comparação lado a lado: imagem grande, nome, nota, nitidez e as ações de triagem.</summary>
public sealed class CompareItemViewModel(PhotoCardViewModel card) : ViewModelBase
{
    private ImageSource? _image;
    public PhotoCardViewModel Card { get; } = card;
    public ImageSource? Image { get => _image; set { _image = value; OnPropertyChanged(); } }
    public string Caption => $"{Card.FileName}  ·  {Card.Dimensions}" + (Card.Photo.Sharpness is { } s && !Card.IsVideo ? $"  ·  nitidez {s:0}" : string.Empty);
}

/// <summary>Comparar 2 a 4 itens: escolher o melhor e rejeitar os outros num clique (a decisão vai para o catálogo).</summary>
public sealed class CompareViewModel : ViewModelBase
{
    private readonly LibraryViewModel _library;

    public CompareViewModel(LibraryViewModel library, IReadOnlyList<PhotoCardViewModel> cards)
    {
        _library = library;
        Items = cards.Select(c => new CompareItemViewModel(c)).ToList();
        PickCommand = new RelayCommand(p => { if (p is CompareItemViewModel item) _ = _library.SetPickAsync([item.Card], PickFlag.Picked, advance: false); });
        RejectCommand = new RelayCommand(p => { if (p is CompareItemViewModel item) _ = _library.SetPickAsync([item.Card], PickFlag.Rejected, advance: false); });
        KeepOnlyCommand = new RelayCommand(p => { if (p is CompareItemViewModel item) _ = KeepOnlyAsync(item); });
        _ = LoadImagesAsync();
    }

    public IReadOnlyList<CompareItemViewModel> Items { get; }
    public int Columns => Items.Count <= 2 ? Items.Count : 2;
    public RelayCommand PickCommand { get; }
    public RelayCommand RejectCommand { get; }
    public RelayCommand KeepOnlyCommand { get; }

    /// <summary>"Ficar com este": ele é escolhido e todos os outros são rejeitados.</summary>
    public async Task KeepOnlyAsync(CompareItemViewModel keep)
    {
        await _library.SetPickAsync([keep.Card], PickFlag.Picked, advance: false);
        await _library.SetPickAsync(Items.Where(i => i != keep).Select(i => i.Card).ToList(), PickFlag.Rejected, advance: false);
    }

    private async Task LoadImagesAsync()
    {
        foreach (var item in Items)
        {
            if (item.Card.IsMissing) continue;
            var path = item.Card.Photo.CurrentPath;
            var rotation = item.Card.Photo.UserRotation;
            item.Image = await Task.Run(() => item.Card.IsVideo ? null
                : PhotoManager.Infrastructure.Images.ImageLoader.LoadPreview(path, 1600) is { } bitmap ? PhotoManager.Infrastructure.Images.ExifOrientation.Rotate(bitmap, rotation) : null)
                ?? (item.Card.ThumbnailUri is { } uri ? new System.Windows.Media.Imaging.BitmapImage(uri) : null);
        }
    }
}
