using Microsoft.Data.Sqlite;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Collections;
using PhotoManager.Domain.Collections;

namespace PhotoManager.Tests;

public sealed class CollectionsEtapaC1Tests : IDisposable
{
    private readonly TestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task AddPhotosAsync_AssociatesPhotosWithoutDuplicating_AndReturnsCount()
    {
        var photo1Path = _env.CreatePng("p1.png");
        var photo2Path = _env.CreatePng("p2.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photos = await _env.Catalog.GetPhotosAsync();
        var p1 = photos.First(p => p.CurrentPath == photo1Path);
        var p2 = photos.First(p => p.CurrentPath == photo2Path);

        var col = await _env.Collections.CreateAsync("Viagens", null);

        var added = await _env.Collections.AddPhotosAsync(col.Id, [p1.Id, p2.Id]);
        Assert.Equal(2, added);

        // Idempotência: tentar adicionar novamente deve ignorar existentes e retornar 0
        var addedAgain = await _env.Collections.AddPhotosAsync(col.Id, [p1.Id, p2.Id]);
        Assert.Equal(0, addedAgain);

        var counts = await _env.Repository.GetDirectCountsAsync();
        Assert.Equal(2, counts[col.Id]);
    }

    [Fact]
    public async Task MovePhotosAsync_MovesOnlyFromSource_AndPreservesAtomicConsistency()
    {
        var photo1Path = _env.CreatePng("p1.png");
        var photo2Path = _env.CreatePng("p2.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photos = await _env.Catalog.GetPhotosAsync();
        var p1 = photos.First(p => p.CurrentPath == photo1Path);
        var p2 = photos.First(p => p.CurrentPath == photo2Path);

        var colOrigem = await _env.Collections.CreateAsync("Origem", null);
        var colDestino = await _env.Collections.CreateAsync("Destino", null);
        var colOutra = await _env.Collections.CreateAsync("Outra", null);

        // p1 pertence a Origem e Outra; p2 pertence só a Origem
        await _env.Collections.AddPhotosAsync(colOrigem.Id, [p1.Id, p2.Id]);
        await _env.Collections.AddPhotosAsync(colOutra.Id, [p1.Id]);

        // Mover p1 e p2 de Origem para Destino
        var result = await _env.Collections.MovePhotosAsync(colOrigem.Id, colDestino.Id, [p1.Id, p2.Id]);
        Assert.Equal(2, result.MovedCount);

        var reloadP1 = (await _env.Catalog.GetPhotosAsync()).First(p => p.Id == p1.Id);
        var reloadP2 = (await _env.Catalog.GetPhotosAsync()).First(p => p.Id == p2.Id);

        // Devem ter saído de Origem
        Assert.DoesNotContain(colOrigem.Id, reloadP1.CollectionIds);
        Assert.DoesNotContain(colOrigem.Id, reloadP2.CollectionIds);

        // Devem estar em Destino
        Assert.Contains(colDestino.Id, reloadP1.CollectionIds);
        Assert.Contains(colDestino.Id, reloadP2.CollectionIds);

        // p1 ainda pertence a Outra
        Assert.Contains(colOutra.Id, reloadP1.CollectionIds);

        // MovePhotosAsync com from == to é no-op sem erro
        var noop = await _env.Collections.MovePhotosAsync(colDestino.Id, colDestino.Id, [p1.Id]);
        Assert.Equal(0, noop.MovedCount);
    }

    [Fact]
    public async Task Subcollections_CanBeCreated_AndMovedBetweenParents()
    {
        var brasil = await _env.Collections.CreateAsync("Brasil", null);
        var sp = await _env.Collections.CreateAsync("São Paulo", brasil.Id);
        var rj = await _env.Collections.CreateAsync("Rio de Janeiro", brasil.Id);

        Assert.Equal(brasil.Id, sp.ParentCollectionId);
        Assert.Equal(brasil.Id, rj.ParentCollectionId);

        var europa = await _env.Collections.CreateAsync("Europa", null);

        // Mover RJ para Europa
        var moveResult = await _env.Collections.MoveAsync(rj.Id, europa.Id);
        Assert.True(moveResult.Success);

        var reloadedRj = (await _env.Collections.GetAllAsync()).First(c => c.Id == rj.Id);
        Assert.Equal(europa.Id, reloadedRj.ParentCollectionId);

        // Mover SP para a raiz
        var moveToRootResult = await _env.Collections.MoveAsync(sp.Id, null);
        Assert.True(moveToRootResult.Success);

        var reloadedSp = (await _env.Collections.GetAllAsync()).First(c => c.Id == sp.Id);
        Assert.Null(reloadedSp.ParentCollectionId);
    }

    [Fact]
    public async Task CyclePrevention_BlocksAllCycleVariations()
    {
        var a = await _env.Collections.CreateAsync("A", null);
        var b = await _env.Collections.CreateAsync("B", a.Id);
        var c = await _env.Collections.CreateAsync("C", b.Id);
        var d = await _env.Collections.CreateAsync("D", c.Id);

        // 1. A -> A (mover para si mesma)
        var selfMove = await _env.Collections.MoveAsync(a.Id, a.Id);
        Assert.False(selfMove.Success);
        Assert.Contains("si mesma", selfMove.Message);

        // 2. A -> B (mover pai para filho direto)
        var parentToChild = await _env.Collections.MoveAsync(a.Id, b.Id);
        Assert.False(parentToChild.Success);
        Assert.Contains("ciclo", parentToChild.Message, StringComparison.OrdinalIgnoreCase);

        // 3. A -> D (mover ancestral para descendente indireto)
        var ancestorToDescendant = await _env.Collections.MoveAsync(a.Id, d.Id);
        Assert.False(ancestorToDescendant.Success);
        Assert.Contains("ciclo", ancestorToDescendant.Message, StringComparison.OrdinalIgnoreCase);

        // 4. B -> D (mover intermediário para folha)
        var bToD = await _env.Collections.MoveAsync(b.Id, d.Id);
        Assert.False(bToD.Success);

        // Verificar que a árvore original continua íntegra
        var reloadedA = (await _env.Collections.GetAllAsync()).First(x => x.Id == a.Id);
        var reloadedB = (await _env.Collections.GetAllAsync()).First(x => x.Id == b.Id);
        Assert.Null(reloadedA.ParentCollectionId);
        Assert.Equal(a.Id, reloadedB.ParentCollectionId);
    }

    [Fact]
    public async Task SiblingUniqueness_BlocksDuplicatesAmongSiblings_AllowsInDifferentParents()
    {
        var brasil = await _env.Collections.CreateAsync("Brasil", null);
        var praiaBrasil = await _env.Collections.CreateAsync("Praias", brasil.Id);

        // Duplicado entre irmãos deve falhar (case-insensitive)
        await Assert.ThrowsAsync<InvalidOperationException>(() => _env.Collections.CreateAsync("praias", brasil.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _env.Collections.CreateAsync(" PRAIAS ", brasil.Id));

        // Mesmo nome em pai diferente (ou raiz) DEVE ser permitido
        var portugal = await _env.Collections.CreateAsync("Portugal", null);
        var praiaPortugal = await _env.Collections.CreateAsync("Praias", portugal.Id);
        Assert.Equal("Praias", praiaPortugal.Name);
        Assert.NotEqual(praiaBrasil.Id, praiaPortugal.Id);

        // Tentar mover praiasPortugal para Brasil deve falhar por colisão de irmãos
        var moveConflict = await _env.Collections.MoveAsync(praiaPortugal.Id, brasil.Id);
        Assert.False(moveConflict.Success);
        Assert.Contains("Já existe", moveConflict.Message);
    }

    [Fact]
    public async Task MaxDepth_EnforcesLimitOfEightLevels()
    {
        // Criar cadeia de 8 níveis: N1 -> N2 -> ... -> N8
        long? currentParent = null;
        for (var i = 1; i <= 8; i++)
        {
            var node = await _env.Collections.CreateAsync($"Nivel_{i}", currentParent);
            currentParent = node.Id;
        }

        // Tentar criar nível 9 deve falhar
        await Assert.ThrowsAsync<InvalidOperationException>(() => _env.Collections.CreateAsync("Nivel_9", currentParent));

        // Subárvore de 3 níveis em outra raiz
        var r1 = await _env.Collections.CreateAsync("R1", null);
        var r2 = await _env.Collections.CreateAsync("R2", r1.Id);
        var r3 = await _env.Collections.CreateAsync("R3", r2.Id);

        // Mover R1 para N6: R1 vira N7, R2 vira N8, R3 viraria N9 (excede 8) -> deve falhar
        var n6 = (await _env.Collections.GetAllAsync()).First(c => c.Name == "Nivel_6");
        var moveResult = await _env.Collections.MoveAsync(r1.Id, n6.Id);
        Assert.False(moveResult.Success);
        Assert.Contains("profundidade", moveResult.Message, StringComparison.OrdinalIgnoreCase);

        // Mover R1 para N5: R1 vira N6, R2 vira N7, R3 vira N8 -> deve passar (<= 8)
        var n5 = (await _env.Collections.GetAllAsync()).First(c => c.Name == "Nivel_5");
        var validMove = await _env.Collections.MoveAsync(r1.Id, n5.Id);
        Assert.True(validMove.Success);
    }

    [Fact]
    public async Task DeleteAsync_PromoteChildren_MovesChildrenUpAndResolvesNameCollisions()
    {
        var pai = await _env.Collections.CreateAsync("Pai", null);
        var filhoA = await _env.Collections.CreateAsync("FilhoA", pai.Id);
        var filhoB = await _env.Collections.CreateAsync("FilhoB", pai.Id);

        // Já existe uma coleção com nome "FilhoA" na raiz (destino da promoção)
        var colExistenteRaiz = await _env.Collections.CreateAsync("FilhoA", null);

        var deleteResult = await _env.Collections.DeleteAsync(pai.Id, DeleteMode.PromoteChildren);
        Assert.Equal(1, deleteResult.DeletedCount);
        Assert.Equal(2, deleteResult.PromotedCount);

        // Deve ter renomeado o filho conflitante para "FilhoA (2)"
        Assert.Contains(deleteResult.RenamedChildren, r => r.OldName == "FilhoA" && r.NewName == "FilhoA (2)");

        var all = await _env.Collections.GetAllAsync();
        Assert.DoesNotContain(all, c => c.Id == pai.Id);

        var promotedA = all.First(c => c.Id == filhoA.Id);
        Assert.Null(promotedA.ParentCollectionId);
        Assert.Equal("FilhoA (2)", promotedA.Name);

        var promotedB = all.First(c => c.Id == filhoB.Id);
        Assert.Null(promotedB.ParentCollectionId);
        Assert.Equal("FilhoB", promotedB.Name);
    }

    [Fact]
    public async Task DeleteAsync_WithDescendants_RemovesEntireSubtree()
    {
        var a = await _env.Collections.CreateAsync("RootA", null);
        var b = await _env.Collections.CreateAsync("ChildB", a.Id);
        var c = await _env.Collections.CreateAsync("GrandChildC", b.Id);

        var deleteResult = await _env.Collections.DeleteAsync(a.Id, DeleteMode.WithDescendants);
        Assert.Equal(3, deleteResult.DeletedCount);

        var all = await _env.Collections.GetAllAsync();
        Assert.DoesNotContain(all, col => col.Id == a.Id || col.Id == b.Id || col.Id == c.Id);
    }

    [Fact]
    public async Task PhysicalFilesAndPhotoRecords_AreNeverDeletedWhenCollectionsAreDeleted()
    {
        var photoPath = _env.CreatePng("foto_segura.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photo = (await _env.Catalog.GetPhotosAsync()).First(p => p.CurrentPath == photoPath);

        var col = await _env.Collections.CreateAsync("ColecaoTemporaria", null);
        var subCol = await _env.Collections.CreateAsync("SubTemporaria", col.Id);

        await _env.Collections.AddPhotosAsync(col.Id, [photo.Id]);
        await _env.Collections.AddPhotosAsync(subCol.Id, [photo.Id]);

        // Excluir toda a hierarquia
        await _env.Collections.DeleteAsync(col.Id, DeleteMode.WithDescendants);

        // 1. O arquivo físico NÃO pode ter sido tocado
        Assert.True(File.Exists(photoPath));

        // 2. O registro em Photos NÃO pode ter sido excluído
        var reloadedPhotos = await _env.Catalog.GetPhotosAsync();
        var reloaded = Assert.Single(reloadedPhotos);
        Assert.Equal(photo.Id, reloaded.Id);
        Assert.Empty(reloaded.CollectionIds);
        Assert.Empty(reloaded.Collections);
    }

    [Fact]
    public async Task Counters_AreDirectOnly_AndAllCollectionsPlusNoCollectionMatchesTotal()
    {
        var p1Path = _env.CreatePng("p1.png");
        var p2Path = _env.CreatePng("p2.png");
        var p3Path = _env.CreatePng("p3.png");
        var p4Path = _env.CreatePng("p4.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photos = (await _env.Catalog.GetPhotosAsync()).OrderBy(p => p.FileName).ToList();
        var p1 = photos[0];
        var p2 = photos[1];
        var p3 = photos[2];
        var p4 = photos[3];

        var pai = await _env.Collections.CreateAsync("Pai", null);
        var filho = await _env.Collections.CreateAsync("Filho", pai.Id);

        // p1 está no Pai
        // p2 está no Filho
        // p3 está em Ambos (Pai e Filho)
        // p4 NÃO está em nenhuma coleção
        await _env.Collections.AddPhotosAsync(pai.Id, [p1.Id, p3.Id]);
        await _env.Collections.AddPhotosAsync(filho.Id, [p2.Id, p3.Id]);

        var tree = await _env.Collections.GetTreeAsync();
        var paiNode = Assert.Single(tree.Roots);
        Assert.Equal("Pai", paiNode.Name);
        var filhoNode = Assert.Single(paiNode.Children);
        Assert.Equal("Filho", filhoNode.Name);

        // Contagens DIRETAS (Pai tem p1 e p3 = 2; não soma descendentes)
        Assert.Equal(2, paiNode.DirectCount);
        // Filho tem p2 e p3 = 2
        Assert.Equal(2, filhoNode.DirectCount);

        // Fotos distintas em qualquer coleção: p1, p2, p3 = 3
        Assert.Equal(3, tree.TotalDistinctInAny);

        // Sem coleção: p4 = 1
        Assert.Equal(1, tree.WithoutCollection);

        // Invariante essencial
        Assert.Equal(4, tree.TotalDistinctInAny + tree.WithoutCollection);
    }

    [Fact]
    public async Task HomonymousCollections_InDifferentBranches_AreFormattedWithPathInPhotoCollections()
    {
        var p1Path = _env.CreatePng("homonimo.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photo = (await _env.Catalog.GetPhotosAsync()).First();

        var brasil = await _env.Collections.CreateAsync("Brasil", null);
        var praiaBR = await _env.Collections.CreateAsync("Praia", brasil.Id);

        var portugal = await _env.Collections.CreateAsync("Portugal", null);
        var praiaPT = await _env.Collections.CreateAsync("Praia", portugal.Id);

        // Foto associada a ambas as coleções homônimas
        await _env.Collections.AddPhotosAsync(praiaBR.Id, [photo.Id]);
        await _env.Collections.AddPhotosAsync(praiaPT.Id, [photo.Id]);

        var reloaded = (await _env.Catalog.GetPhotosAsync()).First(p => p.Id == photo.Id);
        Assert.Equal(2, reloaded.CollectionIds.Count);

        // O nome derivado deve conter desambiguação "Pai / Filho" quando há homônimos
        Assert.Contains("Brasil / Praia", reloaded.Collections);
        Assert.Contains("Portugal / Praia", reloaded.Collections);
    }

    [Fact]
    public async Task SaveAsync_OnOrganizationRepository_DoesNotTouchPhotoCollections()
    {
        var photoPath = _env.CreatePng("org_test.png");
        await _env.Catalog.ImportFolderAsync(_env.Photos);
        var photo = (await _env.Catalog.GetPhotosAsync()).First();

        var col = await _env.Collections.CreateAsync("ColecaoFixa", null);
        await _env.Collections.AddPhotosAsync(col.Id, [photo.Id]);

        // Salvar metadados de organização (categoria, nota, tags, favorito)
        photo.CategoryName = "Natureza";
        photo.PersonalNote = "Nota de teste";
        photo.Rating = 5;
        photo.IsFavorite = true;
        photo.Tags = ["tag1", "tag2"];
        await _env.Organization.SaveAsync(photo);

        // As coleções devem permanecer 100% preservadas
        var reloaded = (await _env.Catalog.GetPhotosAsync()).First(p => p.Id == photo.Id);
        Assert.Equal("Natureza", reloaded.CategoryName);
        Assert.Equal(5, reloaded.Rating);
        Assert.True(reloaded.IsFavorite);
        Assert.Contains(col.Id, reloaded.CollectionIds);
        Assert.Contains("ColecaoFixa", reloaded.Collections);
    }

    [Fact]
    public async Task Migration_FromLegacyFlatDatabase_PreservesIdsRelationsAndCreatesBakFile()
    {
        var legacyDbPath = Path.Combine(_env.Root, "legacy_test", "photomanager.db");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyDbPath)!);

        // Criar banco no schema antigo (flat: Name TEXT NOT NULL UNIQUE, sem ParentCollectionId)
        var connStr = new SqliteConnectionStringBuilder { DataSource = legacyDbPath, ForeignKeys = true }.ToString();
        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Photos (Id INTEGER PRIMARY KEY AUTOINCREMENT, FileName TEXT NOT NULL, CurrentPath TEXT NOT NULL UNIQUE, Extension TEXT NOT NULL, FileSize INTEGER NOT NULL, CreatedAt TEXT NOT NULL, ModifiedAt TEXT NOT NULL, ImportedAt TEXT NOT NULL, IsMissing INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE Collections (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE);
                CREATE TABLE PhotoCollections (PhotoId INTEGER NOT NULL, CollectionId INTEGER NOT NULL, PRIMARY KEY(PhotoId, CollectionId), FOREIGN KEY(PhotoId) REFERENCES Photos(Id) ON DELETE CASCADE, FOREIGN KEY(CollectionId) REFERENCES Collections(Id) ON DELETE CASCADE);

                INSERT INTO Photos (Id, FileName, CurrentPath, Extension, FileSize, CreatedAt, ModifiedAt, ImportedAt)
                VALUES (1, 'foto1.jpg', 'C:/fotos/foto1.jpg', '.jpg', 1000, '2026-01-01', '2026-01-01', '2026-01-01');
                INSERT INTO Photos (Id, FileName, CurrentPath, Extension, FileSize, CreatedAt, ModifiedAt, ImportedAt)
                VALUES (2, 'foto2.jpg', 'C:/fotos/foto2.jpg', '.jpg', 2000, '2026-01-01', '2026-01-01', '2026-01-01');

                INSERT INTO Collections (Id, Name) VALUES (10, 'Viagem');
                INSERT INTO Collections (Id, Name) VALUES (20, 'Familia');

                INSERT INTO PhotoCollections (PhotoId, CollectionId) VALUES (1, 10);
                INSERT INTO PhotoCollections (PhotoId, CollectionId) VALUES (1, 20);
                INSERT INTO PhotoCollections (PhotoId, CollectionId) VALUES (2, 10);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var bakPath = Path.Combine(Path.GetDirectoryName(legacyDbPath)!, "photomanager.db.pre-colecoes.bak");
        Assert.False(File.Exists(bakPath));

        // Inicializar repositório no banco legado: deve disparar a migração D3
        var repo = new PhotoManager.Persistence.SqliteCatalogRepository(legacyDbPath);
        await repo.InitializeAsync();

        // 1. Arquivo de backup DEVE ter sido criado
        Assert.True(File.Exists(bakPath));

        // 2. Os dados e relações devem estar preservados
        var collections = await repo.GetAllCollectionsAsync();
        Assert.Equal(2, collections.Count);

        var col10 = collections.First(c => c.Id == 10);
        Assert.Equal("Viagem", col10.Name);
        Assert.Null(col10.ParentCollectionId);

        var col20 = collections.First(c => c.Id == 20);
        Assert.Equal("Familia", col20.Name);
        Assert.Null(col20.ParentCollectionId);

        var counts = await repo.GetDirectCountsAsync();
        Assert.Equal(2, counts[10]);
        Assert.Equal(1, counts[20]);

        // 3. Execuções repetidas de InitializeAsync devem ser idempotentes e não recriar/sobrescrever o .bak
        var bakWriteTime = File.GetLastWriteTimeUtc(bakPath);
        await repo.InitializeAsync();
        var bakWriteTimeAfter = File.GetLastWriteTimeUtc(bakPath);
        Assert.Equal(bakWriteTime, bakWriteTimeAfter);
    }
}
