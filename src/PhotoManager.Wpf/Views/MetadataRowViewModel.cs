using System.Collections.ObjectModel;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Metadata;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Commands;

namespace PhotoManager.Wpf.Views;

/// <summary>
/// Rascunho de metadados de uma foto no editor. O que está nos campos nunca vai ao arquivo sozinho: só o "Salvar" grava
/// (pelo pipeline seguro). A linha guarda o estado lido do arquivo (<c>baseline</c>) para marcar o que foi alterado e permitir reverter.
/// </summary>
public sealed class MetadataRowViewModel : ViewModels.ViewModelBase
{
    private MetadataEdit _baseline = new();
    private string _title = string.Empty, _description = string.Empty, _author = string.Empty, _copyright = string.Empty, _newKeyword = string.Empty;
    private bool _isLoaded, _isLoading, _isIncluded = true, _suspend, _saveFailed;
    private string? _loadError;
    private string _saveMessage = string.Empty;
    private PhotoMetadata? _metadata;

    public MetadataRowViewModel(PhotoCardViewModel card)
    {
        Card = card;
        IsEditable = !card.IsMissing && MetadataFormats.CanEdit(card.Photo.Extension);
        Keywords.CollectionChanged += (_, _) => OnEdited();
        AddKeywordCommand = new RelayCommand(_ => AddKeywords(), _ => CanEdit);
        RemoveKeywordCommand = new RelayCommand(p => { if (p is string keyword) Keywords.Remove(keyword); }, _ => CanEdit);
        RevertCommand = new RelayCommand(_ => Revert(), _ => IsChanged);
    }

    public PhotoCardViewModel Card { get; }
    public Photo Photo => Card.Photo;
    public string FileName => Card.FileName;
    public string FormatText => Card.StatusText;
    public Uri? ThumbnailUri => Card.ThumbnailUri;
    /// <summary>Só JPEG existente é gravável (PNG/WEBP/RAW/ausente aparecem desabilitados).</summary>
    public bool IsEditable { get; }
    public ObservableCollection<string> Keywords { get; } = [];
    public RelayCommand AddKeywordCommand { get; }
    public RelayCommand RemoveKeywordCommand { get; }
    public RelayCommand RevertCommand { get; }

    /// <summary>Disparado a cada alteração dos campos (o editor recalcula o resumo).</summary>
    public event Action? Edited;

    public bool IsLoaded { get => _isLoaded; private set { _isLoaded = value; NotifyState(); } }
    public bool IsLoading { get => _isLoading; set { _isLoading = value; NotifyState(); } }
    public string? LoadError { get => _loadError; set { _loadError = value; NotifyState(); } }
    public PhotoMetadata? Metadata => _metadata;
    /// <summary>Marcada = entra nas operações globais.</summary>
    public bool IsIncluded { get => _isIncluded; set { _isIncluded = value; OnPropertyChanged(); Edited?.Invoke(); } }
    public bool CanEdit => IsEditable && IsLoaded;

    public string TitleText { get => _title; set { _title = value; OnEdited(); } }
    public string DescriptionText { get => _description; set { _description = value; OnEdited(); } }
    public string AuthorText { get => _author; set { _author = value; OnEdited(); } }
    public string CopyrightText { get => _copyright; set { _copyright = value; OnEdited(); } }
    public string NewKeyword { get => _newKeyword; set { _newKeyword = value; OnPropertyChanged(); } }

    public bool IsChanged => IsEditable && IsLoaded && !CurrentEdit().SameAs(_baseline);
    public IReadOnlyList<string> ChangedFields => _baseline.ChangedFields(CurrentEdit());
    public bool TitleChanged => ChangedFields.Contains("Título");
    public bool DescriptionChanged => ChangedFields.Contains("Descrição");
    public bool KeywordsChanged => ChangedFields.Contains("Palavras-chave");
    public bool AuthorChanged => ChangedFields.Contains("Autor");
    public bool CopyrightChanged => ChangedFields.Contains("Copyright");

    public string TitleCounter => $"{TitleText.Trim().Length}/{MetadataEdit.TitleLimit}";
    public string DescriptionCounter => $"{DescriptionText.Trim().Length}/{MetadataEdit.DescriptionLimit}";
    public string KeywordCounter => $"{Keywords.Count}/{MetadataEdit.KeywordLimit}";
    public bool TitleOver => TitleText.Trim().Length > MetadataEdit.TitleLimit;
    public bool DescriptionOver => DescriptionText.Trim().Length > MetadataEdit.DescriptionLimit;
    public bool KeywordsOver => Keywords.Count > MetadataEdit.KeywordLimit;

