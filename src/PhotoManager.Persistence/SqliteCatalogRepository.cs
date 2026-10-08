using Microsoft.Data.Sqlite;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Collections;
using PhotoManager.Application.Metadata;
using PhotoManager.Application.Microstock;
using PhotoManager.Domain.Collections;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Persistence;

public sealed partial class SqliteCatalogRepository(string databasePath) : ICatalogRepository, IOrganizationRepository, IMetadataVersionRepository, IMetadataPresetRepository, IValidationProfileRepository, IUploadHistoryRepository, IDuplicateRepository, ICollectionRepository, PhotoManager.Application.Transfer.ITransferRepository, PhotoManager.Application.Library.ILibraryRepository, PhotoManager.Application.Ai.IAiRepository
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        await using (var connection = await OpenAsync(cancellationToken))
        {
            // Migração de schema legado para hierarquia de coleções (D3)
            var needsMigration = false;
            await using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Collections'";
                var tableName = await checkCmd.ExecuteScalarAsync(cancellationToken);
                if (tableName is not null)
                {
                    checkCmd.CommandText = "PRAGMA table_info(Collections)";
                    await using var infoReader = await checkCmd.ExecuteReaderAsync(cancellationToken);
                    var hasParentCol = false;
                    while (await infoReader.ReadAsync(cancellationToken))
                    {
                        if (string.Equals(infoReader.GetString(1), "ParentCollectionId", StringComparison.OrdinalIgnoreCase))
                        {
                            hasParentCol = true;
                            break;
                        }
                    }
                    if (!hasParentCol) needsMigration = true;
                }
            }

            if (needsMigration)
            {
                var dbDir = Path.GetDirectoryName(databasePath) ?? "";
                var backupPath = Path.Combine(dbDir, "photomanager.db.pre-colecoes.bak");
                if (File.Exists(databasePath) && !File.Exists(backupPath))
                {
                    File.Copy(databasePath, backupPath, overwrite: false);
                }

                var migConnStr = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = false }.ToString();
                await using var migConn = new SqliteConnection(migConnStr);
                await migConn.OpenAsync(cancellationToken);

                long preCollectionsCount = 0;
                long preRelationsCount = 0;
                await using (var countCmd = migConn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM Collections";
                    preCollectionsCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(cancellationToken));
                    countCmd.CommandText = "SELECT COUNT(*) FROM PhotoCollections";
                    preRelationsCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(cancellationToken));
                }

                await using var tx = (SqliteTransaction)await migConn.BeginTransactionAsync(cancellationToken);
                try
                {
                    await using (var cmd = migConn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = """
                            CREATE TABLE Collections_new (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                Name TEXT NOT NULL,
                                ParentCollectionId INTEGER NULL,
                                SortOrder INTEGER NOT NULL DEFAULT 0,
                                CreatedAt TEXT NOT NULL,
                                FOREIGN KEY(ParentCollectionId) REFERENCES Collections_new(Id) ON DELETE SET NULL
                            );
                            INSERT INTO Collections_new (Id, Name, ParentCollectionId, SortOrder, CreatedAt)
                            SELECT Id, Name, NULL, ROW_NUMBER() OVER (ORDER BY Name COLLATE NOCASE) - 1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now')
                            FROM Collections;

                            DROP TABLE Collections;
                            ALTER TABLE Collections_new RENAME TO Collections;
                            CREATE UNIQUE INDEX IF NOT EXISTS UX_Collections_Parent_Name ON Collections (COALESCE(ParentCollectionId, 0), Name COLLATE NOCASE);
                            CREATE INDEX IF NOT EXISTS IX_Collections_ParentCollectionId ON Collections(ParentCollectionId);
                            CREATE INDEX IF NOT EXISTS IX_PhotoCollections_CollectionId ON PhotoCollections(CollectionId);
                            """;
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await using (var verifyCmd = migConn.CreateCommand())
                    {
                        verifyCmd.Transaction = tx;
                        verifyCmd.CommandText = "SELECT COUNT(*) FROM Collections";
                        var postCollections = Convert.ToInt64(await verifyCmd.ExecuteScalarAsync(cancellationToken));
                        if (postCollections != preCollectionsCount)
                            throw new InvalidOperationException($"Falha na migração: contagem de coleções divergente ({postCollections} vs {preCollectionsCount}).");

                        verifyCmd.CommandText = "SELECT COUNT(*) FROM PhotoCollections";
                        var postRelations = Convert.ToInt64(await verifyCmd.ExecuteScalarAsync(cancellationToken));
                        if (postRelations != preRelationsCount)
                            throw new InvalidOperationException($"Falha na migração: contagem de relações divergente ({postRelations} vs {preRelationsCount}).");

                        verifyCmd.CommandText = "SELECT COUNT(*) FROM PhotoCollections pc LEFT JOIN Collections c ON c.Id = pc.CollectionId WHERE c.Id IS NULL";
                        var orphanCount = Convert.ToInt64(await verifyCmd.ExecuteScalarAsync(cancellationToken));
                        if (orphanCount > 0)
                            throw new InvalidOperationException($"Falha na migração: detectadas {orphanCount} relações órfãs.");

                        verifyCmd.CommandText = "PRAGMA foreign_key_check";
                        await using var fkReader = await verifyCmd.ExecuteReaderAsync(cancellationToken);
                        if (await fkReader.ReadAsync(cancellationToken))
                            throw new InvalidOperationException("Falha na migração: foreign_key_check falhou.");
                    }

                    await tx.CommitAsync(cancellationToken);
                }
                catch
                {
                    await tx.RollbackAsync(cancellationToken);
                    throw;
                }
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS Photos (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FileName TEXT NOT NULL,
                    CurrentPath TEXT NOT NULL UNIQUE,
                    Extension TEXT NOT NULL,
                    FileSize INTEGER NOT NULL,
                    Width INTEGER NULL,
                    Height INTEGER NULL,
                    CreatedAt TEXT NOT NULL,
                    ModifiedAt TEXT NOT NULL,
                    DateTaken TEXT NULL,
                    ImportedAt TEXT NOT NULL,
                    ContentHash TEXT NULL,
                    IsMissing INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS IX_Photos_IsMissing ON Photos(IsMissing);
                CREATE INDEX IF NOT EXISTS IX_Photos_CurrentPath ON Photos(CurrentPath);
                CREATE TABLE IF NOT EXISTS Categories (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE);
                CREATE TABLE IF NOT EXISTS Tags (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE);
                CREATE TABLE IF NOT EXISTS Collections (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    ParentCollectionId INTEGER NULL,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    FOREIGN KEY(ParentCollectionId) REFERENCES Collections(Id) ON DELETE SET NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS UX_Collections_Parent_Name ON Collections (COALESCE(ParentCollectionId, 0), Name COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS IX_Collections_ParentCollectionId ON Collections(ParentCollectionId);
                CREATE TABLE IF NOT EXISTS PhotoTags (PhotoId INTEGER NOT NULL, TagId INTEGER NOT NULL, PRIMARY KEY(PhotoId, TagId), FOREIGN KEY(PhotoId) REFERENCES Photos(Id) ON DELETE CASCADE, FOREIGN KEY(TagId) REFERENCES Tags(Id) ON DELETE CASCADE);
                CREATE TABLE IF NOT EXISTS PhotoCollections (PhotoId INTEGER NOT NULL, CollectionId INTEGER NOT NULL, PRIMARY KEY(PhotoId, CollectionId), FOREIGN KEY(PhotoId) REFERENCES Photos(Id) ON DELETE CASCADE, FOREIGN KEY(CollectionId) REFERENCES Collections(Id) ON DELETE CASCADE);
                CREATE INDEX IF NOT EXISTS IX_PhotoCollections_CollectionId ON PhotoCollections(CollectionId);
                CREATE TABLE IF NOT EXISTS MetadataHistory (Id INTEGER PRIMARY KEY AUTOINCREMENT, PhotoId INTEGER NOT NULL, Version INTEGER NOT NULL, ChangedAt TEXT NOT NULL, Title TEXT NULL, Description TEXT NULL, Keywords TEXT NOT NULL, Author TEXT NULL, Copyright TEXT NULL, Note TEXT NULL, UNIQUE(PhotoId, Version), FOREIGN KEY(PhotoId) REFERENCES Photos(Id) ON DELETE CASCADE);
                CREATE TABLE IF NOT EXISTS MetadataPresets (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE COLLATE NOCASE, PlanJson TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS ValidationProfiles (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE COLLATE NOCASE, RulesJson TEXT NOT NULL, IsActive INTEGER NOT NULL DEFAULT 0, UpdatedAt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS Agencies (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE COLLATE NOCASE, IsActive INTEGER NOT NULL DEFAULT 1, SortOrder INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS UploadRecords (Id INTEGER PRIMARY KEY AUTOINCREMENT, PhotoId INTEGER NOT NULL, AgencyId INTEGER NOT NULL, UploadedAt TEXT NOT NULL, Status TEXT NOT NULL, MetadataVersion INTEGER NOT NULL, RemoteFileName TEXT NULL, RetryCount INTEGER NOT NULL DEFAULT 0, LastError TEXT NULL, Notes TEXT NULL, FOREIGN KEY(PhotoId) REFERENCES Photos(Id) ON DELETE CASCADE, FOREIGN KEY(AgencyId) REFERENCES Agencies(Id));
                CREATE TABLE IF NOT EXISTS DuplicateIgnores (Hash TEXT PRIMARY KEY, IgnoredAt TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS FolderColors (Path TEXT PRIMARY KEY COLLATE NOCASE, Color INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS ClipMarkers (Id INTEGER PRIMARY KEY AUTOINCREMENT, PhotoId INTEGER NOT NULL, InSeconds REAL NOT NULL, OutSeconds REAL NOT NULL, Name TEXT NOT NULL, Rating INTEGER NOT NULL DEFAULT 0, FOREIGN KEY(PhotoId) REFERENCES Photos(Id) ON DELETE CASCADE);
                CREATE INDEX IF NOT EXISTS IX_ClipMarkers_Photo ON ClipMarkers(PhotoId);
                CREATE TABLE IF NOT EXISTS SmartCollections (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE COLLATE NOCASE, Query TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS BackupTargets (Id INTEGER PRIMARY KEY AUTOINCREMENT, SourceFolder TEXT NOT NULL, TargetFolder TEXT NOT NULL, LastVerifiedAt TEXT NULL, MissingCount INTEGER NULL, CheckedCount INTEGER NULL, DifferentCount INTEGER NULL);
                CREATE TABLE IF NOT EXISTS AiEmbeddings (PhotoId INTEGER NOT NULL, Model TEXT NOT NULL, Vector BLOB NOT NULL, PRIMARY KEY(PhotoId, Model), FOREIGN KEY(PhotoId) REFERENCES Photos(Id) ON DELETE CASCADE);
                CREATE TABLE IF NOT EXISTS AiPeople (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NULL);
                CREATE TABLE IF NOT EXISTS AiFaces (Id INTEGER PRIMARY KEY AUTOINCREMENT, PhotoId INTEGER NOT NULL, PersonId INTEGER NULL, X REAL NOT NULL, Y REAL NOT NULL, W REAL NOT NULL, H REAL NOT NULL, Embedding BLOB NOT NULL, FOREIGN KEY(PhotoId) REFERENCES Photos(Id) ON DELETE CASCADE, FOREIGN KEY(PersonId) REFERENCES AiPeople(Id) ON DELETE SET NULL);
                CREATE TABLE IF NOT EXISTS AiTags (PhotoId INTEGER NOT NULL, Tag TEXT NOT NULL COLLATE NOCASE, Score REAL NOT NULL, PRIMARY KEY(PhotoId, Tag), FOREIGN KEY(PhotoId) REFERENCES Photos(Id) ON DELETE CASCADE);
                CREATE TABLE IF NOT EXISTS AiTranscripts (PhotoId INTEGER PRIMARY KEY, Language TEXT NULL, Text TEXT NOT NULL, FOREIGN KEY(PhotoId) REFERENCES Photos(Id) ON DELETE CASCADE);
                CREATE TABLE IF NOT EXISTS AiIndexState (PhotoId INTEGER NOT NULL, Kind TEXT NOT NULL, IndexedAt TEXT NOT NULL, PRIMARY KEY(PhotoId, Kind), FOREIGN KEY(PhotoId) REFERENCES Photos(Id) ON DELETE CASCADE);
                CREATE INDEX IF NOT EXISTS IX_Photos_FileSize ON Photos(FileSize);
                CREATE INDEX IF NOT EXISTS IX_UploadRecords_PhotoAgency ON UploadRecords(PhotoId, AgencyId, Id DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            foreach (var column in new[] { "CategoryName TEXT NULL", "PersonalNote TEXT NULL", "Rating INTEGER NOT NULL DEFAULT 0", "IsFavorite INTEGER NOT NULL DEFAULT 0", "MetadataVersion INTEGER NOT NULL DEFAULT 0", "HashedAtSize INTEGER NULL", "HashedAtModified TEXT NULL", "DurationSeconds REAL NULL", "MediaInfoRevision INTEGER NOT NULL DEFAULT 0", "AutoRotation INTEGER NOT NULL DEFAULT 0", "UserRotation INTEGER NOT NULL DEFAULT 0", "ColorLabel INTEGER NOT NULL DEFAULT 0", "Latitude REAL NULL", "Longitude REAL NULL", "PlaceName TEXT NULL", "GpsChecked INTEGER NOT NULL DEFAULT 0", "PickFlag INTEGER NOT NULL DEFAULT 0", "UsageStatus INTEGER NOT NULL DEFAULT 0", "UsageNote TEXT NULL", "PerceptualHash INTEGER NULL", "Sharpness REAL NULL" })
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE Photos ADD COLUMN {column}";
                try { await alter.ExecuteNonQueryAsync(cancellationToken); } catch (SqliteException) { }
            }
            await using var seed = connection.CreateCommand();
            seed.CommandText = "INSERT OR IGNORE INTO ValidationProfiles (Name, RulesJson, IsActive, UpdatedAt) VALUES ($name, $rules, 1, $at)";
            seed.Parameters.AddWithValue("$name", ValidationProfile.GenericDefault.Name);
            seed.Parameters.AddWithValue("$rules", ValidationProfile.GenericDefault.Rules.ToJson());
            seed.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            await seed.ExecuteNonQueryAsync(cancellationToken);
            await using var agencySeed = connection.CreateCommand();
            agencySeed.CommandText = """
                INSERT OR IGNORE INTO Agencies (Name, IsActive, SortOrder) VALUES ('Adobe Stock', 1, 0);
                INSERT OR IGNORE INTO Agencies (Name, IsActive, SortOrder) VALUES ('Shutterstock', 1, 1);
                INSERT OR IGNORE INTO Agencies (Name, IsActive, SortOrder) VALUES ('Depositphotos', 1, 2);
                INSERT OR IGNORE INTO Agencies (Name, IsActive, SortOrder) VALUES ('Dreamstime', 1, 3);
                INSERT OR IGNORE INTO Agencies (Name, IsActive, SortOrder) VALUES ('123RF', 1, 4);
                """;
            await agencySeed.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<Photo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = PhotoSelect + " ORDER BY p.FileName";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Photo>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadPhoto(reader));
        return result;
    }

    public async Task<Photo?> FindByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = PhotoSelect + " WHERE p.CurrentPath = $path";
        command.Parameters.AddWithValue("$path", path);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPhoto(reader) : null;
    }

    public async Task<long> AddAsync(Photo photo, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Photos (FileName, CurrentPath, Extension, FileSize, Width, Height, CreatedAt, ModifiedAt, DateTaken, ImportedAt, ContentHash, IsMissing, CategoryName, PersonalNote, Rating, IsFavorite, DurationSeconds, MediaInfoRevision, AutoRotation)
            VALUES ($fileName, $path, $extension, $fileSize, $width, $height, $createdAt, $modifiedAt, $dateTaken, $importedAt, $hash, $missing, NULL, NULL, 0, 0, $duration, $revision, $autoRotation);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$fileName", photo.FileName);
        command.Parameters.AddWithValue("$path", photo.CurrentPath);
        command.Parameters.AddWithValue("$extension", photo.Extension);
        command.Parameters.AddWithValue("$fileSize", photo.FileSize);
        command.Parameters.AddWithValue("$width", (object?)photo.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)photo.Height ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", photo.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$modifiedAt", photo.ModifiedAt.ToString("O"));
        command.Parameters.AddWithValue("$dateTaken", photo.DateTaken?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$importedAt", photo.ImportedAt.ToString("O"));
        command.Parameters.AddWithValue("$hash", (object?)photo.ContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)photo.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", photo.MediaInfoRevision);
        command.Parameters.AddWithValue("$missing", photo.IsMissing ? 1 : 0);
        command.Parameters.AddWithValue("$autoRotation", photo.AutoRotation);
        photo.Id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        return photo.Id;
    }

    public async Task UpdateMissingStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Photos SET IsMissing = CASE WHEN EXISTS (SELECT 1 WHERE CurrentPath IS NOT NULL) THEN 0 ELSE 1 END";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.CommandText = "SELECT Id, CurrentPath FROM Photos";
        await using var reader = await update.ExecuteReaderAsync(cancellationToken);
        var missingIds = new List<long>();
        while (await reader.ReadAsync(cancellationToken)) if (!File.Exists(reader.GetString(1))) missingIds.Add(reader.GetInt64(0));
        await reader.DisposeAsync();
        foreach (var id in missingIds)
        {
            await using var mark = connection.CreateCommand();
            mark.CommandText = "UPDATE Photos SET IsMissing = 1 WHERE Id = $id";
            mark.Parameters.AddWithValue("$id", id);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpdateLocationAsync(Photo photo, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Photos SET FileName = $fileName, CurrentPath = $path, FileSize = $size, ModifiedAt = $modifiedAt, IsMissing = $missing WHERE Id = $id";
        command.Parameters.AddWithValue("$fileName", photo.FileName);
        command.Parameters.AddWithValue("$path", photo.CurrentPath);
        command.Parameters.AddWithValue("$size", photo.FileSize);
        command.Parameters.AddWithValue("$modifiedAt", photo.ModifiedAt.ToString("O"));
        command.Parameters.AddWithValue("$missing", photo.IsMissing ? 1 : 0);
        command.Parameters.AddWithValue("$id", photo.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateMediaInfoAsync(Photo photo, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Photos SET Width = $width, Height = $height, DurationSeconds = $duration, MediaInfoRevision = $revision, AutoRotation = $autoRotation WHERE Id = $id";
        command.Parameters.AddWithValue("$width", (object?)photo.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)photo.Height ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)photo.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", photo.MediaInfoRevision);
        command.Parameters.AddWithValue("$autoRotation", photo.AutoRotation);
        command.Parameters.AddWithValue("$id", photo.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateUserRotationAsync(long photoId, int degrees, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Photos SET UserRotation = $degrees WHERE Id = $id";
        command.Parameters.AddWithValue("$degrees", ((degrees % 360) + 360) % 360);
        command.Parameters.AddWithValue("$id", photoId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateColorLabelAsync(IReadOnlyCollection<long> photoIds, PhotoColor color, CancellationToken cancellationToken = default)
    {
        if (photoIds.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var chunk in photoIds.Distinct().Chunk(400))
        {
            var names = string.Join(",", chunk.Select((_, i) => $"$p{i}"));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE Photos SET ColorLabel = $color WHERE Id IN ({names})";
            command.Parameters.AddWithValue("$color", (int)color);
            for (var i = 0; i < chunk.Length; i++) command.Parameters.AddWithValue($"$p{i}", chunk[i]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CatalogPathEntry>> GetEntriesUnderAsync(string folder, bool recursive, CancellationToken cancellationToken = default)
    {
        var prefix = folder.TrimEnd('\\', '/') + "\\";
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // LIKE com escape próprio ('!'): a barra invertida faz parte do caminho e '%'/'_' podem aparecer em nomes de pasta.
        command.CommandText = "SELECT Id, CurrentPath, ColorLabel FROM Photos WHERE CurrentPath LIKE $pattern ESCAPE '!'"
            + (recursive ? string.Empty : " AND instr(substr(CurrentPath, $start), '\\') = 0");
        command.Parameters.AddWithValue("$pattern", LikePrefix(prefix));
        command.Parameters.AddWithValue("$start", prefix.Length + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CatalogPathEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var path = reader.GetString(1);
            // LIKE só ignora maiúsculas em ASCII ("Ç" ≠ "ç"); a conferência final é feita aqui, do jeito do Windows.
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new CatalogPathEntry(reader.GetInt64(0), path, reader.IsDBNull(2) ? PhotoColor.None : (PhotoColor)reader.GetInt32(2)));
        }
        return result;
    }

    // ---------- Transferência: cor de pastas e candidatos a duplicata ----------

    async Task<IReadOnlyDictionary<string, PhotoColor>> PhotoManager.Application.Transfer.ITransferRepository.GetFolderColorsAsync(string parent, CancellationToken cancellationToken)
    {
        var prefix = parent.TrimEnd('\\', '/') + "\\";
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Path, Color FROM FolderColors WHERE Path LIKE $pattern ESCAPE '!' AND instr(substr(Path, $start), '\\') = 0";
        command.Parameters.AddWithValue("$pattern", LikePrefix(prefix));
        command.Parameters.AddWithValue("$start", prefix.Length + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, PhotoColor>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
            if (reader.GetString(0).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result[reader.GetString(0)] = (PhotoColor)reader.GetInt32(1);
        return result;
    }

    async Task PhotoManager.Application.Transfer.ITransferRepository.SetFolderColorAsync(IReadOnlyList<string> folders, PhotoColor color, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var folder in folders)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = color == PhotoColor.None ? "DELETE FROM FolderColors WHERE Path = $path" : "INSERT INTO FolderColors (Path, Color) VALUES ($path, $color) ON CONFLICT(Path) DO UPDATE SET Color = excluded.Color";
            command.Parameters.AddWithValue("$path", folder.TrimEnd('\\', '/'));
            command.Parameters.AddWithValue("$color", (int)color);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    async Task PhotoManager.Application.Transfer.ITransferRepository.RebaseFolderColorsAsync(string oldPath, string newPath, CancellationToken cancellationToken)
    {
        var rows = await ReadFolderColorsUnderAsync(oldPath, cancellationToken);
        if (rows.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (path, color) in rows)
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM FolderColors WHERE Path = $path", cancellationToken, ("$path", path));
            await ExecuteAsync(connection, transaction, "INSERT OR REPLACE INTO FolderColors (Path, Color) VALUES ($path, $color)", cancellationToken,
                ("$path", PhotoManager.Application.Transfer.TransferNames.Rebase(path, oldPath, newPath)), ("$color", (int)color));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    async Task PhotoManager.Application.Transfer.ITransferRepository.RemoveFolderColorsAsync(IReadOnlyList<string> folders, CancellationToken cancellationToken)
    {
        foreach (var folder in folders)
        {
            var rows = await ReadFolderColorsUnderAsync(folder, cancellationToken);
            if (rows.Count == 0) continue;
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var (path, _) in rows) await ExecuteAsync(connection, transaction, "DELETE FROM FolderColors WHERE Path = $path", cancellationToken, ("$path", path));
            await transaction.CommitAsync(cancellationToken);
        }
    }

    async Task<IReadOnlyList<PhotoManager.Application.Transfer.CatalogSizeMatch>> PhotoManager.Application.Transfer.ITransferRepository.GetSizeMatchesAsync(IReadOnlyCollection<long> sizes, CancellationToken cancellationToken)
    {
        var result = new List<PhotoManager.Application.Transfer.CatalogSizeMatch>();
        if (sizes.Count == 0) return result;
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var chunk in sizes.Distinct().Chunk(400))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT Id, CurrentPath, FileSize, ContentHash, HashedAtSize FROM Photos WHERE IsMissing = 0 AND FileSize IN ({string.Join(",", chunk.Select((_, i) => $"$s{i}"))})";
            for (var i = 0; i < chunk.Length; i++) command.Parameters.AddWithValue($"$s{i}", chunk[i]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }
        return result;
    }

    /// <summary>A pasta e todas as subpastas marcadas dentro dela.</summary>
    private async Task<List<(string Path, PhotoColor Color)>> ReadFolderColorsUnderAsync(string folder, CancellationToken cancellationToken)
    {
        var root = folder.TrimEnd('\\', '/');
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Path, Color FROM FolderColors WHERE Path = $root OR Path LIKE $pattern ESCAPE '!'";
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$pattern", LikePrefix(root + "\\"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(string, PhotoColor)>();
        while (await reader.ReadAsync(cancellationToken))
            if (PhotoManager.Application.Transfer.TransferNames.IsSameOrInside(reader.GetString(0), root)) rows.Add((reader.GetString(0), (PhotoColor)reader.GetInt32(1)));
        return rows;
    }

    private static string LikePrefix(string prefix) => prefix.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";

    public async Task UpdatePathsAsync(IReadOnlyList<(long Id, string NewPath)> moves, CancellationToken cancellationToken = default)
    {
        if (moves.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        // CurrentPath é único: numa troca (a→b, b→a) todos passam antes por um caminho provisório, dentro da mesma transação.
        if (moves.Count > 1)
            foreach (var (id, _) in moves)
                await ExecuteAsync(connection, transaction, "UPDATE Photos SET CurrentPath = $temporary WHERE Id = $id", cancellationToken, ("$temporary", $"\u0001moving:{id}"), ("$id", id));
        foreach (var (id, newPath) in moves)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Photos SET CurrentPath = $path, FileName = $name, IsMissing = 0 WHERE Id = $id";
            command.Parameters.AddWithValue("$path", newPath);
            command.Parameters.AddWithValue("$name", Path.GetFileName(newPath));
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateGpsAsync(long photoId, double? latitude, double? longitude, string? placeName, bool gpsChecked, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Photos SET Latitude = $lat, Longitude = $lon, PlaceName = $place, GpsChecked = $checked WHERE Id = $id";
        command.Parameters.AddWithValue("$lat", (object?)latitude ?? DBNull.Value);
        command.Parameters.AddWithValue("$lon", (object?)longitude ?? DBNull.Value);
        command.Parameters.AddWithValue("$place", (object?)placeName ?? DBNull.Value);
        command.Parameters.AddWithValue("$checked", gpsChecked ? 1 : 0);
        command.Parameters.AddWithValue("$id", photoId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeletePhotosAsync(IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken = default)
    {
        if (photoIds.Count == 0) return 0;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var removed = 0;
        foreach (var chunk in photoIds.Distinct().Chunk(400))
        {
            var names = string.Join(",", chunk.Select((_, i) => $"$p{i}"));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Filhas apagadas explicitamente (o cascade da FK faria o mesmo; assim a remoção não depende dele).
            command.CommandText = $"""
                DELETE FROM PhotoTags WHERE PhotoId IN ({names});
                DELETE FROM PhotoCollections WHERE PhotoId IN ({names});
                DELETE FROM MetadataHistory WHERE PhotoId IN ({names});
                DELETE FROM UploadRecords WHERE PhotoId IN ({names});
                DELETE FROM Photos WHERE Id IN ({names});
                """;
            for (var i = 0; i < chunk.Length; i++) command.Parameters.AddWithValue($"$p{i}", chunk[i]);
            await command.ExecuteNonQueryAsync(cancellationToken);
            removed += chunk.Length;
        }
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    public Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default) => GetNamesAsync("Categories");
    public Task<IReadOnlyList<string>> GetTagsAsync(CancellationToken cancellationToken = default) => GetNamesAsync("Tags");
    public Task<IReadOnlyList<string>> GetCollectionsAsync(CancellationToken cancellationToken = default) => GetNamesAsync("Collections");

    public async Task SaveAsync(long photoId, string? category, string? note, int rating, bool favorite, IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, "UPDATE Photos SET CategoryName = $category, PersonalNote = $note, Rating = $rating, IsFavorite = $favorite WHERE Id = $id", cancellationToken,
            ("$category", (object?)Normalize(category) ?? DBNull.Value), ("$note", (object?)Normalize(note) ?? DBNull.Value), ("$rating", Math.Clamp(rating, 0, 5)), ("$favorite", favorite ? 1 : 0), ("$id", photoId));
        await ExecuteAsync(connection, transaction, "DELETE FROM PhotoTags WHERE PhotoId = $id", cancellationToken, ("$id", photoId));
        foreach (var tag in tags.Select(Normalize).Where(value => value is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await ExecuteAsync(connection, transaction, "INSERT OR IGNORE INTO Tags(Name) VALUES ($name); INSERT OR IGNORE INTO PhotoTags(PhotoId, TagId) SELECT $id, Id FROM Tags WHERE Name = $name", cancellationToken, ("$name", tag!), ("$id", photoId));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordVersionAsync(long photoId, int version, MetadataEdit values, string? note, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction,
            "INSERT OR REPLACE INTO MetadataHistory (PhotoId, Version, ChangedAt, Title, Description, Keywords, Author, Copyright, Note) VALUES ($id, $version, $at, $title, $description, $keywords, $author, $copyright, $note)",
            cancellationToken,
            ("$id", photoId), ("$version", version), ("$at", DateTime.UtcNow.ToString("O")),
            ("$title", (object?)values.Title ?? DBNull.Value), ("$description", (object?)values.Description ?? DBNull.Value),
            ("$keywords", System.Text.Json.JsonSerializer.Serialize(values.Keywords)),
            ("$author", (object?)values.Author ?? DBNull.Value), ("$copyright", (object?)values.Copyright ?? DBNull.Value), ("$note", (object?)note ?? DBNull.Value));
        await ExecuteAsync(connection, transaction, "UPDATE Photos SET MetadataVersion = MAX(MetadataVersion, $version), ContentHash = NULL, HashedAtSize = NULL, HashedAtModified = NULL WHERE Id = $id", cancellationToken, ("$id", photoId), ("$version", version));
        await transaction.CommitAsync(cancellationToken);
    }

    async Task<IReadOnlyList<MetadataPreset>> IMetadataPresetRepository.GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, PlanJson FROM MetadataPresets ORDER BY Name COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MetadataPreset>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new MetadataPreset(reader.GetInt64(0), reader.GetString(1), BatchMetadataPlan.FromJson(reader.GetString(2))));
        return result;
    }

    public async Task<MetadataPreset> SaveAsync(string name, BatchMetadataPlan plan, CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Informe um nome para o preset.", nameof(name));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MetadataPresets (Name, PlanJson, UpdatedAt) VALUES ($name, $json, $at)
            ON CONFLICT(Name) DO UPDATE SET PlanJson = excluded.PlanJson, UpdatedAt = excluded.UpdatedAt
            RETURNING Id, Name
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$json", plan.ToJson());
        command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new MetadataPreset(reader.GetInt64(0), reader.GetString(1), plan);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM MetadataPresets WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<IReadOnlyList<ValidationProfile>> IValidationProfileRepository.GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, RulesJson, IsActive FROM ValidationProfiles ORDER BY Name COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var profiles = new List<ValidationProfile>();
        while (await reader.ReadAsync(cancellationToken))
            profiles.Add(new(reader.GetInt64(0), reader.GetString(1), ValidationRules.FromJson(reader.GetString(2)), reader.GetInt64(3) != 0));
        return profiles;
    }

    async Task<ValidationProfile> IValidationProfileRepository.SaveAsync(ValidationProfile profile, CancellationToken cancellationToken)
    {
        var name = profile.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("Informe um nome para o perfil.", nameof(profile));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (profile.Id == 0)
        {
            command.CommandText = "INSERT INTO ValidationProfiles (Name, RulesJson, IsActive, UpdatedAt) VALUES ($name, $rules, $active, $at) RETURNING Id";
            command.Parameters.AddWithValue("$active", profile.IsActive ? 1 : 0);
        }
        else
        {
            command.CommandText = "UPDATE ValidationProfiles SET Name = $name, RulesJson = $rules, UpdatedAt = $at WHERE Id = $id; SELECT $id";
            command.Parameters.AddWithValue("$id", profile.Id);
        }
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$rules", profile.Rules.ToJson());
        command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return new(id, name, profile.Rules, profile.IsActive);
    }

    async Task IValidationProfileRepository.SetActiveAsync(long profileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, "UPDATE ValidationProfiles SET IsActive = 0", cancellationToken);
        await ExecuteAsync(connection, transaction, "UPDATE ValidationProfiles SET IsActive = 1 WHERE Id = $id", cancellationToken, ("$id", profileId));
        await transaction.CommitAsync(cancellationToken);
    }

    async Task IValidationProfileRepository.DeleteAsync(long profileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ValidationProfiles";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (count <= 1) throw new InvalidOperationException("Não é possível excluir o último perfil de validação.");
        command.CommandText = "SELECT IsActive FROM ValidationProfiles WHERE Id = $id";
        command.Parameters.AddWithValue("$id", profileId);
        var active = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0) != 0;
        command.CommandText = "DELETE FROM ValidationProfiles WHERE Id = $id";
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (active)
        {
            command.Parameters.Clear();
            command.CommandText = "UPDATE ValidationProfiles SET IsActive = 1 WHERE Id = (SELECT Id FROM ValidationProfiles ORDER BY Name COLLATE NOCASE LIMIT 1)";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    async Task<IReadOnlyList<Agency>> IUploadHistoryRepository.GetAgenciesAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = activeOnly
            ? "SELECT Id, Name, IsActive, SortOrder FROM Agencies WHERE IsActive = 1 ORDER BY SortOrder, Name COLLATE NOCASE"
            : "SELECT Id, Name, IsActive, SortOrder FROM Agencies ORDER BY SortOrder, Name COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Agency>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt32(3)));
        return result;
    }

    async Task<Agency> IUploadHistoryRepository.SaveAgencyAsync(Agency agency, CancellationToken cancellationToken)
    {
        var name = agency.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("Informe o nome do banco.", nameof(agency));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (agency.Id == 0)
        {
            command.CommandText = "INSERT INTO Agencies (Name, IsActive, SortOrder) VALUES ($name, $active, (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Agencies)) RETURNING Id, SortOrder";
            command.Parameters.AddWithValue("$active", agency.IsActive ? 1 : 0);
        }
        else
        {
            command.CommandText = "UPDATE Agencies SET Name = $name, IsActive = $active, SortOrder = $order WHERE Id = $id; SELECT Id, SortOrder FROM Agencies WHERE Id = $id";
            command.Parameters.AddWithValue("$id", agency.Id);
            command.Parameters.AddWithValue("$order", agency.Order);
            command.Parameters.AddWithValue("$active", agency.IsActive ? 1 : 0);
        }
        command.Parameters.AddWithValue("$name", name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Não foi possível salvar o banco.");
        return new(agency.Id == 0 ? reader.GetInt64(0) : agency.Id, name, agency.IsActive, reader.GetInt32(1));
    }

    async Task IUploadHistoryRepository.SetAgencyActiveAsync(long agencyId, bool active, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Agencies SET IsActive = $active WHERE Id = $id";
        command.Parameters.AddWithValue("$active", active ? 1 : 0);
        command.Parameters.AddWithValue("$id", agencyId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task IUploadHistoryRepository.SetAgencyOrderAsync(IReadOnlyList<long> agencyIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        for (var index = 0; index < agencyIds.Count; index++)
            await ExecuteAsync(connection, transaction, "UPDATE Agencies SET SortOrder = $order WHERE Id = $id", cancellationToken, ("$order", index), ("$id", agencyIds[index]));
        await transaction.CommitAsync(cancellationToken);
    }

    async Task<UploadRecord> IUploadHistoryRepository.AppendRecordAsync(UploadRecordInput input, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UploadRecords (PhotoId, AgencyId, UploadedAt, Status, MetadataVersion, RemoteFileName, RetryCount, LastError, Notes)
            VALUES ($photo, $agency, $at, $status, $version, $remote, $retry, $error, $notes)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$photo", input.PhotoId);
        command.Parameters.AddWithValue("$agency", input.AgencyId);
        command.Parameters.AddWithValue("$at", input.UploadedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$status", input.Status.ToString());
        command.Parameters.AddWithValue("$version", input.MetadataVersion);
        command.Parameters.AddWithValue("$remote", (object?)input.RemoteFileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$retry", input.RetryCount);
        command.Parameters.AddWithValue("$error", (object?)input.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)input.Notes ?? DBNull.Value);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        var agency = await GetAgencyAsync(input.AgencyId, cancellationToken) ?? throw new InvalidOperationException("Banco não encontrado após gravar o histórico.");
        return new(id, input.PhotoId, input.AgencyId, input.UploadedAt, input.Status, input.MetadataVersion, input.RemoteFileName, input.RetryCount, input.LastError, input.Notes, agency.Name, agency.IsActive);
    }

    async Task<IReadOnlyList<UploadRecord>> IUploadHistoryRepository.GetCurrentRecordsAsync(IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken)
    {
        if (photoIds.Count == 0) return [];
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var parameters = new List<string>();
        var index = 0;
        foreach (var photoId in photoIds.Distinct()) { var name = "$p" + index++; parameters.Add(name); command.Parameters.AddWithValue(name, photoId); }
        command.CommandText = $"""
            WITH latest AS (
                SELECT ur.*, ROW_NUMBER() OVER (PARTITION BY ur.PhotoId, ur.AgencyId ORDER BY ur.Id DESC) AS RowNumber
                FROM UploadRecords ur WHERE ur.PhotoId IN ({string.Join(",", parameters)})
            )
            SELECT l.Id, l.PhotoId, l.AgencyId, l.UploadedAt, l.Status, l.MetadataVersion, l.RemoteFileName, l.RetryCount, l.LastError, l.Notes, a.Name, a.IsActive
            FROM latest l JOIN Agencies a ON a.Id = l.AgencyId WHERE l.RowNumber = 1
            ORDER BY a.SortOrder, a.Name COLLATE NOCASE
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<UploadRecord>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadUploadRecord(reader));
        return result;
    }

    async Task<IReadOnlyList<UploadRecord>> IUploadHistoryRepository.GetHistoryAsync(long photoId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ur.Id, ur.PhotoId, ur.AgencyId, ur.UploadedAt, ur.Status, ur.MetadataVersion, ur.RemoteFileName, ur.RetryCount, ur.LastError, ur.Notes, a.Name, a.IsActive FROM UploadRecords ur JOIN Agencies a ON a.Id = ur.AgencyId WHERE ur.PhotoId = $photo ORDER BY ur.Id DESC";
        command.Parameters.AddWithValue("$photo", photoId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<UploadRecord>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadUploadRecord(reader));
        return result;
    }

    async Task IDuplicateRepository.SaveHashAsync(long photoId, string hash, long size, DateTime modifiedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Photos SET ContentHash = $hash, HashedAtSize = $size, HashedAtModified = $modified WHERE Id = $id";
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$modified", modifiedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$id", photoId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<IReadOnlySet<string>> IDuplicateRepository.GetIgnoredHashesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Hash FROM DuplicateIgnores";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    async Task IDuplicateRepository.SetIgnoredHashAsync(string hash, bool ignored, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ignored
            ? "INSERT INTO DuplicateIgnores (Hash, IgnoredAt) VALUES ($hash, $at) ON CONFLICT(Hash) DO UPDATE SET IgnoredAt = excluded.IgnoredAt"
            : "DELETE FROM DuplicateIgnores WHERE Hash = $hash";
        command.Parameters.AddWithValue("$hash", hash);
        if (ignored) command.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<IReadOnlySet<long>> IDuplicateRepository.GetDuplicatePhotoIdsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.Id FROM Photos p JOIN (SELECT ContentHash FROM Photos WHERE ContentHash IS NOT NULL AND ContentHash <> '' GROUP BY ContentHash HAVING COUNT(*) > 1) d ON d.ContentHash = p.ContentHash WHERE NOT EXISTS (SELECT 1 FROM DuplicateIgnores i WHERE i.Hash = p.ContentHash)";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<long>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetInt64(0));
        return result;
    }

    private async Task<Agency?> GetAgencyAsync(long agencyId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, IsActive, SortOrder FROM Agencies WHERE Id = $id";
        command.Parameters.AddWithValue("$id", agencyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt32(3)) : null;
    }

    public async Task<IReadOnlyList<MetadataVersionEntry>> GetHistoryAsync(long photoId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, ChangedAt, Title, Description, Keywords, Author, Copyright, Note FROM MetadataHistory WHERE PhotoId = $id ORDER BY Version DESC";
        command.Parameters.AddWithValue("$id", photoId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MetadataVersionEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new MetadataEdit
            {
                Title = reader.IsDBNull(2) ? null : reader.GetString(2), Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                Keywords = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [],
                Author = reader.IsDBNull(5) ? null : reader.GetString(5), Copyright = reader.IsDBNull(6) ? null : reader.GetString(6)
            };
            result.Add(new MetadataVersionEntry(photoId, reader.GetInt32(0), DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind), values, reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return result;
    }

    private async Task<IReadOnlyList<string>> GetNamesAsync(string table)
    {
        await using var connection = await OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Name FROM {table} ORDER BY Name";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private const string PhotoSelect = """
        SELECT p.Id, p.FileName, p.CurrentPath, p.Extension, p.FileSize, p.Width, p.Height, p.CreatedAt, p.ModifiedAt, p.DateTaken, p.ImportedAt, p.ContentHash, p.IsMissing,
               p.CategoryName, p.PersonalNote, p.Rating, p.IsFavorite,
               (SELECT group_concat(t.Name, char(30)) FROM PhotoTags pt JOIN Tags t ON t.Id = pt.TagId WHERE pt.PhotoId = p.Id),
               (SELECT group_concat(pc.CollectionId || char(31) || c.Name || char(31) || coalesce(p2.Name, ''), char(30))
                FROM PhotoCollections pc
                JOIN Collections c ON c.Id = pc.CollectionId
                LEFT JOIN Collections p2 ON p2.Id = c.ParentCollectionId
                WHERE pc.PhotoId = p.Id),
               p.MetadataVersion, p.HashedAtSize, p.HashedAtModified, p.DurationSeconds, p.MediaInfoRevision, p.AutoRotation, p.UserRotation, p.ColorLabel, p.Latitude, p.Longitude, p.PlaceName, p.GpsChecked,
               p.PickFlag, p.UsageStatus, p.UsageNote, p.PerceptualHash, p.Sharpness
        FROM Photos p
        """;

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static Photo ReadPhoto(SqliteDataReader reader)
    {
        var rawCollections = reader.IsDBNull(18) ? null : reader.GetString(18);
        var (collectionIds, collections) = ParseCollections(rawCollections);

        return new()
        {
            Id = reader.GetInt64(0), FileName = reader.GetString(1), CurrentPath = reader.GetString(2), Extension = reader.GetString(3), FileSize = reader.GetInt64(4),
            Width = reader.IsDBNull(5) ? null : reader.GetInt32(5), Height = reader.IsDBNull(6) ? null : reader.GetInt32(6), CreatedAt = DateTime.Parse(reader.GetString(7)), ModifiedAt = DateTime.Parse(reader.GetString(8)),
            DateTaken = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9)), ImportedAt = DateTime.Parse(reader.GetString(10)), ContentHash = reader.IsDBNull(11) ? null : reader.GetString(11), IsMissing = reader.GetInt64(12) != 0,
            CategoryName = reader.IsDBNull(13) ? null : reader.GetString(13), PersonalNote = reader.IsDBNull(14) ? null : reader.GetString(14), Rating = reader.GetInt32(15), IsFavorite = reader.GetInt64(16) != 0,
            Tags = SplitNames(reader.IsDBNull(17) ? null : reader.GetString(17)),
            CollectionIds = collectionIds,
            Collections = collections,
            MetadataVersion = reader.GetInt32(19),
            HashedAtSize = reader.IsDBNull(20) ? null : reader.GetInt64(20), HashedAtModified = reader.IsDBNull(21) ? null : DateTime.Parse(reader.GetString(21), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DurationSeconds = reader.IsDBNull(22) ? null : reader.GetDouble(22),
            MediaInfoRevision = reader.IsDBNull(23) ? 0 : reader.GetInt32(23),
            AutoRotation = reader.IsDBNull(24) ? 0 : reader.GetInt32(24),
            UserRotation = reader.IsDBNull(25) ? 0 : reader.GetInt32(25),
            ColorLabel = reader.IsDBNull(26) ? PhotoColor.None : (PhotoColor)reader.GetInt32(26),
            Latitude = reader.IsDBNull(27) ? null : reader.GetDouble(27),
            Longitude = reader.IsDBNull(28) ? null : reader.GetDouble(28),
            PlaceName = reader.IsDBNull(29) ? null : reader.GetString(29),
            GpsChecked = !reader.IsDBNull(30) && reader.GetInt64(30) != 0,
            Pick = reader.IsDBNull(31) ? PickFlag.None : (PickFlag)reader.GetInt32(31),
            Usage = reader.IsDBNull(32) ? UsageStatus.None : (UsageStatus)reader.GetInt32(32),
            UsageNote = reader.IsDBNull(33) ? null : reader.GetString(33),
            PerceptualHash = reader.IsDBNull(34) ? null : reader.GetInt64(34),
            Sharpness = reader.IsDBNull(35) ? null : reader.GetDouble(35)
        };
    }

    private static (List<long> Ids, List<string> Names) ParseCollections(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ([], []);
        var rawEntries = value.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<(long Id, string Name, string? ParentName)>();
        foreach (var entry in rawEntries)
        {
            var parts = entry.Split(FieldSeparator);
            if (parts.Length >= 2 && long.TryParse(parts[0], out var id))
            {
                var name = parts[1];
                var parent = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null;
                parsed.Add((id, name, parent));
            }
            else
            {
                parsed.Add((0, entry, null));
            }
        }

        var ids = parsed.Where(p => p.Id > 0).Select(p => p.Id).ToList();
        var duplicates = parsed
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var names = new List<string>();
        foreach (var p in parsed)
        {
            if (duplicates.Contains(p.Name) && !string.IsNullOrWhiteSpace(p.ParentName))
                names.Add($"{p.ParentName} / {p.Name}");
            else
                names.Add(p.Name);
        }

        return (ids, names);
    }

    private static UploadRecord ReadUploadRecord(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
        Enum.Parse<UploadRecordStatus>(reader.GetString(4), ignoreCase: true), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10), reader.GetInt64(11) != 0);

    /// <summary>Separadores de controle (RS/US) usados no group_concat: nomes digitados pelo usuário nunca os contêm (o serviço os rejeita).</summary>
    private const char EntrySeparator = '\u001e';
    private const char FieldSeparator = '\u001f';

    private static List<string> SplitNames(string? value) => string.IsNullOrWhiteSpace(value) ? [] : value.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    #region ICollectionRepository

    async Task<IReadOnlyList<Collection>> ICollectionRepository.GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, ParentCollectionId, SortOrder, CreatedAt FROM Collections ORDER BY COALESCE(ParentCollectionId, 0), SortOrder, Name COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<Collection>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new Collection
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                ParentCollectionId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                SortOrder = reader.GetInt32(3),
                CreatedAt = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind)
            });
        }
        return list;
    }

    async Task<Collection?> ICollectionRepository.GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, ParentCollectionId, SortOrder, CreatedAt FROM Collections WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new Collection
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            ParentCollectionId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            SortOrder = reader.GetInt32(3),
            CreatedAt = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind)
        };
    }

    async Task<Collection> ICollectionRepository.InsertAsync(Collection collection, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Collections (Name, ParentCollectionId, SortOrder, CreatedAt)
            VALUES ($name, $parent, $sortOrder, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", collection.Name);
        command.Parameters.AddWithValue("$parent", (object?)collection.ParentCollectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sortOrder", collection.SortOrder);
        command.Parameters.AddWithValue("$createdAt", collection.CreatedAt.ToString("O"));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        collection.Id = id;
        return collection;
    }

    async Task ICollectionRepository.UpdateAsync(Collection collection, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Collections SET Name = $name, ParentCollectionId = $parent, SortOrder = $sortOrder WHERE Id = $id";
        command.Parameters.AddWithValue("$name", collection.Name);
        command.Parameters.AddWithValue("$parent", (object?)collection.ParentCollectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sortOrder", collection.SortOrder);
        command.Parameters.AddWithValue("$id", collection.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    async Task<IReadOnlyList<Collection>> ICollectionRepository.GetChildrenAsync(long? parentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (parentId.HasValue)
        {
            command.CommandText = "SELECT Id, Name, ParentCollectionId, SortOrder, CreatedAt FROM Collections WHERE ParentCollectionId = $parent ORDER BY SortOrder, Name COLLATE NOCASE";
            command.Parameters.AddWithValue("$parent", parentId.Value);
        }
        else
        {
            command.CommandText = "SELECT Id, Name, ParentCollectionId, SortOrder, CreatedAt FROM Collections WHERE ParentCollectionId IS NULL ORDER BY SortOrder, Name COLLATE NOCASE";
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<Collection>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new Collection
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                ParentCollectionId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                SortOrder = reader.GetInt32(3),
                CreatedAt = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind)
            });
        }
        return list;
    }

    async Task<IReadOnlyList<long>> ICollectionRepository.GetPhotoIdsInCollectionAsync(long collectionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PhotoId FROM PhotoCollections WHERE CollectionId = $id";
        command.Parameters.AddWithValue("$id", collectionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<long>();
        while (await reader.ReadAsync(cancellationToken)) list.Add(reader.GetInt64(0));
        return list;
    }

    async Task<IReadOnlyDictionary<long, int>> ICollectionRepository.GetDirectCountsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CollectionId, COUNT(DISTINCT PhotoId) FROM PhotoCollections GROUP BY CollectionId";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var dict = new Dictionary<long, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            dict[reader.GetInt64(0)] = reader.GetInt32(1);
        }
        return dict;
    }

    async Task<int> ICollectionRepository.GetTotalDistinctInAnyCollectionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(DISTINCT PhotoId) FROM PhotoCollections";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    async Task<int> ICollectionRepository.GetTotalPhotosCountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Photos";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    async Task<int> ICollectionRepository.AddPhotosAsync(long collectionId, IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken)
    {
        if (photoIds.Count == 0) return 0;
        await using var connection = await OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var added = 0;
        foreach (var photoId in photoIds.Distinct())
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO PhotoCollections (PhotoId, CollectionId) VALUES ($photoId, $collectionId)";
            cmd.Parameters.AddWithValue("$photoId", photoId);
            cmd.Parameters.AddWithValue("$collectionId", collectionId);
            added += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return added;
    }

    async Task<int> ICollectionRepository.RemovePhotosAsync(long collectionId, IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken)
    {
        if (photoIds.Count == 0) return 0;
        await using var connection = await OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var removed = 0;
        foreach (var photoId in photoIds.Distinct())
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM PhotoCollections WHERE CollectionId = $collectionId AND PhotoId = $photoId";
            cmd.Parameters.AddWithValue("$collectionId", collectionId);
            cmd.Parameters.AddWithValue("$photoId", photoId);
            removed += await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return removed;
    }

    async Task<MovePhotosResult> ICollectionRepository.MovePhotosAsync(long fromId, long toId, IReadOnlyCollection<long> photoIds, CancellationToken cancellationToken)
    {
        if (fromId == toId || photoIds.Count == 0) return new MovePhotosResult(0, 0);
        await using var connection = await OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var moved = 0;
        var alreadyInTarget = 0;
        foreach (var photoId in photoIds.Distinct())
        {
            await using var checkCmd = connection.CreateCommand();
            checkCmd.Transaction = tx;
            checkCmd.CommandText = "SELECT COUNT(*) FROM PhotoCollections WHERE PhotoId = $photoId AND CollectionId = $toId";
            checkCmd.Parameters.AddWithValue("$photoId", photoId);
            checkCmd.Parameters.AddWithValue("$toId", toId);
            var existsInTarget = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(cancellationToken)) > 0;

            if (existsInTarget)
            {
                alreadyInTarget++;
            }
            else
            {
                await using var insCmd = connection.CreateCommand();
                insCmd.Transaction = tx;
                insCmd.CommandText = "INSERT INTO PhotoCollections (PhotoId, CollectionId) VALUES ($photoId, $toId)";
                insCmd.Parameters.AddWithValue("$photoId", photoId);
                insCmd.Parameters.AddWithValue("$toId", toId);
                await insCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var delCmd = connection.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM PhotoCollections WHERE PhotoId = $photoId AND CollectionId = $fromId";
            delCmd.Parameters.AddWithValue("$photoId", photoId);
            delCmd.Parameters.AddWithValue("$fromId", fromId);
            var rows = await delCmd.ExecuteNonQueryAsync(cancellationToken);
            if (rows > 0) moved++;
        }
        await tx.CommitAsync(cancellationToken);
        return new MovePhotosResult(moved, alreadyInTarget);
    }

    async Task<DeleteResult> ICollectionRepository.DeleteHierarchyAsync(long id, DeleteMode mode, IReadOnlyList<(long Id, string OldName, string NewName)> childRenames, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var getCmd = connection.CreateCommand();
        getCmd.Transaction = tx;
        getCmd.CommandText = "SELECT ParentCollectionId FROM Collections WHERE Id = $id";
        getCmd.Parameters.AddWithValue("$id", id);
        var parentObj = await getCmd.ExecuteScalarAsync(cancellationToken);
        if (parentObj is null)
            throw new InvalidOperationException("Coleção não encontrada.");
        var parentId = parentObj is DBNull ? (long?)null : Convert.ToInt64(parentObj);

        if (mode == DeleteMode.PromoteChildren)
        {
            // A coleção excluída ainda ocupa o nome no nível dela; sem liberar, um filho homônimo (ex.: Natal > Natal) violaria o índice único ao subir.
            await using var freeNameCmd = connection.CreateCommand();
            freeNameCmd.Transaction = tx;
            freeNameCmd.CommandText = "UPDATE Collections SET Name = '__excluindo__' || Id WHERE Id = $id";
            freeNameCmd.Parameters.AddWithValue("$id", id);
            await freeNameCmd.ExecuteNonQueryAsync(cancellationToken);

            foreach (var (childId, _, newName) in childRenames)
            {
                await using var renCmd = connection.CreateCommand();
                renCmd.Transaction = tx;
                renCmd.CommandText = "UPDATE Collections SET Name = $newName WHERE Id = $childId";
                renCmd.Parameters.AddWithValue("$newName", newName);
                renCmd.Parameters.AddWithValue("$childId", childId);
                await renCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var promCmd = connection.CreateCommand();
            promCmd.Transaction = tx;
            promCmd.CommandText = "UPDATE Collections SET ParentCollectionId = $parentId WHERE ParentCollectionId = $id";
            promCmd.Parameters.AddWithValue("$parentId", (object?)parentId ?? DBNull.Value);
            promCmd.Parameters.AddWithValue("$id", id);
            var promoted = await promCmd.ExecuteNonQueryAsync(cancellationToken);

            await using var delAssocCmd = connection.CreateCommand();
            delAssocCmd.Transaction = tx;
            delAssocCmd.CommandText = "DELETE FROM PhotoCollections WHERE CollectionId = $id";
            delAssocCmd.Parameters.AddWithValue("$id", id);
            await delAssocCmd.ExecuteNonQueryAsync(cancellationToken);

            await using var delColCmd = connection.CreateCommand();
            delColCmd.Transaction = tx;
            delColCmd.CommandText = "DELETE FROM Collections WHERE Id = $id";
            delColCmd.Parameters.AddWithValue("$id", id);
            await delColCmd.ExecuteNonQueryAsync(cancellationToken);

            await tx.CommitAsync(cancellationToken);
            return new DeleteResult(id, 1, promoted, childRenames);
        }
        else
        {
            var allIdsToDelete = new List<long> { id };
            var queue = new Queue<long>();
            queue.Enqueue(id);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                await using var childCmd = connection.CreateCommand();
                childCmd.Transaction = tx;
                childCmd.CommandText = "SELECT Id FROM Collections WHERE ParentCollectionId = $cur";
                childCmd.Parameters.AddWithValue("$cur", cur);
                await using var reader = await childCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var childId = reader.GetInt64(0);
                    allIdsToDelete.Add(childId);
                    queue.Enqueue(childId);
                }
            }

            foreach (var colId in allIdsToDelete)
            {
                await using var delAssocCmd = connection.CreateCommand();
                delAssocCmd.Transaction = tx;
                delAssocCmd.CommandText = "DELETE FROM PhotoCollections WHERE CollectionId = $id";
                delAssocCmd.Parameters.AddWithValue("$id", colId);
                await delAssocCmd.ExecuteNonQueryAsync(cancellationToken);

                await using var delColCmd = connection.CreateCommand();
                delColCmd.Transaction = tx;
                delColCmd.CommandText = "DELETE FROM Collections WHERE Id = $id";
                delColCmd.Parameters.AddWithValue("$id", colId);
                await delColCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return new DeleteResult(id, allIdsToDelete.Count, 0, []);
        }
    }

    async Task<bool> ICollectionRepository.HasOrphansAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM PhotoCollections pc LEFT JOIN Collections c ON c.Id = pc.CollectionId WHERE c.Id IS NULL";
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return count > 0;
    }

    public Task<IReadOnlyList<Collection>> GetAllCollectionsAsync(CancellationToken cancellationToken = default) => ((ICollectionRepository)this).GetAllAsync(cancellationToken);
    public Task<IReadOnlyDictionary<long, int>> GetDirectCountsAsync(CancellationToken cancellationToken = default) => ((ICollectionRepository)this).GetDirectCountsAsync(cancellationToken);

    #endregion
}
