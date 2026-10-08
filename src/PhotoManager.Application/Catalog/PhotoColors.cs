using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Catalog;

/// <summary>Nomes e cores (hexadecimal) das etiquetas de cor, em um só lugar para menus, selos, filtros e documentação.</summary>
public static class PhotoColors
{
    public static IReadOnlyList<PhotoColor> All { get; } = [PhotoColor.Red, PhotoColor.Orange, PhotoColor.Yellow, PhotoColor.Green, PhotoColor.Blue, PhotoColor.Purple];

    public static string Name(PhotoColor color) => color switch
    {
        PhotoColor.Red => "Vermelho",
        PhotoColor.Orange => "Laranja",
        PhotoColor.Yellow => "Amarelo",
        PhotoColor.Green => "Verde",
        PhotoColor.Blue => "Azul",
        PhotoColor.Purple => "Roxo",
        _ => "Sem cor"
    };

    public static string Hex(PhotoColor color) => color switch
    {
        PhotoColor.Red => "#EF4444",
        PhotoColor.Orange => "#F97316",
        PhotoColor.Yellow => "#EAB308",
        PhotoColor.Green => "#22C55E",
        PhotoColor.Blue => "#3B82F6",
        PhotoColor.Purple => "#A855F7",
        _ => "#00000000"
    };

    /// <summary>Tecla numérica 1-6 → cor; 0 → remover; qualquer outra → nulo.</summary>
    public static PhotoColor? FromDigit(int digit) => digit switch
    {
        0 => PhotoColor.None,
        >= 1 and <= 6 => (PhotoColor)digit,
        _ => null
    };

    /// <summary>Nomes do filtro por cor: "Qualquer", "Sem cor" e as seis cores.</summary>
    public static IReadOnlyList<string> FilterChoices { get; } = ["Qualquer", "Sem cor", .. All.Select(Name)];

    /// <summary>Cor escolhida no filtro: <c>null</c> = qualquer; <see cref="PhotoColor.None"/> = sem cor.</summary>
    public static PhotoColor? FromFilterChoice(string? choice) =>
        string.IsNullOrEmpty(choice) || choice == "Qualquer" ? null
        : choice == "Sem cor" ? PhotoColor.None
        : All.Cast<PhotoColor?>().FirstOrDefault(c => Name(c!.Value) == choice);
}