    public bool SaveFailed => _saveFailed;
    public string SaveMessage { get => _saveMessage; private set { _saveMessage = value; NotifyState(); } }

    /// <summary>Texto curto para a linha: o que acontece com esta foto.</summary>
    public string StatusText
    {
        get
        {
            if (Card.IsMissing) return "Arquivo ausente";
            if (!IsEditable) return $"Somente leitura ({FormatText})";
            if (LoadError is not null) return LoadError;
            if (IsLoading || !IsLoaded) return "Lendo metadados…";
            if (_saveFailed) return SaveMessage;
            if (IsChanged) return "Alterada: " + string.Join(", ", ChangedFields);
            return SaveMessage.Length > 0 ? SaveMessage : "Sem alterações";
        }
    }

    /// <summary>"changed" | "error" | "muted" | "ok" — usado para colorir o status.</summary>
    public string StatusKind => (Card.IsMissing || LoadError is not null || _saveFailed) ? "error" : !IsEditable ? "muted" : IsChanged ? "changed" : SaveMessage.Length > 0 ? "ok" : "muted";

    public MetadataEdit CurrentEdit() => new MetadataEdit { Title = TitleText, Description = DescriptionText, Keywords = Keywords.ToList(), Author = AuthorText, Copyright = CopyrightText }.Normalize();

    /// <summary>Define o estado lido do arquivo: campos e baseline.</summary>
    public void SetBaseline(PhotoMetadata metadata)
    {
        _metadata = metadata;
        if (metadata.Error is not null) { LoadError = metadata.Error; IsLoading = false; return; }
        _baseline = MetadataEdit.From(metadata);
        Load(_baseline);
        _saveFailed = false;
        LoadError = null;
        _isLoading = false;
        IsLoaded = true;
    }

    /// <summary>Aplica valores (de uma operação global, por exemplo) aos campos sem mexer no baseline.</summary>
    public void Apply(MetadataEdit edit) => Load(edit);

    public void Revert() { Load(_baseline); _saveFailed = false; SaveMessage = string.Empty; }

    public void DedupeKeywords()
    {
        var unique = Keywords.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unique.Count != Keywords.Count) Load(CurrentEdit() with { Keywords = unique });
    }

    public void MarkSaved(string message) { _saveFailed = false; SaveMessage = message; }
    public void MarkFailed(string message) { _saveFailed = true; SaveMessage = message; }

    private void Load(MetadataEdit edit)
    {
        _suspend = true;
        _title = edit.Title ?? string.Empty; _description = edit.Description ?? string.Empty; _author = edit.Author ?? string.Empty; _copyright = edit.Copyright ?? string.Empty;
        Keywords.Clear();
        foreach (var keyword in edit.Keywords) Keywords.Add(keyword);
        _suspend = false;
        OnEdited();
    }

    private void AddKeywords()
    {
        foreach (var keyword in NewKeyword.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase)) Keywords.Add(keyword);
        NewKeyword = string.Empty;
    }

    private void OnEdited()
    {
        if (_suspend) return;
        foreach (var name in new[] { nameof(TitleText), nameof(DescriptionText), nameof(AuthorText), nameof(CopyrightText), nameof(IsChanged), nameof(ChangedFields), nameof(TitleChanged), nameof(DescriptionChanged),
                     nameof(KeywordsChanged), nameof(AuthorChanged), nameof(CopyrightChanged), nameof(TitleCounter), nameof(DescriptionCounter), nameof(KeywordCounter), nameof(TitleOver), nameof(DescriptionOver),
                     nameof(KeywordsOver), nameof(StatusText), nameof(StatusKind) })
            OnPropertyChanged(name);
        RevertCommand.RaiseCanExecuteChanged();
        Edited?.Invoke();
    }

    private void NotifyState()
    {
        foreach (var name in new[] { nameof(IsLoaded), nameof(IsLoading), nameof(LoadError), nameof(CanEdit), nameof(StatusText), nameof(StatusKind), nameof(SaveFailed), nameof(SaveMessage), nameof(IsChanged) })
            OnPropertyChanged(name);
        AddKeywordCommand.RaiseCanExecuteChanged(); RemoveKeywordCommand.RaiseCanExecuteChanged(); RevertCommand.RaiseCanExecuteChanged();
        Edited?.Invoke();
    }
}
