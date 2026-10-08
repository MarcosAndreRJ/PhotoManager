namespace PhotoManager.Application.Collections;

/// <summary>O que fazer com o clique do botão esquerdo numa foto da grade.</summary>
public enum PhotoClickAction
{
    /// <summary>Deixa o ListBox tratar na hora (clique simples, Ctrl/Shift em modo normal).</summary>
    Default,
    /// <summary>Multi-seleção: o clique só alterna a foto quando o botão é solto sem ter virado arraste.</summary>
    ToggleOnRelease,
    /// <summary>Clique numa foto que já está numa seleção múltipla: a seleção só se reduz a ela ao soltar sem arraste (como no Explorer).</summary>
    CollapseOnRelease
}

public static class PhotoClickPolicy
{
    /// <summary>
    /// Adiar a decisão para o soltar do botão é o que permite arrastar várias fotos: se o ListBox tratasse o clique ao apertar,
    /// a seleção cairia para uma foto antes de o arraste começar.
    /// </summary>
    public static PhotoClickAction Resolve(bool multiSelectMode, bool clickedIsSelected, int selectedCount, bool ctrl, bool shift)
    {
        if (shift) return PhotoClickAction.Default;                       // seleção em intervalo continua do ListBox
        if (multiSelectMode) return PhotoClickAction.ToggleOnRelease;     // clicar na mídia marca/desmarca, sem precisar acertar a caixa
        if (ctrl) return PhotoClickAction.Default;
        return clickedIsSelected && selectedCount > 1 ? PhotoClickAction.CollapseOnRelease : PhotoClickAction.Default;
    }
}
