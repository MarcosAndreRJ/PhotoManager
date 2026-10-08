using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using PhotoManager.Application.Metadata;
using PhotoManager.Wpf.Commands;

namespace PhotoManager.Wpf.Views;

public sealed record OperationChoice(BatchOperation Operation, string Label);
public sealed record EditorLine(string Title, string Details);

/// <summary>
/// Editor de metadados (aba Metadados): lista das fotos selecionadas com edição por linha + operações globais por campo
/// aplicadas aos <b>rascunhos</b>. Nada vai ao arquivo até "Salvar" (pipeline seguro por foto: temporário → validação → substituição).
/// </summary>
public sealed class MetadataEditorViewModel : ViewModels.ViewModelBase
{
    private static readonly Dictionary<BatchOperation, string> Labels = new()
    {
        [BatchOperation.Keep] = "Manter", [BatchOperation.Replace] = "Substituir", [BatchOperation.Append] = "Acrescentar",
        [BatchOperation.Add] = "Adicionar", [BatchOperation.Remove] = "Remover", [BatchOperation.Clear] = "Limpar"
    };
    private const int LoadConcurrency = 4;

    private readonly IMetadataReader _reader;
    private readonly IMetadataEditService _editor;
    private readonly IMetadataPresetRepository? _presets;
    private readonly Dictionary<long, MetadataRowViewModel> _cache = [];
    private readonly SemaphoreSlim _loadGate = new(LoadConcurrency);
    private MetadataRowViewModel? _focused;
    private bool _isActive, _isBusy;
    private BatchOperation _titleOp, _descriptionOp, _keywordsOp, _authorOp, _copyrightOp;
    private string _title = string.Empty, _description = string.Empty, _author = string.Empty, _copyright = string.Empty, _newKeyword = string.Empty, _presetName = string.Empty;
    private MetadataPreset? _selectedPreset;
    private string _statusText = string.Empty;
    private bool _statusIsError;
    private int _progress, _focusVersion;
    private CancellationTokenSource? _cts;

