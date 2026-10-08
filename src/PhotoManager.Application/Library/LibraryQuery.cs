using System.Globalization;
using System.Text;
using PhotoManager.Application.Catalog;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Library;

/// <summary>Informações que não estão na <see cref="Photo"/> (duplicatas, marcadores, IA). Sem contexto, esses termos não casam com nada.</summary>
public interface ILibraryQueryContext
{
    bool IsDuplicate(long photoId) => false;
    bool HasMarkers(long photoId) => false;
    bool TranscriptContains(long photoId, string text) => false;
    bool HasPerson(long photoId, string name) => false;
    bool HasAiTag(long photoId, string tag) => false;
    /// <summary>Ids que a busca por significado (IA) devolveu para o texto; nulo = IA indisponível.</summary>
    IReadOnlySet<long>? SemanticMatches(string text) => null;
}

public sealed class EmptyQueryContext : ILibraryQueryContext { public static EmptyQueryContext Instance { get; } = new(); }

/// <summary>Um termo "chave:valor" da busca (ou texto livre, com <see cref="Key"/> vazia).</summary>
public sealed record QueryTerm(string Key, string Operator, string Value, bool Negated, string Source)
{
    public string Label => Key.Length == 0 ? $"“{Value}”" : $"{(Negated ? "não " : string.Empty)}{LibraryQuery.KeyLabel(Key)} {(Operator is "=" ? string.Empty : Operator + " ")}{Value}".Trim();
}

