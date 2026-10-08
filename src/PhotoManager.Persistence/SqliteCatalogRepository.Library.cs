using System.Globalization;
using Microsoft.Data.Sqlite;
using PhotoManager.Application.Ai;
using PhotoManager.Application.Library;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Persistence;

/// <summary>Triagem, uso, análise de imagem, marcadores de clipe, coleções inteligentes, backups e índice da IA local.</summary>
public sealed partial class SqliteCatalogRepository
{
    // ---------- triagem, uso e análise ----------

    public Task UpdatePickAsync(IReadOnlyCollection<long> photoIds, PickFlag flag, CancellationToken cancellationToken = default) =>
        UpdateManyAsync(photoIds, "PickFlag = $value", ("$value", (int)flag), cancellationToken);

    public Task UpdateUsageAsync(IReadOnlyCollection<long> photoIds, UsageStatus status, string? note, CancellationToken cancellationToken = default) =>
        UpdateManyAsync(photoIds, "UsageStatus = $value, UsageNote = $note", ("$value", (int)status), cancellationToken, ("$note", (object?)note ?? DBNull.Value));

    public async Task UpdateAnalysisAsync(long photoId, long? perceptualHash, double? sharpness, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Photos SET PerceptualHash = $hash, Sharpness = $sharp WHERE Id = $id";
        command.Parameters.AddWithValue("$hash", (object?)perceptualHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$sharp", (object?)sharpness ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", photoId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateManyAsync(IReadOnlyCollection<long> ids, string set, (string Name, object Value) value, CancellationToken cancellationToken, params (string Name, object Value)[] extra)
    {
        if (ids.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var chunk in ids.Distinct().Chunk(400))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE Photos SET {set} WHERE Id IN ({string.Join(",", chunk.Select((_, i) => $"$p{i}"))})";
            command.Parameters.AddWithValue(value.Name, value.Value);
            foreach (var (name, v) in extra) command.Parameters.AddWithValue(name, v);
            for (var i = 0; i < chunk.Length; i++) command.Parameters.AddWithValue($"$p{i}", chunk[i]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------- marcadores de clipe ----------

    public async Task<IReadOnlyList<ClipMarker>> GetMarkersAsync(long? photoId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, PhotoId, InSeconds, OutSeconds, Name, Rating FROM ClipMarkers" + (photoId is null ? string.Empty : " WHERE PhotoId = $photo") + " ORDER BY PhotoId, InSeconds";
        if (photoId is { } id) command.Parameters.AddWithValue("$photo", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ClipMarker>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetString(4), reader.GetInt32(5)));
        return result;
    }

    public async Task<ClipMarker> AddMarkerAsync(long photoId, double inSeconds, double outSeconds, string name, int rating, CancellationToken cancellationToken = default)
    {
        if (outSeconds < inSeconds) (inSeconds, outSeconds) = (outSeconds, inSeconds);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ClipMarkers (PhotoId, InSeconds, OutSeconds, Name, Rating) VALUES ($photo, $in, $out, $name, $rating) RETURNING Id";
        command.Parameters.AddWithValue("$photo", photoId);
        command.Parameters.AddWithValue("$in", inSeconds);
        command.Parameters.AddWithValue("$out", outSeconds);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$rating", rating);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return new ClipMarker(id, photoId, inSeconds, outSeconds, name, rating);
    }

    public async Task UpdateMarkerAsync(ClipMarker marker, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ClipMarkers SET InSeconds = $in, OutSeconds = $out, Name = $name, Rating = $rating WHERE Id = $id";
        command.Parameters.AddWithValue("$in", Math.Min(marker.InSeconds, marker.OutSeconds));
        command.Parameters.AddWithValue("$out", Math.Max(marker.InSeconds, marker.OutSeconds));
        command.Parameters.AddWithValue("$name", marker.Name);
        command.Parameters.AddWithValue("$rating", marker.Rating);
        command.Parameters.AddWithValue("$id", marker.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteMarkerAsync(long markerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ClipMarkers WHERE Id = $id";
        command.Parameters.AddWithValue("$id", markerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---------- coleções inteligentes ----------

    public async Task<IReadOnlyList<SmartCollection>> GetSmartCollectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Query FROM SmartCollections ORDER BY Name COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SmartCollection>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    public async Task<SmartCollection> SaveSmartCollectionAsync(long? id, string name, string query, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = id is null
            ? "INSERT INTO SmartCollections (Name, Query) VALUES ($name, $query) RETURNING Id"
            : "UPDATE SmartCollections SET Name = $name, Query = $query WHERE Id = $id RETURNING Id";
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$query", query.Trim());
        if (id is { } existing) command.Parameters.AddWithValue("$id", existing);
        var saved = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return new SmartCollection(saved, name.Trim(), query.Trim());
    }

    public async Task DeleteSmartCollectionAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SmartCollections WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---------- backups ----------

    public async Task<IReadOnlyList<BackupTarget>> GetBackupTargetsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, SourceFolder, TargetFolder, LastVerifiedAt, MissingCount, CheckedCount, DifferentCount FROM BackupTargets ORDER BY SourceFolder COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<BackupTarget>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6)));
        return result;
    }

    public async Task<BackupTarget> SaveBackupTargetAsync(long? id, string sourceFolder, string targetFolder, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = id is null
            ? "INSERT INTO BackupTargets (SourceFolder, TargetFolder) VALUES ($source, $target) RETURNING Id"
            : "UPDATE BackupTargets SET SourceFolder = $source, TargetFolder = $target, LastVerifiedAt = NULL, MissingCount = NULL, CheckedCount = NULL, DifferentCount = NULL WHERE Id = $id RETURNING Id";
        command.Parameters.AddWithValue("$source", sourceFolder);
        command.Parameters.AddWithValue("$target", targetFolder);
        if (id is { } existing) command.Parameters.AddWithValue("$id", existing);
        var saved = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return new BackupTarget(saved, sourceFolder, targetFolder, null, null, null, null);
    }

    public async Task DeleteBackupTargetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM BackupTargets WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateBackupStatusAsync(long id, DateTime verifiedUtc, int checkedCount, int missingCount, int differentCount, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE BackupTargets SET LastVerifiedAt = $at, CheckedCount = $checked, MissingCount = $missing, DifferentCount = $different WHERE Id = $id";
        command.Parameters.AddWithValue("$at", verifiedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$checked", checkedCount);
        command.Parameters.AddWithValue("$missing", missingCount);
        command.Parameters.AddWithValue("$different", differentCount);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---------- IA local ----------

    async Task IAiRepository.SaveEmbeddingAsync(long photoId, string model, float[] vector, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO AiEmbeddings (PhotoId, Model, Vector) VALUES ($photo, $model, $vector)";
        command.Parameters.AddWithValue("$photo", photoId);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$vector", VectorMath.ToBytes(vector));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<IReadOnlyDictionary<long, float[]>> IAiRepository.GetEmbeddingsAsync(string model, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PhotoId, Vector FROM AiEmbeddings WHERE Model = $model";
        command.Parameters.AddWithValue("$model", model);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<long, float[]>();
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetInt64(0)] = VectorMath.FromBytes((byte[])reader.GetValue(1));
        return result;
    }

    async Task IAiRepository.SaveFacesAsync(long photoId, IReadOnlyList<DetectedFace> faces, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM AiFaces WHERE PhotoId = $photo", cancellationToken, ("$photo", photoId));
        foreach (var face in faces)
            await ExecuteAsync(connection, transaction, "INSERT INTO AiFaces (PhotoId, X, Y, W, H, Embedding) VALUES ($photo, $x, $y, $w, $h, $e)", cancellationToken,
                ("$photo", photoId), ("$x", face.X), ("$y", face.Y), ("$w", face.Width), ("$h", face.Height), ("$e", VectorMath.ToBytes(face.Embedding)));
        await transaction.CommitAsync(cancellationToken);
    }

    async Task<IReadOnlyList<AiFace>> IAiRepository.GetFacesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, PhotoId, PersonId, X, Y, W, H, Embedding FROM AiFaces ORDER BY Id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AiFace>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetFloat(3), reader.GetFloat(4), reader.GetFloat(5), reader.GetFloat(6), VectorMath.FromBytes((byte[])reader.GetValue(7))));
        return result;
    }

    async Task<long> IAiRepository.CreatePersonAsync(string? name, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO AiPeople (Name) VALUES ($name) RETURNING Id";
        command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    async Task IAiRepository.AssignFacesAsync(IReadOnlyCollection<long> faceIds, long? personId, CancellationToken cancellationToken)
    {
        if (faceIds.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in faceIds)
            await ExecuteAsync(connection, transaction, "UPDATE AiFaces SET PersonId = $person WHERE Id = $id", cancellationToken, ("$person", (object?)personId ?? DBNull.Value), ("$id", id));
        await ExecuteAsync(connection, transaction, "DELETE FROM AiPeople WHERE Name IS NULL AND NOT EXISTS (SELECT 1 FROM AiFaces f WHERE f.PersonId = AiPeople.Id)", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    async Task IAiRepository.RenamePersonAsync(long personId, string? name, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AiPeople SET Name = $name WHERE Id = $id";
        command.Parameters.AddWithValue("$name", string.IsNullOrWhiteSpace(name) ? DBNull.Value : name.Trim());
        command.Parameters.AddWithValue("$id", personId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<IReadOnlyList<AiPerson>> IAiRepository.GetPeopleAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.Id, p.Name, COUNT(f.Id), MIN(f.PhotoId) FROM AiPeople p JOIN AiFaces f ON f.PersonId = p.Id
            GROUP BY p.Id, p.Name ORDER BY p.Name IS NULL, COUNT(f.Id) DESC
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<AiPerson>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3)));
        return result;
    }

    async Task IAiRepository.SaveTagsAsync(long photoId, IReadOnlyList<(string Tag, float Score)> tags, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM AiTags WHERE PhotoId = $photo", cancellationToken, ("$photo", photoId));
        foreach (var (tag, score) in tags)
            await ExecuteAsync(connection, transaction, "INSERT OR REPLACE INTO AiTags (PhotoId, Tag, Score) VALUES ($photo, $tag, $score)", cancellationToken, ("$photo", photoId), ("$tag", tag), ("$score", score));
        await transaction.CommitAsync(cancellationToken);
    }

    async Task<IReadOnlyDictionary<long, IReadOnlyList<string>>> IAiRepository.GetTagsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PhotoId, Tag FROM AiTags ORDER BY PhotoId, Score DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<long, List<string>>();
        while (await reader.ReadAsync(cancellationToken))
            (result.TryGetValue(reader.GetInt64(0), out var list) ? list : result[reader.GetInt64(0)] = []).Add(reader.GetString(1));
        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value);
    }

    async Task IAiRepository.SaveTranscriptAsync(long photoId, Transcript transcript, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO AiTranscripts (PhotoId, Language, Text) VALUES ($photo, $lang, $text)";
        command.Parameters.AddWithValue("$photo", photoId);
        command.Parameters.AddWithValue("$lang", transcript.Language);
        command.Parameters.AddWithValue("$text", transcript.Text);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<IReadOnlyDictionary<long, string>> IAiRepository.GetTranscriptsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PhotoId, Text FROM AiTranscripts";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<long, string>();
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetInt64(0)] = reader.GetString(1);
        return result;
    }

    async Task IAiRepository.MarkIndexedAsync(long photoId, AiCapability kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO AiIndexState (PhotoId, Kind, IndexedAt) VALUES ($photo, $kind, $at)";
        command.Parameters.AddWithValue("$photo", photoId);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<IReadOnlySet<long>> IAiRepository.GetIndexedAsync(AiCapability kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PhotoId FROM AiIndexState WHERE Kind = $kind";
        command.Parameters.AddWithValue("$kind", kind.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<long>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetInt64(0));
        return result;
    }
}
