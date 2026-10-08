using Microsoft.Data.Sqlite;
using PhotoManager.Application.Metadata;
using PhotoManager.Application.Microstock;
using PhotoManager.Domain.Photos;
using PhotoManager.Persistence;

namespace PhotoManager.Tests;

public sealed class MicrostockValidationTests
{
    [Fact]
    public void GenericProfile_ReportsEachMissingRuleWithReadableMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "photo.jpg");
        File.WriteAllBytes(path, [1]);
        try
        {
            var photo = CreatePhoto(path, width: 1000, height: 1000);
            var metadata = new PhotoMetadata { Title = "x", Keywords = ["uma", "duas"] };
            var result = ValidationProfileValidator.Validate(photo, metadata, ValidationProfile.GenericDefault);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue => issue.Code == "keywords-min" && issue.Message.Contains("Faltam 3"));
            Assert.DoesNotContain(result.Issues, issue => issue.Code == "title-required");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ProfileRules_CanRequireDescriptionAuthorFormatAndDimensions()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "photo.png");
        File.WriteAllBytes(path, [1]);
        try
        {
            var photo = CreatePhoto(path, width: 100, height: 100);
            var profile = ValidationProfile.GenericDefault with
            {
                Rules = new ValidationRules { DescriptionRequired = true, AuthorRequired = true, RequireEditableFormat = true, MinimumMegapixels = 1 }
            };
            var result = ValidationProfileValidator.Validate(photo, new PhotoMetadata { Title = "Título", Keywords = ["a", "b", "c", "d", "e"] }, profile);

            Assert.False(result.IsValid);
            Assert.Contains(result.Issues, issue => issue.Code == "description-required");
            Assert.Contains(result.Issues, issue => issue.Code == "author-required");
            Assert.Contains(result.Issues, issue => issue.Code == "read-only-format");
            Assert.Contains(result.Issues, issue => issue.Code == "dimensions");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PreparationStatus_UsesDocumentedPrecedence()
    {
        var photo = CreatePhoto("C:\\photo.jpg");
        var valid = new MetadataValidationResult(true, true, []);
        var incomplete = new MetadataValidationResult(true, false, [new("x", "pendência")]);
        var now = DateTime.UtcNow;
        var uploads = new[]
        {
            new UploadRecordSnapshot(photo.Id, 1, "Adobe", true, UploadStatusSnapshot.Uploaded, 0, now),
            new UploadRecordSnapshot(photo.Id, 2, "Shutterstock", true, UploadStatusSnapshot.Pending, 0, now)
        };

        Assert.Equal(MicrostockPreparationStatus.ReadyForSubmission, PreparationStatusCalculator.Calculate(photo, valid, []));
        Assert.Equal(MicrostockPreparationStatus.MetadataIncomplete, PreparationStatusCalculator.Calculate(photo, incomplete, []));
        Assert.Equal(MicrostockPreparationStatus.PartiallyUploaded, PreparationStatusCalculator.Calculate(photo, valid, uploads));
        Assert.Equal(MicrostockPreparationStatus.UploadedToAll, PreparationStatusCalculator.Calculate(photo, valid, uploads.Select(upload => upload with { Status = UploadStatusSnapshot.Uploaded }).ToArray()));
        var changedPhoto = CreatePhoto("C:\\photo.jpg");
        changedPhoto.MetadataVersion = 1;
        Assert.Equal(MicrostockPreparationStatus.ChangedAfterUpload, PreparationStatusCalculator.Calculate(changedPhoto, valid, uploads));
        Assert.Equal(MicrostockPreparationStatus.Error, PreparationStatusCalculator.Calculate(changedPhoto, valid, [uploads[0] with { Status = UploadStatusSnapshot.Rejected }]));
    }

    private static Photo CreatePhoto(string path, int width = 1000, int height = 1000) => new()
    {
        Id = 7, CurrentPath = path, FileName = Path.GetFileName(path), Extension = Path.GetExtension(path), Width = width, Height = height,
        FileSize = 1, CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow, ImportedAt = DateTime.UtcNow
    };
}

