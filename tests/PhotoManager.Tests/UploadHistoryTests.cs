using Microsoft.Data.Sqlite;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Microstock;
using PhotoManager.Domain.Photos;
using PhotoManager.Persistence;

namespace PhotoManager.Tests;

public sealed class UploadHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoManagerTests", Guid.NewGuid().ToString("N"));
    private readonly SqliteCatalogRepository _repository;
    private readonly IUploadHistoryRepository _historyRepository;
    private readonly UploadHistoryService _service;

    public UploadHistoryTests()
    {
        Directory.CreateDirectory(_root);
        _repository = new SqliteCatalogRepository(Path.Combine(_root, "data", "photomanager.db"));
        _repository.InitializeAsync().GetAwaiter().GetResult();
        _historyRepository = _repository;
        _service = new UploadHistoryService(_historyRepository, _repository);
    }

    [Fact]
    public async Task Agencies_AreSeeded_IdempotentAndCanBeRenamedDeactivatedAndReordered()
    {
        await _repository.InitializeAsync();
        var agencies = await _historyRepository.GetAgenciesAsync();
        Assert.Equal(5, agencies.Count);
        var adobe = agencies.Single(agency => agency.Name == "Adobe Stock");

        var renamed = await _historyRepository.SaveAgencyAsync(adobe with { Name = "Adobe Stock BR" });
        await _historyRepository.SetAgencyActiveAsync(renamed.Id, false);
        await _historyRepository.SetAgencyOrderAsync(agencies.OrderByDescending(agency => agency.Order).Select(agency => agency.Id).ToList());

        var persisted = await _historyRepository.GetAgenciesAsync();
        Assert.Equal("Adobe Stock BR", persisted.Single(agency => agency.Id == adobe.Id).Name);
        Assert.False(persisted.Single(agency => agency.Id == adobe.Id).IsActive);
        Assert.Equal(5, persisted.Count);
        Assert.Equal(4, (await _historyRepository.GetAgenciesAsync(true)).Count);
    }

    [Fact]
    public async Task Marking_IsAppendOnly_CurrentStateUsesLatest_AndUndoIsReversible()
    {
        var photo = await AddPhotoAsync();
        var agency = (await _historyRepository.GetAgenciesAsync(true)).First();
        var first = await _service.MarkUploadedAsync([photo.Id], [agency.Id], DateTime.UtcNow, "remote.jpg", "primeiro");
        var issue = await _service.MarkIssueAsync([photo.Id], [agency.Id], UploadRecordStatus.Error, "Falhou", DateTime.UtcNow.AddMinutes(1), null);

        var currentError = await _service.GetCurrentSnapshotsAsync([photo.Id]);
        Assert.Equal(2, first.RecordsCreated + issue.RecordsCreated);
        Assert.Equal(UploadStatusSnapshot.Error, Assert.Single(currentError).Status);
        Assert.Equal(2, (await _service.GetHistoryAsync(photo.Id)).Count);

        await _service.UndoMarkingAsync([photo.Id], [agency.Id], DateTime.UtcNow.AddMinutes(2), "corrigir");
        var currentPending = await _service.GetCurrentSnapshotsAsync([photo.Id]);
        Assert.Equal(UploadStatusSnapshot.Pending, Assert.Single(currentPending).Status);
        Assert.Equal(3, (await _service.GetHistoryAsync(photo.Id)).Count);
    }

    [Fact]
    public async Task Marking_CapturesMetadataVersion_AndActiveAgencyUniverseDrivesPreparationState()
    {
        var photo = await AddPhotoAsync();
        var agencies = await _historyRepository.GetAgenciesAsync(true);
        await _service.MarkUploadedAsync([photo.Id], [agencies[0].Id], DateTime.UtcNow, null, null);
        var snapshot = Assert.Single(await _service.GetCurrentSnapshotsAsync([photo.Id]));
        Assert.Equal(0, snapshot.MetadataVersion);

        var valid = new MetadataValidationResult(true, true, []);
        Assert.Equal(MicrostockPreparationStatus.PartiallyUploaded, PreparationStatusCalculator.Calculate(photo, valid, [snapshot], agencies.Select(agency => agency.Id).ToList()));
        foreach (var agency in agencies.Skip(1)) await _historyRepository.SetAgencyActiveAsync(agency.Id, false);
        var active = await _historyRepository.GetAgenciesAsync(true);
        Assert.Equal(MicrostockPreparationStatus.UploadedToAll, PreparationStatusCalculator.Calculate(photo, valid, [snapshot], active.Select(agency => agency.Id).ToList()));

        photo.MetadataVersion = 1;
        Assert.Equal(MicrostockPreparationStatus.ChangedAfterUpload, PreparationStatusCalculator.Calculate(photo, valid, [snapshot], active.Select(agency => agency.Id).ToList()));
    }

    [Fact]
    public async Task MarkIssue_RejectsUnsupportedStatus_AndMissingSelectionReturnsWarnings()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.MarkIssueAsync([], [], UploadRecordStatus.Uploaded, "não", DateTime.UtcNow, null));
        var missingPhoto = await _service.MarkUploadedAsync([999], [999], DateTime.UtcNow, null, null);
        Assert.Equal(0, missingPhoto.RecordsCreated);
        Assert.Single(missingPhoto.Warnings);
        var photo = await AddPhotoAsync();
        var missingAgency = await _service.MarkUploadedAsync([photo.Id], [999], DateTime.UtcNow, null, null);
        Assert.Equal(0, missingAgency.RecordsCreated);
        Assert.Single(missingAgency.Warnings);
    }

    [Fact]
    public async Task OldDatabase_MigratesUploadTablesWithoutLosingPhoto()
    {
        var path = Path.Combine(_root, "old.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Photos (Id INTEGER PRIMARY KEY AUTOINCREMENT, FileName TEXT NOT NULL, CurrentPath TEXT NOT NULL UNIQUE, Extension TEXT NOT NULL, FileSize INTEGER NOT NULL, Width INTEGER NULL, Height INTEGER NULL, CreatedAt TEXT NOT NULL, ModifiedAt TEXT NOT NULL, DateTaken TEXT NULL, ImportedAt TEXT NOT NULL, ContentHash TEXT NULL, IsMissing INTEGER NOT NULL DEFAULT 0); INSERT INTO Photos (FileName, CurrentPath, Extension, FileSize, CreatedAt, ModifiedAt, ImportedAt) VALUES ('legacy.jpg', 'C:/legacy.jpg', '.jpg', 1, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');";
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteCatalogRepository(path);
        await repository.InitializeAsync();
        await repository.InitializeAsync();
        Assert.Equal("legacy.jpg", Assert.Single(await repository.GetAllAsync()).FileName);
        Assert.Equal(5, (await ((IUploadHistoryRepository)repository).GetAgenciesAsync()).Count);
    }

    private async Task<Photo> AddPhotoAsync()
    {
        var photo = new Photo { FileName = "photo.jpg", CurrentPath = Path.Combine(_root, Guid.NewGuid() + ".jpg"), Extension = ".jpg", FileSize = 1, Width = 1000, Height = 1000, CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow, ImportedAt = DateTime.UtcNow };
        await _repository.AddAsync(photo);
        return photo;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