    public MetadataEditorViewModel(LibraryViewModel library, IMetadataReader reader, IMetadataEditService editor, IMetadataPresetRepository? presets = null)
    {
        Library = library;
        _reader = reader;
        _editor = editor;
        _presets = presets;
        library.PropertyChanged += OnLibraryChanged;
        Keywords.CollectionChanged += (_, _) => OnPlanChanged();
        AddKeywordCommand = new RelayCommand(_ => AddKeywords());
        RemoveKeywordCommand = new RelayCommand(p => { if (p is string keyword) Keywords.Remove(keyword); });
        ApplyGlobalCommand = new RelayCommand(_ => _ = ApplyGlobalAsync(), _ => CanApplyGlobal);
        ClearFormCommand = new RelayCommand(_ => ClearForm());
        CopyFromFocusedCommand = new RelayCommand(_ => _ = CopyFromFocusedAsync(), _ => !IsBusy && Focused is { IsEditable: true } && IncludedEditableCount > (Focused.IsIncluded ? 1 : 0));
        DedupeKeywordsCommand = new RelayCommand(_ => DedupeKeywords(), _ => !IsBusy && IncludedEditableCount > 0);
        IncludeAllCommand = new RelayCommand(_ => SetIncluded(true));
        IncludeNoneCommand = new RelayCommand(_ => SetIncluded(false));
        SaveAllCommand = new RelayCommand(_ => _ = SaveAllAsync(), _ => CanSaveAll);
        SaveRowCommand = new RelayCommand(p => { if (p is MetadataRowViewModel row) _ = SaveRowsAsync([row]); }, p => !IsBusy && p is MetadataRowViewModel { IsChanged: true });
        RevertAllCommand = new RelayCommand(_ => RevertAll(), _ => !IsBusy && ChangedCount > 0);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBusy);
        LoadPresetCommand = new RelayCommand(_ => LoadPreset(), _ => SelectedPreset is not null);
        SavePresetCommand = new RelayCommand(_ => _ = SavePresetAsync(), _ => _presets is not null && !IsBusy && PresetName.Trim().Length > 0 && Plan().Validate().Count == 0 && !Plan().IsNoOp);
        DeletePresetCommand = new RelayCommand(_ => _ = DeletePresetAsync(), _ => _presets is not null && SelectedPreset is not null && !IsBusy);
        _ = LoadPresetsAsync();
    }

    public LibraryViewModel Library { get; }
    /// <summary>Confirmação antes de descartar rascunhos (recebe a quantidade); definida pela View. Nulo = descarta sem perguntar.</summary>
    public Func<int, bool>? ConfirmRevert { get; set; }

    public ObservableCollection<MetadataRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> Keywords { get; } = [];
    public ObservableCollection<MetadataPreset> Presets { get; } = [];
    public ObservableCollection<EditorLine> FocusedHistory { get; } = [];
    public ObservableCollection<OperationChoice> TitleOperations { get; } = Choices(nameof(BatchMetadataPlan.Title));
    public ObservableCollection<OperationChoice> DescriptionOperations { get; } = Choices(nameof(BatchMetadataPlan.Description));
    public ObservableCollection<OperationChoice> KeywordOperations { get; } = Choices(nameof(BatchMetadataPlan.Keywords));
    public ObservableCollection<OperationChoice> AuthorOperations { get; } = Choices(nameof(BatchMetadataPlan.Author));
    public ObservableCollection<OperationChoice> CopyrightOperations { get; } = Choices(nameof(BatchMetadataPlan.Copyright));

    public RelayCommand AddKeywordCommand { get; }
    public RelayCommand RemoveKeywordCommand { get; }
    public RelayCommand ApplyGlobalCommand { get; }
    public RelayCommand ClearFormCommand { get; }
    public RelayCommand CopyFromFocusedCommand { get; }
    public RelayCommand DedupeKeywordsCommand { get; }
    public RelayCommand IncludeAllCommand { get; }
    public RelayCommand IncludeNoneCommand { get; }
    public RelayCommand SaveAllCommand { get; }
    public RelayCommand SaveRowCommand { get; }
    public RelayCommand RevertAllCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand LoadPresetCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }

    // ---------- resumo
    public int RowCount => Rows.Count;
    public int IncludedCount => Rows.Count(r => r.IsIncluded);
    public int IncludedEditableCount => Rows.Count(r => r.IsIncluded && r.IsEditable);
    public int ChangedCount => Rows.Count(r => r.IsChanged);
    public int ReadOnlyCount => Rows.Count(r => !r.IsEditable);
    public bool HasRows => Rows.Count > 0;
    public string SummaryText
    {
        get
        {
            if (Rows.Count == 0) return "Nenhuma foto no editor.";
            var parts = new List<string> { $"{Rows.Count} foto(s)", $"{IncludedCount} marcada(s)", $"{ChangedCount} alterada(s)" };
            if (ReadOnlyCount > 0) parts.Add($"{ReadOnlyCount} somente leitura");
            return string.Join(" · ", parts);
        }
    }
    public string SaveButtonText => ChangedCount == 0 ? "Salvar" : $"Salvar {ChangedCount} alterada(s)";
    public bool CanSaveAll => !IsBusy && ChangedCount > 0;
    public string GlobalImpactText
    {
        get
        {
            var plan = Plan();
            if (plan.IsNoOp) return "Escolha uma operação em algum campo (“Manter” não altera nada).";
            var problems = plan.Validate();
            if (problems.Count > 0) return string.Join("  ", problems);
            if (IncludedEditableCount == 0)
            {
                if (IncludedCount == 0) return "Marque ao menos uma foto para aplicar.";
                var formats = string.Join(", ", Rows.Where(r => r.IsIncluded && !r.IsEditable).Select(r => r.FormatText).Distinct());
                return $"Nenhuma das {IncludedCount} foto(s) marcada(s) pode ser alterada: estão ausentes ou são de um formato sem suporte à gravação ({formats}).";
            }
            var fields = new List<string>();
            if (plan.TitleOperation != BatchOperation.Keep) fields.Add("Título");
            if (plan.DescriptionOperation != BatchOperation.Keep) fields.Add("Descrição");
            if (plan.KeywordsOperation != BatchOperation.Keep) fields.Add("Palavras-chave");
            if (plan.AuthorOperation != BatchOperation.Keep) fields.Add("Autor");
            if (plan.CopyrightOperation != BatchOperation.Keep) fields.Add("Copyright");
            return $"Será aplicado a {IncludedEditableCount} foto(s) marcada(s) ({string.Join(", ", fields)}). Só os rascunhos mudam; grave com “Salvar”.";
        }
    }
    public bool CanApplyGlobal => !IsBusy && IncludedEditableCount > 0 && !Plan().IsNoOp && Plan().Validate().Count == 0;

    // ---------- foto em foco
    public MetadataRowViewModel? Focused { get => _focused; set { if (ReferenceEquals(_focused, value)) return; _focused = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasFocused)); OnPropertyChanged(nameof(FocusedGps)); OnPropertyChanged(nameof(FocusedMapUrl)); OnPropertyChanged(nameof(FocusedCamera)); OnPropertyChanged(nameof(FocusedExposure)); OnPropertyChanged(nameof(FocusedDate)); CopyFromFocusedCommand.RaiseCanExecuteChanged(); _ = LoadFocusedHistoryAsync(); } }
    public bool HasFocused => Focused is not null;
    public string FocusedCamera => string.Join("  ·  ", new[] { Focused?.Metadata?.Camera, Focused?.Metadata?.Lens }.Where(v => !string.IsNullOrWhiteSpace(v))) is { Length: > 0 } t ? t : "—";
    public string FocusedExposure => string.Join("  ·  ", new[] { Focused?.Metadata?.Aperture, Focused?.Metadata?.ExposureTime, Focused?.Metadata?.Iso is { } iso ? $"ISO {iso}" : null, Focused?.Metadata?.FocalLength }.Where(v => !string.IsNullOrWhiteSpace(v))) is { Length: > 0 } t ? t : "—";
    public string FocusedDate => Focused?.Metadata?.DateTaken?.ToString("g", CultureInfo.CurrentCulture) ?? "—";
    public string FocusedGps => Focused?.Metadata is { HasGps: true } m ? string.Create(CultureInfo.InvariantCulture, $"{m.Latitude:0.0000}° , {m.Longitude:0.0000}°") : "Sem localização";
    public string? FocusedMapUrl => Focused?.Metadata is { HasGps: true } m ? string.Create(CultureInfo.InvariantCulture, $"https://www.openstreetmap.org/?mlat={m.Latitude}&mlon={m.Longitude}#map=15/{m.Latitude}/{m.Longitude}") : null;

    // ---------- formulário de operações globais
    public BatchOperation TitleOperation { get => _titleOp; set { _titleOp = value; OnPlanChanged(); OnPropertyChanged(nameof(TitleEnabled)); } }
    public BatchOperation DescriptionOperation { get => _descriptionOp; set { _descriptionOp = value; OnPlanChanged(); OnPropertyChanged(nameof(DescriptionEnabled)); } }
    public BatchOperation KeywordsOperation { get => _keywordsOp; set { _keywordsOp = value; OnPlanChanged(); OnPropertyChanged(nameof(KeywordsEnabled)); } }
    public BatchOperation AuthorOperation { get => _authorOp; set { _authorOp = value; OnPlanChanged(); OnPropertyChanged(nameof(AuthorEnabled)); } }
    public BatchOperation CopyrightOperation { get => _copyrightOp; set { _copyrightOp = value; OnPlanChanged(); OnPropertyChanged(nameof(CopyrightEnabled)); } }
    public string GlobalTitle { get => _title; set { _title = value; OnPlanChanged(); } }
    public string GlobalDescription { get => _description; set { _description = value; OnPlanChanged(); } }
    public string GlobalAuthor { get => _author; set { _author = value; OnPlanChanged(); } }
    public string GlobalCopyright { get => _copyright; set { _copyright = value; OnPlanChanged(); } }
    public string NewKeyword { get => _newKeyword; set { _newKeyword = value; OnPropertyChanged(); } }
    public bool TitleEnabled => TitleOperation != BatchOperation.Keep;
    public bool DescriptionEnabled => DescriptionOperation != BatchOperation.Keep;
    public bool KeywordsEnabled => KeywordsOperation is not (BatchOperation.Keep or BatchOperation.Clear);
    public bool AuthorEnabled => AuthorOperation != BatchOperation.Keep;
    public bool CopyrightEnabled => CopyrightOperation != BatchOperation.Keep;
    public string PresetName { get => _presetName; set { _presetName = value; OnPropertyChanged(); RaiseCommands(); } }
    public MetadataPreset? SelectedPreset { get => _selectedPreset; set { _selectedPreset = value; OnPropertyChanged(); if (value is not null) PresetName = value.Name; RaiseCommands(); } }

    // ---------- estado
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(CanSaveAll)); OnPropertyChanged(nameof(CanApplyGlobal)); RaiseCommands(); } }
    public bool IsIdle => !IsBusy;
    public int Progress { get => _progress; private set { _progress = value; OnPropertyChanged(); } }
    public string StatusText { get => _statusText; private set { _statusText = value; OnPropertyChanged(); } }
    public bool StatusIsError { get => _statusIsError; private set { _statusIsError = value; OnPropertyChanged(); } }

    public BatchMetadataPlan Plan() => new()
    {
        TitleOperation = TitleOperation, Title = GlobalTitle,
        DescriptionOperation = DescriptionOperation, Description = GlobalDescription,
        KeywordsOperation = KeywordsOperation, Keywords = Keywords.ToList(),
        AuthorOperation = AuthorOperation, Author = GlobalAuthor,
        CopyrightOperation = CopyrightOperation, Copyright = GlobalCopyright
    };

    private static ObservableCollection<OperationChoice> Choices(string field) => new(BatchMetadataPlan.Allowed(field).Select(op => new OperationChoice(op, Labels[op])));

    // ---------- ciclo de vida / alvos
    public void SetActive(bool active)
    {
        _isActive = active;
        if (active) RefreshTargets();
    }

    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isActive && e.PropertyName is nameof(LibraryViewModel.SelectedPhoto)) RefreshTargets();
    }

    /// <summary>Fotos marcadas na Biblioteca; se só há uma foto selecionada, ela. Linhas com alterações pendentes nunca somem (ficam até salvar ou reverter).</summary>
    private IReadOnlyList<PhotoCardViewModel> Targets()
    {
        var selected = Library.SelectedCards;
        var primary = Library.SelectedPhoto;
        if (primary is not null && (selected.Count == 0 || !selected.Contains(primary))) return [primary];
        return selected;
    }

    public void RefreshTargets()
    {
        var targets = Targets();
        var keep = Rows.Where(r => r.IsChanged && !targets.Any(t => t.Photo.Id == r.Photo.Id)).ToList();
        var rows = new List<MetadataRowViewModel>();
        foreach (var card in targets) rows.Add(GetRow(card));
        rows.AddRange(keep);
        foreach (var row in Rows) row.Edited -= OnRowEdited;
        Rows.Clear();
        foreach (var row in rows) { row.Edited += OnRowEdited; Rows.Add(row); }
        Focused = Rows.FirstOrDefault(r => ReferenceEquals(r, Focused)) ?? Rows.FirstOrDefault();
        OnSummaryChanged();
    }

    private MetadataRowViewModel GetRow(PhotoCardViewModel card)
    {
        if (_cache.TryGetValue(card.Photo.Id, out var existing) && ReferenceEquals(existing.Card, card)) return existing;
        // Cartões são recriados a cada recarga do catálogo: preserva o rascunho se havia alterações, senão cria linha nova.
        if (existing is { IsChanged: true }) return existing;
        var row = new MetadataRowViewModel(card);
        _cache[card.Photo.Id] = row;
        return row;
    }

    /// <summary>Leitura preguiçosa: só quando a linha aparece (ou quando uma operação precisa dela).</summary>
    public async Task EnsureLoadedAsync(MetadataRowViewModel row)
    {
        if (row.IsLoaded || row.IsLoading || row.LoadError is not null || row.Card.IsMissing) return;
        row.IsLoading = true;
        await _loadGate.WaitAsync();
        try { row.SetBaseline(await _reader.ReadAsync(row.Photo.CurrentPath)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { row.LoadError = $"Não foi possível ler: {ex.Message}"; row.IsLoading = false; }
        finally { _loadGate.Release(); }
    }

    public Task EnsureLoadedAsync(IEnumerable<MetadataRowViewModel> rows) => Task.WhenAll(rows.Select(EnsureLoadedAsync).ToList());

    // ---------- operações globais (sobre rascunhos)
    public async Task ApplyGlobalAsync()
    {
        if (!CanApplyGlobal) return;
        var plan = Plan();
        var targets = Rows.Where(r => r.IsIncluded && r.IsEditable).ToList();
        IsBusy = true;
        try
        {
            await EnsureLoadedAsync(targets);
            var changed = 0;
            foreach (var row in targets.Where(r => r.IsLoaded))
            {
                var before = row.CurrentEdit();
                var after = plan.Apply(before);
                if (!after.SameAs(before)) { row.Apply(after); changed++; }
            }
            var skipped = targets.Count(r => !r.IsLoaded);
            SetStatus($"Operação aplicada a {targets.Count - skipped} foto(s): {changed} alterada(s). Revise as linhas e clique em Salvar.{(skipped > 0 ? $" {skipped} não puderam ser lidas." : string.Empty)}", false);
        }
        finally { IsBusy = false; }
    }

    /// <summary>Copia todos os campos preenchidos da foto em foco para as demais marcadas (substituindo).</summary>
    public async Task CopyFromFocusedAsync()
    {
        if (Focused is not { IsEditable: true } source) return;
        var edit = source.CurrentEdit();
        var plan = new BatchMetadataPlan
        {
            TitleOperation = edit.Title is null ? BatchOperation.Keep : BatchOperation.Replace, Title = edit.Title,
            DescriptionOperation = edit.Description is null ? BatchOperation.Keep : BatchOperation.Replace, Description = edit.Description,
            KeywordsOperation = edit.Keywords.Count == 0 ? BatchOperation.Keep : BatchOperation.Replace, Keywords = edit.Keywords,
            AuthorOperation = edit.Author is null ? BatchOperation.Keep : BatchOperation.Replace, Author = edit.Author,
            CopyrightOperation = edit.Copyright is null ? BatchOperation.Keep : BatchOperation.Replace, Copyright = edit.Copyright
        };
        if (plan.IsNoOp) { SetStatus("A foto em foco não tem metadados para copiar.", true); return; }
        var targets = Rows.Where(r => r.IsIncluded && r.IsEditable && !ReferenceEquals(r, source)).ToList();
        IsBusy = true;
        try
        {
            await EnsureLoadedAsync(targets);
            var changed = 0;
            foreach (var row in targets.Where(r => r.IsLoaded)) { var after = plan.Apply(row.CurrentEdit()); if (!after.SameAs(row.CurrentEdit())) { row.Apply(after); changed++; } }
            SetStatus($"Metadados de “{source.FileName}” copiados para {targets.Count} foto(s) marcada(s): {changed} alterada(s). Clique em Salvar para gravar.", false);
        }
        finally { IsBusy = false; }
    }

    private void DedupeKeywords()
    {
        var count = 0;
        foreach (var row in Rows.Where(r => r.IsIncluded && r.IsEditable && r.IsLoaded)) { var before = row.Keywords.Count; row.DedupeKeywords(); if (row.Keywords.Count != before) count++; }
        SetStatus(count == 0 ? "Nenhuma palavra repetida nas fotos marcadas." : $"Palavras repetidas removidas de {count} foto(s). Clique em Salvar para gravar.", false);
    }

    private void SetIncluded(bool value)
    {
        foreach (var row in Rows) row.IsIncluded = value;
        OnSummaryChanged();
    }

    private void ClearForm()
    {
        TitleOperation = DescriptionOperation = KeywordsOperation = AuthorOperation = CopyrightOperation = BatchOperation.Keep;
        GlobalTitle = GlobalDescription = GlobalAuthor = GlobalCopyright = NewKeyword = string.Empty;
        Keywords.Clear();
    }

    private void AddKeywords()
    {
        foreach (var keyword in NewKeyword.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase)) Keywords.Add(keyword);
        NewKeyword = string.Empty;
    }

    // ---------- salvar / reverter
    public Task SaveAllAsync() => SaveRowsAsync(Rows.Where(r => r.IsChanged).ToList());

    public async Task SaveRowsAsync(IReadOnlyList<MetadataRowViewModel> rows)
    {
        if (IsBusy || rows.Count == 0) return;
        IsBusy = true;
        Progress = 0;
        _cts = new CancellationTokenSource();
        int saved = 0, failed = 0;
        var finished = false; // callbacks atrasados não podem sobrescrever o resultado final
        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var row = rows[i];
                Progress = i * 100 / rows.Count;
                SetStatus($"Gravando {i + 1} de {rows.Count}: {row.FileName}", false);
                try
                {
                    var result = await _editor.SaveAsync(row.Photo, row.CurrentEdit(), _cts.Token);
                    var expected = row.CurrentEdit();
                    row.SetBaseline(await _reader.ReadAsync(row.Photo.CurrentPath));   // relê do arquivo: mostra o que realmente ficou
                    row.MarkSaved(result.Changed ? $"Salva — v{result.Version}" : "Sem alterações no arquivo");
                    if (!row.CurrentEdit().SameAs(expected)) row.MarkSaved($"Salva — v{result.Version} (algum campo continua vindo do EXIF)");
                    saved++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    row.MarkFailed($"Não salva: {ex.Message} O arquivo original não foi alterado.");
                    failed++;
                }
            }
            finished = true;
            Progress = 100;
            SetStatus($"{saved} foto(s) salva(s){(failed > 0 ? $", {failed} com erro (continuam como rascunho)" : string.Empty)}.", failed > 0);
        }
        catch (OperationCanceledException) { finished = true; SetStatus($"Cancelado. {saved} foto(s) já gravadas permanecem alteradas.", false); }
        finally { _ = finished; IsBusy = false; _cts?.Dispose(); _cts = null; OnSummaryChanged(); _ = LoadFocusedHistoryAsync(); }
    }

    private void RevertAll()
    {
        var changed = Rows.Where(r => r.IsChanged).ToList();
        if (changed.Count == 0 || (ConfirmRevert?.Invoke(changed.Count) ?? true) == false) return;
        foreach (var row in changed) row.Revert();
        SetStatus($"{changed.Count} rascunho(s) descartado(s); os arquivos não foram alterados.", false);
    }

    // ---------- presets
    private async Task LoadPresetsAsync(long? select = null)
    {
        if (_presets is null) return;
        try
        {
            var all = await _presets.GetAllAsync();
            Presets.Clear();
            foreach (var preset in all) Presets.Add(preset);
            if (select is not null) SelectedPreset = Presets.FirstOrDefault(p => p.Id == select);
        }
        catch (Exception ex) { SetStatus($"Não foi possível carregar os presets: {ex.Message}", true); }
    }

    private void LoadPreset()
    {
        if (SelectedPreset is not { } preset) return;
        var plan = preset.Plan;
        TitleOperation = plan.TitleOperation; GlobalTitle = plan.Title ?? string.Empty;
        DescriptionOperation = plan.DescriptionOperation; GlobalDescription = plan.Description ?? string.Empty;
        KeywordsOperation = plan.KeywordsOperation;
        Keywords.Clear();
        foreach (var keyword in plan.Keywords) Keywords.Add(keyword);
        AuthorOperation = plan.AuthorOperation; GlobalAuthor = plan.Author ?? string.Empty;
        CopyrightOperation = plan.CopyrightOperation; GlobalCopyright = plan.Copyright ?? string.Empty;
        SetStatus($"Preset “{preset.Name}” carregado. Clique em “Aplicar às marcadas”.", false);
    }

    private async Task SavePresetAsync()
    {
        if (_presets is null) return;
        try { var saved = await _presets.SaveAsync(PresetName, Plan()); await LoadPresetsAsync(saved.Id); SetStatus($"Preset “{saved.Name}” salvo.", false); }
        catch (Exception ex) { SetStatus($"Não foi possível salvar o preset: {ex.Message}", true); }
    }

    private async Task DeletePresetAsync()
    {
        if (_presets is null || SelectedPreset is not { } preset) return;
        try { await _presets.DeleteAsync(preset.Id); await LoadPresetsAsync(); SelectedPreset = null; PresetName = string.Empty; SetStatus($"Preset “{preset.Name}” excluído.", false); }
        catch (Exception ex) { SetStatus($"Não foi possível excluir o preset: {ex.Message}", true); }
    }

    // ---------- histórico da foto em foco
    private async Task LoadFocusedHistoryAsync()
    {
        var version = ++_focusVersion;
        var row = Focused;
        FocusedHistory.Clear();
        if (row is null) return;
        try
        {
            var history = await _editor.GetHistoryAsync(row.Photo.Id);
            if (version != _focusVersion) return;
            foreach (var entry in history) FocusedHistory.Add(new EditorLine($"v{entry.Version} · {entry.ChangedAt.ToLocalTime():g}", entry.Note ?? string.Empty));
        }
        catch (Exception) { /* histórico é informativo */ }
    }

    // ---------- notificações
    private void OnRowEdited() => OnSummaryChanged();

    private void OnSummaryChanged()
    {
        foreach (var name in new[] { nameof(RowCount), nameof(IncludedCount), nameof(IncludedEditableCount), nameof(ChangedCount), nameof(ReadOnlyCount), nameof(HasRows), nameof(SummaryText), nameof(SaveButtonText),
                     nameof(CanSaveAll), nameof(CanApplyGlobal), nameof(GlobalImpactText) })
            OnPropertyChanged(name);
        RaiseCommands();
    }

    private void OnPlanChanged()
    {
        foreach (var name in new[] { nameof(TitleOperation), nameof(DescriptionOperation), nameof(KeywordsOperation), nameof(AuthorOperation), nameof(CopyrightOperation), nameof(GlobalTitle), nameof(GlobalDescription),
                     nameof(GlobalAuthor), nameof(GlobalCopyright), nameof(CanApplyGlobal), nameof(GlobalImpactText) })
            OnPropertyChanged(name);
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        ApplyGlobalCommand.RaiseCanExecuteChanged(); CopyFromFocusedCommand.RaiseCanExecuteChanged(); DedupeKeywordsCommand.RaiseCanExecuteChanged(); SaveAllCommand.RaiseCanExecuteChanged();
        SaveRowCommand.RaiseCanExecuteChanged(); RevertAllCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); LoadPresetCommand.RaiseCanExecuteChanged();
        SavePresetCommand.RaiseCanExecuteChanged(); DeletePresetCommand.RaiseCanExecuteChanged();
    }

    private void SetStatus(string text, bool isError) { StatusText = text; StatusIsError = isError; }
}