public sealed class MicrostockPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoManagerTests", Guid.NewGuid().ToString("N"));
    private readonly SqliteCatalogRepository _repository;

    public MicrostockPersistenceTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteCatalogRepository(Path.Combine(_root, "data", "photomanager.db"));
    }

    [Fact]
    public async Task ValidationProfiles_MigrateOldDatabase_Idempotently_AndDoNotLosePhotos()
    {
        var databasePath = Path.Combine(_root, "data", "photomanager.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Photos (Id INTEGER PRIMARY KEY AUTOINCREMENT, FileName TEXT NOT NULL, CurrentPath TEXT NOT NULL UNIQUE, Extension TEXT NOT NULL, FileSize INTEGER NOT NULL, Width INTEGER NULL, Height INTEGER NULL, CreatedAt TEXT NOT NULL, ModifiedAt TEXT NOT NULL, DateTaken TEXT NULL, ImportedAt TEXT NOT NULL, ContentHash TEXT NULL, IsMissing INTEGER NOT NULL DEFAULT 0); INSERT INTO Photos (FileName, CurrentPath, Extension, FileSize, CreatedAt, ModifiedAt, ImportedAt) VALUES ('old.jpg', 'C:/old.jpg', '.jpg', 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');";
            await command.ExecuteNonQueryAsync();
        }

        await _repository.InitializeAsync();
        await _repository.InitializeAsync();
        var profiles = (IValidationProfileRepository)_repository;
        var all = await profiles.GetAllAsync();
        var photos = await _repository.GetAllAsync();

        Assert.Single(all);
        Assert.Equal("Padrão genérico", all[0].Name);
        Assert.Single(photos);
        Assert.Equal("old.jpg", photos[0].FileName);
        Assert.Equal(0, photos[0].MetadataVersion);
    }

    [Fact]
    public async Task ValidationProfiles_CanCreateActivateDuplicateAndRejectLastDelete()
    {
        await _repository.InitializeAsync();
        var profiles = (IValidationProfileRepository)_repository;
        var created = await profiles.SaveAsync(new ValidationProfile(0, "Editorial", new ValidationRules { KeywordsMinCount = 1 }));
        await profiles.SetActiveAsync(created.Id);
        var active = (await profiles.GetAllAsync()).Single(profile => profile.IsActive);
        Assert.Equal(created.Id, active.Id);

        await profiles.DeleteAsync(ValidationProfile.GenericDefault.Id == 0 ? (await profiles.GetAllAsync()).Single(profile => profile.Name == "Padrão genérico").Id : ValidationProfile.GenericDefault.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => profiles.DeleteAsync(created.Id));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}

public sealed class MicrostockEvaluationTests
{
    [Fact]
    public async Task Evaluation_Handles2000Photos_ReportsProgress_AndSupportsCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = Enumerable.Range(0, 2000).Select(index => Path.Combine(root, $"photo-{index}.jpg")).ToList();
            foreach (var path in paths) File.WriteAllBytes(path, [1]);
            var photos = paths.Select((path, index) => new Photo { Id = index + 1, FileName = Path.GetFileName(path), CurrentPath = path, Extension = ".jpg", FileSize = 1, CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow, ImportedAt = DateTime.UtcNow }).ToList();
            var reader = new FakeMetadataReader();
            var service = new MicrostockEvaluationService(reader);
            var progressValues = new List<int>();
            var results = await service.EvaluateAsync(photos, ValidationProfile.GenericDefault, progress: new Progress<int>(progressValues.Add));

            Assert.Equal(2000, results.Count);
            Assert.Equal(2000, progressValues.Last());
            Assert.Equal(2000, reader.Reads);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EvaluateAsync(photos, ValidationProfile.GenericDefault, cancellationToken: cancellation.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FakeMetadataReader : IMetadataReader
    {
        public int Reads;
        public Task<PhotoMetadata> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(new PhotoMetadata { Title = "Título", Keywords = ["a", "b", "c", "d", "e"] });
        }
    }
}