/// <summary>
/// Busca com sintaxe, como num app profissional: <c>tipo:video duração:&lt;60 orientação:vertical cor:verde nota:&gt;=3</c>.
/// Termos combinam com E; "-" na frente nega (<c>-uso:publicado</c>); aspas para valores com espaço (<c>tag:"pôr do sol"</c>).
/// Texto livre procura no nome, nota, local, categoria e tags.
/// </summary>
public sealed class LibraryQuery
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tipo"] = "tipo", ["type"] = "tipo",
        ["duracao"] = "duracao", ["duração"] = "duracao", ["dur"] = "duracao",
        ["orientacao"] = "orientacao", ["orientação"] = "orientacao", ["formato"] = "orientacao",
        ["cor"] = "cor", ["color"] = "cor",
        ["nota"] = "nota", ["estrelas"] = "nota", ["avaliacao"] = "nota", ["avaliação"] = "nota",
        ["favorita"] = "favorita", ["fav"] = "favorita",
        ["bandeira"] = "bandeira", ["triagem"] = "bandeira",
        ["uso"] = "uso", ["usado"] = "uso",
        ["shorts"] = "shorts", ["reels"] = "shorts", ["curto"] = "shorts",
        ["tag"] = "tag", ["categoria"] = "categoria", ["colecao"] = "colecao", ["coleção"] = "colecao",
        ["pasta"] = "pasta", ["ext"] = "ext", ["extensao"] = "ext", ["extensão"] = "ext",
        ["local"] = "local", ["lugar"] = "local", ["ano"] = "ano", ["mes"] = "mes", ["mês"] = "mes", ["data"] = "data",
        ["gps"] = "gps", ["ausente"] = "ausente", ["duplicada"] = "duplicada", ["duplicado"] = "duplicada",
        ["marcadores"] = "marcadores", ["cortes"] = "marcadores", ["desfocada"] = "desfocada", ["nitidez"] = "nitidez",
        ["fala"] = "fala", ["ia"] = "ia", ["pessoa"] = "pessoa", ["etiqueta"] = "etiqueta", ["nome"] = "nome", ["tamanho"] = "tamanho"
    };

    /// <summary>Chaves conhecidas (forma principal) com descrição, para sugestões e ajuda.</summary>
    public static IReadOnlyList<(string Key, string Help)> Keys { get; } =
    [
        ("tipo", "foto, video"), ("duração", "<60, >=10, 1m30"), ("orientação", "vertical, horizontal, quadrada"), ("cor", "vermelho…roxo, sem"),
        ("nota", ">=3, 5"), ("favorita", "sim, nao"), ("bandeira", "escolhida, rejeitada, sem"), ("uso", "usado, publicado, sem"),
        ("shorts", "sim, nao (vertical 9:16 até 3 min)"), ("tag", "nome da tag"), ("categoria", "nome"), ("coleção", "nome"), ("pasta", "parte do caminho"),
        ("ext", "jpg, mp4…"), ("local", "nome do lugar"), ("ano", "2026"), ("mês", "2026-09"), ("data", ">2026-09-01"), ("gps", "sim, nao"),
        ("ausente", "sim"), ("duplicada", "sim"), ("marcadores", "sim"), ("desfocada", "sim"), ("tamanho", ">500MB"),
        ("fala", "texto dito no vídeo (IA)"), ("ia", "descrição da cena (IA)"), ("pessoa", "nome (IA)"), ("etiqueta", "etiqueta automática (IA)")
    ];

    public static IReadOnlyDictionary<string, string[]> Values { get; } = new Dictionary<string, string[]>
    {
        ["tipo"] = ["foto", "video"], ["orientacao"] = ["vertical", "horizontal", "quadrada"],
        ["cor"] = ["vermelho", "laranja", "amarelo", "verde", "azul", "roxo", "sem"], ["favorita"] = ["sim", "nao"],
        ["bandeira"] = ["escolhida", "rejeitada", "sem"], ["uso"] = ["usado", "publicado", "sem"], ["shorts"] = ["sim", "nao"],
        ["gps"] = ["sim", "nao"], ["ausente"] = ["sim", "nao"], ["duplicada"] = ["sim", "nao"], ["marcadores"] = ["sim", "nao"], ["desfocada"] = ["sim", "nao"],
        ["nota"] = [">=1", ">=3", ">=4", "5"], ["duracao"] = ["<60", "<180", ">=600"]
    };

    /// <summary>Nitidez abaixo disso = provável foto tremida/desfocada (escala de <see cref="ImageAnalysis.Sharpness"/>).</summary>
    public const double BlurThreshold = 60;

    private LibraryQuery(IReadOnlyList<QueryTerm> terms, IReadOnlyList<string> errors) { Terms = terms; Errors = errors; }

    public IReadOnlyList<QueryTerm> Terms { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool IsEmpty => Terms.Count == 0;
    public bool UsesAi => Terms.Any(t => t.Key is "ia" or "fala" or "pessoa" or "etiqueta");

    public static string KeyLabel(string key) => Keys.FirstOrDefault(k => Normalize(k.Key) == key).Key ?? key;

    public static LibraryQuery Parse(string? text)
    {
        var terms = new List<QueryTerm>();
        var errors = new List<string>();
        foreach (var raw in Tokenize(text ?? string.Empty))
        {
            var token = raw;
            var negated = token.Length > 1 && token[0] == '-';
            if (negated) token = token[1..];
            var colon = token.IndexOf(':');
            if (colon <= 0 || colon == token.Length - 1 || !Aliases.TryGetValue(token[..colon], out var key))
            {
                terms.Add(new QueryTerm(string.Empty, "=", Unquote(token), negated, raw));
                continue;
            }
            var value = Unquote(token[(colon + 1)..]);
            var op = "=";
            foreach (var candidate in new[] { ">=", "<=", ">", "<", "=" })
                if (value.StartsWith(candidate, StringComparison.Ordinal)) { op = candidate; value = value[candidate.Length..]; break; }
            if (value.Length == 0) { errors.Add($"Falta o valor de “{token[..colon]}”."); continue; }
            var term = new QueryTerm(key, op, value, negated, raw);
            if (Validate(term) is { } error) errors.Add(error); else terms.Add(term);
        }
        return new LibraryQuery(terms, errors);
    }

    /// <summary>O texto da busca sem um dos termos (o "x" de uma ficha de filtro).</summary>
    public static string Without(string text, QueryTerm term)
    {
        var tokens = Tokenize(text).ToList();
        var index = tokens.FindIndex(t => t == term.Source);
        if (index >= 0) tokens.RemoveAt(index);
        return string.Join(" ", tokens);
    }

    public bool Matches(Photo photo, ILibraryQueryContext? context = null)
    {
        context ??= EmptyQueryContext.Instance;
        foreach (var term in Terms)
            if (MatchesTerm(photo, term, context) == term.Negated) return false;
        return true;
    }

    /// <summary>Sugestões para completar o último pedaço digitado (chave ou valor).</summary>
    public static IReadOnlyList<string> Suggest(string text, Func<string, IEnumerable<string>>? dynamicValues = null)
    {
        var tokens = Tokenize(text).ToList();
        var endsWithSpace = text.Length > 0 && char.IsWhiteSpace(text[^1]);
        var last = endsWithSpace || tokens.Count == 0 ? string.Empty : tokens[^1];
        var prefix = endsWithSpace || tokens.Count == 0 ? text : text[..^last.Length];
        var negation = last.StartsWith('-') ? "-" : string.Empty;
        var body = last.TrimStart('-');
        var colon = body.IndexOf(':');
        IEnumerable<string> options;
        if (colon < 0)
            options = Keys.Select(k => k.Key + ":").Where(k => body.Length == 0 || k.StartsWith(body, StringComparison.CurrentCultureIgnoreCase) || Normalize(k).StartsWith(Normalize(body), StringComparison.Ordinal));
        else
        {
            var keyText = body[..colon];
            if (!Aliases.TryGetValue(keyText, out var key)) return [];
            var typed = Unquote(body[(colon + 1)..]);
            var candidates = Values.TryGetValue(key, out var fixedValues) ? fixedValues : dynamicValues?.Invoke(key) ?? [];
            options = candidates.Where(v => Normalize(v).StartsWith(Normalize(typed), StringComparison.Ordinal))
                .Select(v => $"{keyText}:{(v.Contains(' ') ? $"\"{v}\"" : v)} ");
        }
        return options.Take(12).Select(o => prefix + negation + o).ToList();
    }

    private static bool MatchesTerm(Photo p, QueryTerm t, ILibraryQueryContext ctx)
    {
        var v = Normalize(t.Value);
        switch (t.Key)
        {
            case "":
                return Contains(p.FileName, t.Value) || Contains(p.PersonalNote, t.Value) || Contains(p.PlaceName, t.Value) || Contains(p.CategoryName, t.Value)
                    || p.Tags.Any(tag => Contains(tag, t.Value));
            case "tipo": return v.StartsWith("vid", StringComparison.Ordinal) ? p.IsVideo : !p.IsVideo;
            case "duracao": return p.IsVideo && p.DurationSeconds is { } seconds && Compare(seconds, t.Operator, ParseDuration(t.Value)!.Value);
            case "orientacao":
                return v switch
                {
                    "vertical" or "retrato" => p.Orientation == PhotoOrientation.Portrait,
                    "horizontal" or "paisagem" => p.Orientation == PhotoOrientation.Landscape,
                    _ => p.Orientation == PhotoOrientation.Square
                };
            case "cor": return p.ColorLabel == ParseColor(v);
            case "nota": return Compare(p.Rating, t.Operator, int.Parse(t.Value, CultureInfo.InvariantCulture));
            case "favorita": return p.IsFavorite == IsYes(v);
            case "bandeira": return p.Pick == (v.StartsWith("esc", StringComparison.Ordinal) || v == "pick" ? PickFlag.Picked : v.StartsWith("rej", StringComparison.Ordinal) ? PickFlag.Rejected : PickFlag.None);
            case "uso": return p.Usage == (v.StartsWith("pub", StringComparison.Ordinal) ? UsageStatus.Published : v.StartsWith("usa", StringComparison.Ordinal) ? UsageStatus.Used : UsageStatus.None)
                               || (v.StartsWith("usa", StringComparison.Ordinal) && p.Usage == UsageStatus.Published);   // publicado também foi usado
            case "shorts": return ShortFormRules.IsShortForm(p) == IsYes(v);
            case "tag": return p.Tags.Any(tag => Normalize(tag) == v);
            case "categoria": return Normalize(p.CategoryName ?? string.Empty) == v;
            case "colecao": return p.Collections.Any(c => Normalize(c) == v);
            case "pasta": return Contains(Path.GetDirectoryName(p.CurrentPath), t.Value);
            case "ext": return Normalize(p.Extension.TrimStart('.')) == v.TrimStart('.');
            case "local": return Contains(p.PlaceName, t.Value);
            case "nome": return Contains(p.FileName, t.Value);
            case "ano": return Compare(p.DisplayDate.Year, t.Operator, int.Parse(t.Value, CultureInfo.InvariantCulture));
            case "mes":
                return t.Value.Contains('-')
                    ? p.DisplayDate.ToString("yyyy-MM", CultureInfo.InvariantCulture) == t.Value
                    : p.DisplayDate.Month == int.Parse(t.Value, CultureInfo.InvariantCulture);
            case "data": return Compare(p.DisplayDate.Date.Ticks, t.Operator, ParseDate(t.Value)!.Value.Ticks);
            case "gps": return p.HasGps == IsYes(v);
            case "ausente": return p.IsMissing == IsYes(v);
            case "duplicada": return ctx.IsDuplicate(p.Id) == IsYes(v);
            case "marcadores": return ctx.HasMarkers(p.Id) == IsYes(v);
            case "desfocada": return p.Sharpness is { } s && (s < BlurThreshold) == IsYes(v);
            case "nitidez": return p.Sharpness is { } sharp && Compare(sharp, t.Operator, double.Parse(t.Value, CultureInfo.InvariantCulture));
            case "tamanho": return Compare(p.FileSize, t.Operator, ParseSize(t.Value)!.Value);
            case "fala": return ctx.TranscriptContains(p.Id, t.Value);
            case "pessoa": return ctx.HasPerson(p.Id, t.Value);
            case "etiqueta": return ctx.HasAiTag(p.Id, t.Value);
            case "ia": return ctx.SemanticMatches(t.Value)?.Contains(p.Id) ?? false;
            default: return true;
        }
    }

    private static string? Validate(QueryTerm t)
    {
        var v = Normalize(t.Value);
        return t.Key switch
        {
            "duracao" when ParseDuration(t.Value) is null => $"Duração inválida: “{t.Value}” (use 60, 90s, 1m30, 2min).",
            "nota" when !int.TryParse(t.Value, out var n) || n is < 0 or > 5 => "A nota vai de 0 a 5.",
            "ano" when !int.TryParse(t.Value, out _) => $"Ano inválido: “{t.Value}”.",
            "mes" when !(int.TryParse(t.Value, out var m) && m is >= 1 and <= 12) && !DateTime.TryParseExact(t.Value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => $"Mês inválido: “{t.Value}” (use 9 ou 2026-09).",
            "data" when ParseDate(t.Value) is null => $"Data inválida: “{t.Value}” (use 2026-09-27 ou 27/09/2026).",
            "cor" when ParseColor(v) is null => $"Cor desconhecida: “{t.Value}”.",
            "nitidez" when !double.TryParse(t.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => "Nitidez deve ser um número.",
            "tamanho" when ParseSize(t.Value) is null => $"Tamanho inválido: “{t.Value}” (use 500MB, 2GB).",
            "orientacao" when v is not ("vertical" or "retrato" or "horizontal" or "paisagem" or "quadrada") => $"Orientação desconhecida: “{t.Value}”.",
            _ => null
        };
    }

    /// <summary>"60", "90s", "1m30", "2min", "1:30" → segundos.</summary>
    public static double? ParseDuration(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (double.TryParse(text.TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return seconds;
        if (TimeSpan.TryParseExact(text, [@"m\:ss", @"h\:mm\:ss"], CultureInfo.InvariantCulture, out var span)) return span.TotalSeconds;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^(?:(\d+)h)?(?:(\d+)(?:m|min))?(?:(\d+)s?)?$");
        if (!match.Success || match.Value.Length == 0) return null;
        double Part(int i) => match.Groups[i].Success ? double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        return Part(1) * 3600 + Part(2) * 60 + Part(3);
    }

    public static long? ParseSize(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text.Trim().ToUpperInvariant(), @"^(\d+(?:[.,]\d+)?)\s*(B|KB|MB|GB|TB)?$");
        if (!match.Success) return null;
        var number = double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var unit = match.Groups[2].Value switch { "KB" => 1L << 10, "MB" => 1L << 20, "GB" => 1L << 30, "TB" => 1L << 40, _ => 1L };
        return (long)(number * unit);
    }

    private static DateTime? ParseDate(string text) =>
        DateTime.TryParseExact(text, ["yyyy-MM-dd", "dd/MM/yyyy", "yyyy-MM", "yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static PhotoColor? ParseColor(string normalized) => normalized switch
    {
        "vermelho" or "vermelha" => PhotoColor.Red, "laranja" => PhotoColor.Orange, "amarelo" or "amarela" => PhotoColor.Yellow,
        "verde" => PhotoColor.Green, "azul" => PhotoColor.Blue, "roxo" or "roxa" => PhotoColor.Purple, "sem" or "nenhuma" => PhotoColor.None,
        _ => null
    };

    private static bool IsYes(string normalized) => normalized is "sim" or "s" or "yes" or "true" or "1";

    private static bool Compare(double actual, string op, double expected) => op switch
    {
        "<" => actual < expected, "<=" => actual <= expected, ">" => actual > expected, ">=" => actual >= expected,
        _ => Math.Abs(actual - expected) < 0.0001
    };

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && Normalize(haystack).Contains(Normalize(needle), StringComparison.Ordinal);

    /// <summary>Minúsculas e sem acento: "Duração" = "duracao".</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string Unquote(string text) => text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text.Trim('"');

    /// <summary>Separa por espaço, respeitando aspas: tag:"pôr do sol" é um token só.</summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
