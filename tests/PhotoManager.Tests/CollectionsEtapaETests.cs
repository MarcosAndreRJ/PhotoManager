using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PhotoManager.Application.Collections;
using PhotoManager.Domain.Collections;
using PhotoManager.Domain.Photos;
using PhotoManager.Wpf.Views;

namespace PhotoManager.Tests;

/// <summary>
/// Testes abrangentes da Etapa E e fechamento da frente de coleções:
/// 1. CollectionDropPlanner (função pura de decisão: reparentar, raiz, si mesma, ciclos diretos e indiretos, profundidade máxima 8, irmãos homônimos, nós virtuais, no-ops).
/// 2. Execução no LibraryViewModel via ICollectionService (banco real, preservação de fotos e filhos).
/// 3. Convivência de formatos de arraste (PhotoIds vs CollectionId) no mesmo nó de árvore.
/// 4. Teste de integridade do catálogo: 0 ciclos, 0 órfãos em PhotoCollections, integridade de Photos e arquivos.
/// 5. Teste de desempenho em volume sintético: 5.000 fotos x 200 coleções em 4 níveis.
/// </summary>
[Collection("WpfUi")]
public sealed class CollectionsEtapaETests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); } catch (Exception ex) { failure = ex; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException(failure.ToString(), failure);
    }

    [Fact]
    public void CollectionDropPlanner_MoveToAnotherCollection_Allowed()
    {
        var tree = new Dictionary<long, (string Name, long? ParentId)>
        {
            [1] = ("Viagens", null),
            [2] = ("Roraima 2026", null)
        };

        var request = new CollectionDropPlanRequest(
            DraggedCollectionId: 2,
            DraggedCollectionName: "Roraima 2026",
            TargetIsRoot: false,
            TargetCollectionId: 1,
            TargetCollectionName: "Viagens",
            TargetIsVirtual: false,
            TreeLookup: tree);

        var plan = CollectionDropPlanner.Plan(request);

        Assert.True(plan.CanDrop);
        Assert.Equal(CollectionDropAction.Move, plan.Action);
        Assert.Equal(1, plan.TargetParentId);
        Assert.Equal("Viagens", plan.TargetParentName);
        Assert.Contains("Mover", plan.Message);
        Assert.Contains("Roraima 2026", plan.Message);
        Assert.Contains("Viagens", plan.Message);
    }

    [Fact]
    public void CollectionDropPlanner_MoveSubcollectionToRoot_Allowed()
    {
        var tree = new Dictionary<long, (string Name, long? ParentId)>
        {
            [1] = ("Viagens", null),
            [2] = ("Roraima 2026", 1)
        };

        var request = new CollectionDropPlanRequest(
            DraggedCollectionId: 2,
            DraggedCollectionName: "Roraima 2026",
            TargetIsRoot: true,
            TargetCollectionId: null,
            TargetCollectionName: null,
            TargetIsVirtual: false,
            TreeLookup: tree);

        var plan = CollectionDropPlanner.Plan(request);

        Assert.True(plan.CanDrop);
        Assert.Equal(CollectionDropAction.Move, plan.Action);
        Assert.Null(plan.TargetParentId);
        Assert.Equal("Raiz", plan.TargetParentName);
        Assert.Contains("raiz", plan.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectionDropPlanner_MoveToSameParent_IsNoOp()
    {
        var tree = new Dictionary<long, (string Name, long? ParentId)>
        {
            [1] = ("Viagens", null),
            [2] = ("Roraima 2026", 1)
        };

        var request = new CollectionDropPlanRequest(
            DraggedCollectionId: 2,
            DraggedCollectionName: "Roraima 2026",
            TargetIsRoot: false,
            TargetCollectionId: 1,
            TargetCollectionName: "Viagens",
            TargetIsVirtual: false,
            TreeLookup: tree);

        var plan = CollectionDropPlanner.Plan(request);

        Assert.Equal(CollectionDropAction.NoOp, plan.Action);
        Assert.Contains("já está", plan.Message);
    }

    [Fact]
    public void CollectionDropPlanner_MoveRootToRoot_IsNoOp()
    {
        var tree = new Dictionary<long, (string Name, long? ParentId)>
        {
            [1] = ("Viagens", null)
        };

        var request = new CollectionDropPlanRequest(
            DraggedCollectionId: 1,
            DraggedCollectionName: "Viagens",
            TargetIsRoot: true,
            TargetCollectionId: null,
            TargetCollectionName: null,
            TargetIsVirtual: false,
            TreeLookup: tree);

        var plan = CollectionDropPlanner.Plan(request);

        Assert.Equal(CollectionDropAction.NoOp, plan.Action);
        Assert.Contains("já está", plan.Message);
    }

    [Fact]
    public void CollectionDropPlanner_MoveIntoSelf_Blocked()
    {
        var tree = new Dictionary<long, (string Name, long? ParentId)>
        {
            [1] = ("Viagens", null)
        };

        var request = new CollectionDropPlanRequest(
            DraggedCollectionId: 1,
            DraggedCollectionName: "Viagens",
            TargetIsRoot: false,
            TargetCollectionId: 1,
            TargetCollectionName: "Viagens",
            TargetIsVirtual: false,
            TreeLookup: tree);

        var plan = CollectionDropPlanner.Plan(request);

        Assert.False(plan.CanDrop);
        Assert.Equal(CollectionDropAction.Blocked, plan.Action);
        Assert.Contains("dentro de si mesma", plan.Message);
    }

    [Fact]
    public void CollectionDropPlanner_MoveIntoDirectChild_CreatesCycle_Blocked()
    {
        var tree = new Dictionary<long, (string Name, long? ParentId)>
        {
            [1] = ("Viagens", null),
            [2] = ("Brasil", 1)
        };

        var request = new CollectionDropPlanRequest(
            DraggedCollectionId: 1,
            DraggedCollectionName: "Viagens",
            TargetIsRoot: false,
            TargetCollectionId: 2,
            TargetCollectionName: "Brasil",
            TargetIsVirtual: false,
            TreeLookup: tree);

        var plan = CollectionDropPlanner.Plan(request);

        Assert.False(plan.CanDrop);
        Assert.Equal(CollectionDropAction.Blocked, plan.Action);
        Assert.Contains("ciclo", plan.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectionDropPlanner_MoveIntoDeepDescendant_CreatesCycle_Blocked()
    {
        // 1 -> 2 -> 3 -> 4. Tentar mover 1 para 4.
        var tree = new Dictionary<long, (string Name, long? ParentId)>
        {
            [1] = ("Mundo", null),
            [2] = ("América do Sul", 1),
            [3] = ("Brasil", 2),
            [4] = ("Roraima", 3)
        };

        var request = new CollectionDropPlanRequest(
            DraggedCollectionId: 1,
            DraggedCollectionName: "Mundo",
            TargetIsRoot: false,
            TargetCollectionId: 4,
            TargetCollectionName: "Roraima",
            TargetIsVirtual: false,
            TreeLookup: tree);

        var plan = CollectionDropPlanner.Plan(request);

        Assert.False(plan.CanDrop);
        Assert.Equal(CollectionDropAction.Blocked, plan.Action);
        Assert.Contains("ciclo", plan.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CollectionDropPlanner_MoveToVirtualItem_Blocked()
    {
        var tree = new Dictionary<long, (string Name, long? ParentId)>
        {
            [1] = ("Viagens", null)
        };

        var request = new CollectionDropPlanRequest(
            DraggedCollectionId: 1,
            DraggedCollectionName: "Viagens",
            TargetIsRoot: false,
            TargetCollectionId: 999,
            TargetCollectionName: "Todas",
            TargetIsVirtual: true,
            TreeLookup: tree);

        var plan = CollectionDropPlanner.Plan(request);

        Assert.False(plan.CanDrop);
        Assert.Equal(CollectionDropAction.Blocked, plan.Action);
        Assert.Contains("itens virtuais", plan.Message);
    }

    [Fact]
    public void CollectionDropPlanner_SiblingHomonymInDestination_Blocked()
    {
        // Destino 'Viagens' já tem uma filha chamada 'Fotos'.
        // Coleção arrastada 'Fotos' está em outro lugar e tenta entrar em 'Viagens'.
        var tree = new Dictionary<long, (string Name, long? ParentId)>
        {
            [1] = ("Viagens", null),
            [2] = ("Fotos", 1),
            [3] = ("Trabalho", null),
            [4] = ("Fotos", 3)
        };

        var request = new CollectionDropPlanRequest(
            DraggedCollectionId: 4,
            DraggedCollectionName: "Fotos",
            TargetIsRoot: false,
            TargetCollectionId: 1,
            TargetCollectionName: "Viagens",
            TargetIsVirtual: false,
            TreeLookup: tree);

        var plan = CollectionDropPlanner.Plan(request);

        Assert.False(plan.CanDrop);
        Assert.Equal(CollectionDropAction.Blocked, plan.Action);
        Assert.Contains("Já existe uma coleção chamada “Fotos”", plan.Message);
    }

    [Fact]
    public void CollectionDropPlanner_SiblingHomonymInRoot_Blocked()
    {
        // Na raiz já existe 'Trabalho'.
        // Subcoleção 2 ('Trabalho') tenta subir para a raiz.
        var tree = new Dictionary<long, (string Name, long? ParentId)>
        {
            [1] = ("Viagens", null),
            [2] = ("Trabalho", 1),
            [3] = ("Trabalho", null)
        };

        var request = new CollectionDropPlanRequest(
            DraggedCollectionId: 2,
            DraggedCollectionName: "Trabalho",
            TargetIsRoot: true,
            TargetCollectionId: null,
            TargetCollectionName: null,
            TargetIsVirtual: false,
            TreeLookup: tree);

        var plan = CollectionDropPlanner.Plan(request);

        Assert.False(plan.CanDrop);
        Assert.Equal(CollectionDropAction.Blocked, plan.Action);
        Assert.Contains("Já existe uma coleção chamada “Trabalho” na raiz", plan.Message);
    }

    [Fact]
    public void CollectionDropPlanner_ExceedsMaxDepth8_Blocked()
    {
        // Caminho de destino com profundidade 7 (níveis 1 a 7).
        // Subárvore arrastada tem altura 2 (nó pai + filho).
        // 7 + 2 = 9 > 8 => Bloqueado.
        var tree = new Dictionary<long, (string Name, long? ParentId)>
        {
            [1] = ("N1", null),
            [2] = ("N2", 1),
            [3] = ("N3", 2),
            [4] = ("N4", 3),
            [5] = ("N5", 4),
            [6] = ("N6", 5),
            [7] = ("N7", 6),
            [10] = ("SubA", null),
            [11] = ("SubB", 10)
        };

        var request = new CollectionDropPlanRequest(
            DraggedCollectionId: 10,
            DraggedCollectionName: "SubA",
            TargetIsRoot: false,
            TargetCollectionId: 7,
            TargetCollectionName: "N7",
            TargetIsVirtual: false,
            TreeLookup: tree);

        var plan = CollectionDropPlanner.Plan(request);

        Assert.False(plan.CanDrop);
        Assert.Equal(CollectionDropAction.Blocked, plan.Action);
        Assert.Contains("profundidade máxima", plan.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LibraryViewModel_ExecuteCollectionDropPlan_MovesSubcollectionAndPreservesPhotos()
    {
        var path = _env.CreatePng("test_drop.png");
        var rootA = await _env.Collections.CreateAsync("Origem", null);
        var rootB = await _env.Collections.CreateAsync("Destino", null);
        var child = await _env.Collections.CreateAsync("Filha", rootA.Id);

        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);

        var photo = vm.Photos.First(p => p.FileName == "test_drop.png").Photo;
        await _env.Collections.AddPhotosAsync(child.Id, [photo.Id]);
        await vm.ImportFolderAsync(_env.Photos);

        // Planeja mover 'Filha' de 'Origem' para 'Destino'
        var plan = vm.PlanCollectionDrop(
            draggedCollectionId: child.Id,
            targetCollectionId: rootB.Id,
            targetCollectionName: rootB.Name,
            targetIsRoot: false,
            targetIsVirtual: false);

        Assert.True(plan.CanDrop);
        Assert.Equal(CollectionDropAction.Move, plan.Action);

        var executed = await vm.ExecuteCollectionDropPlanAsync(plan);
        Assert.True(executed);

        // Verifica no banco de dados
        var updatedChild = await _env.Collections.GetByIdAsync(child.Id);
        Assert.NotNull(updatedChild);
        Assert.Equal(rootB.Id, updatedChild.ParentCollectionId);

        // A foto ainda está na coleção Filha (fotos continuam intactas)
        var photosInChild = await ((ICollectionRepository)_env.Repository).GetPhotoIdsInCollectionAsync(child.Id);
        Assert.Contains(photo.Id, photosInChild);

        // Verifica a mensagem de status da ViewModel
        Assert.Contains("Filha", vm.StatusText);
        Assert.Contains("Destino", vm.StatusText);

        // Arquivo físico no disco intacto
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task LibraryViewModel_ExecuteCollectionDropPlan_MoveToRoot()
    {
        var rootA = await _env.Collections.CreateAsync("Pai", null);
        var child = await _env.Collections.CreateAsync("Filho", rootA.Id);

        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);

        // Planeja mover 'Filho' para a raiz
        var plan = vm.PlanCollectionDrop(
            draggedCollectionId: child.Id,
            targetCollectionId: null,
            targetCollectionName: null,
            targetIsRoot: true,
            targetIsVirtual: false);

        Assert.True(plan.CanDrop);
        Assert.Equal(CollectionDropAction.Move, plan.Action);
        Assert.Null(plan.TargetParentId);

        var executed = await vm.ExecuteCollectionDropPlanAsync(plan);
        Assert.True(executed);

        var updated = await _env.Collections.GetByIdAsync(child.Id);
        Assert.NotNull(updated);
        Assert.Null(updated.ParentCollectionId);
        Assert.Contains("raiz", vm.StatusText);
    }

    [Fact]
    public async Task LibraryViewModel_ExecuteCollectionDropPlan_ServiceCycleDefense_HandlesErrorGracefully()
    {
        var colA = await _env.Collections.CreateAsync("A", null);
        var colB = await _env.Collections.CreateAsync("B", colA.Id);

        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);

        // Simula um plano malicioso forçado (A para B) que teria escapado do planejador de UI
        var forcedPlan = new CollectionDropPlan(
            Action: CollectionDropAction.Move,
            DraggedCollectionId: colA.Id,
            DraggedCollectionName: "A",
            TargetParentId: colB.Id,
            TargetParentName: "B",
            Message: "Mover A para B",
            CanDrop: true);

        var success = await vm.ExecuteCollectionDropPlanAsync(forcedPlan);
        Assert.False(success);
        // O status da VM deve refletir o erro do serviço
        Assert.Contains("ciclo", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DragDrop_FormatsCoexistence_SameTreeNodeHandlesBothFormats()
    {
        // Garante que as constantes de formato são distintas
        Assert.NotEqual(CollectionDragDropFormats.PhotoIds, CollectionDragDropFormats.CollectionId);
        Assert.Equal("PhotoManager.PhotoIds", CollectionDragDropFormats.PhotoIds);
        Assert.Equal("PhotoManager.CollectionId", CollectionDragDropFormats.CollectionId);

        var tree = new Dictionary<long, (string Name, long? ParentId)>
        {
            [1] = ("Viagens", null),
            [2] = ("Trabalho", null)
        };

        // Cenário 1: Arrastar Coleção (CollectionId) sobre o nó Viagens
        var colRequest = new CollectionDropPlanRequest(
            DraggedCollectionId: 2,
            DraggedCollectionName: "Trabalho",
            TargetIsRoot: false,
            TargetCollectionId: 1,
            TargetCollectionName: "Viagens",
            TargetIsVirtual: false,
            TreeLookup: tree);
        var colPlan = CollectionDropPlanner.Plan(colRequest);
        Assert.True(colPlan.CanDrop);
        Assert.Equal(CollectionDropAction.Move, colPlan.Action);

        // Cenário 2: Arrastar Fotos (PhotoIds) sobre o mesmo nó Viagens
        var photoPlan = DropPlanner.Plan(new DropPlanRequest(
            PhotoIds: [100, 101],
            ActiveSourceCollectionId: null,
            TargetCollectionId: 1,
            TargetCollectionName: "Viagens",
            TargetIsVirtual: false,
            IsTargetHeaderOrEmpty: false,
            CopyPressed: true,
            PhotosCollectionsMap: new Dictionary<long, IReadOnlyList<long>> { [100] = [], [101] = [] },
            CollectionNames: new Dictionary<long, string> { [1] = "Viagens" }));
        Assert.True(photoPlan.CanDrop);
        Assert.Equal(DropAction.Add, photoPlan.Action);
    }

    [Fact]
    public async Task CatalogIntegrity_AuditZeroOrphansAndZeroCycles()
    {
        var path = _env.CreatePng("integ.png");
        var vm = _env.CreateLibrary();
        await vm.ImportFolderAsync(_env.Photos);

        var photo = vm.Photos.First(p => p.FileName == "integ.png").Photo;

        // Cria estrutura de teste:
        // A -> B -> C
        // X
        var a = await _env.Collections.CreateAsync("A", null);
        var b = await _env.Collections.CreateAsync("B", a.Id);
        var c = await _env.Collections.CreateAsync("C", b.Id);
        var x = await _env.Collections.CreateAsync("X", null);

        await _env.Collections.AddPhotosAsync(c.Id, [photo.Id]);
        await _env.Collections.AddPhotosAsync(x.Id, [photo.Id]);

        // Move B para a raiz
        await _env.Collections.MoveAsync(b.Id, null);
        // Move C para X
        await _env.Collections.MoveAsync(c.Id, x.Id);
        // Exclui B com promoção de filhos
        await _env.Collections.DeleteAsync(b.Id, DeleteMode.PromoteChildren);

        // Auditoria no SQLite:
        var allCollections = await _env.Collections.GetAllAsync();
        var collectionDict = allCollections.ToDictionary(k => k.Id);

        // 1. Prova 0 ciclos: subindo a cadeia de cada coleção nunca atinge o nó de início
        foreach (var col in allCollections)
        {
            var visited = new HashSet<long> { col.Id };
            var curr = col.ParentCollectionId;
            while (curr.HasValue)
            {
                Assert.DoesNotContain(curr.Value, visited);
                visited.Add(curr.Value);
                Assert.True(collectionDict.ContainsKey(curr.Value), "Coleção pai deve existir no catálogo.");
                curr = collectionDict[curr.Value].ParentCollectionId;
            }
        }

        // 2. Prova 0 relações órfãs em PhotoCollections
        var allPhotos = await _env.Repository.GetAllAsync();
        var photoDict = allPhotos.ToDictionary(p => p.Id);

        foreach (var col in allCollections)
        {
            var photoIds = await ((ICollectionRepository)_env.Repository).GetPhotoIdsInCollectionAsync(col.Id);
            foreach (var pid in photoIds)
            {
                Assert.True(photoDict.ContainsKey(pid), $"Foto {pid} vinculada à coleção {col.Name} deve existir.");
            }
        }

        // 3. Prova que os registros em Photos estão intocados
        Assert.Single(allPhotos);
        Assert.Equal("integ.png", allPhotos[0].FileName);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Performance_SyntheticVolume_5000Photos_200Collections_4Levels()
    {
        const int collectionCount = 200;
        const int photosCount = 5000;

        var collectionIds = new List<long>();
        var random = new Random(42);

        // Cria 200 coleções em até 4 níveis
        // 20 na raiz, e as demais como filhas de anteriores
        for (int i = 1; i <= collectionCount; i++)
        {
            long? parentId = null;
            if (i > 20 && collectionIds.Count > 0)
            {
                parentId = collectionIds[random.Next(collectionIds.Count)];
            }
            var created = await _env.Collections.CreateAsync($"Col_{i:D4}", parentId);
            collectionIds.Add(created.Id);
        }

        // Cria 5000 fotos sintéticas diretamente via SQLite em transação ultra-rápida
        var nowIso = DateTime.UtcNow.ToString("O");
        var dbPath = Path.Combine(_env.Root, "data", "photomanager.db");
        await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "INSERT INTO Photos (FileName, CurrentPath, Extension, FileSize, CreatedAt, ModifiedAt, ImportedAt) VALUES ($fn, $cp, '.jpg', 1024, $now, $now, $now)";
            var pFn = cmd.Parameters.Add("$fn", SqliteType.Text);
            var pCp = cmd.Parameters.Add("$cp", SqliteType.Text);
            var pNow = cmd.Parameters.Add("$now", SqliteType.Text);
            pNow.Value = nowIso;
            for (int i = 1; i <= photosCount; i++)
            {
                pFn.Value = $"img_{i:D5}.jpg";
                pCp.Value = $"K:\\Volume\\img_{i:D5}.jpg";
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        var allPhotos = await _env.Repository.GetAllAsync();
        var photoIds = allPhotos.Select(p => p.Id).ToList();

        // Distribui vínculos
        var sampleIds = photoIds.Take(1000).ToArray();
        await _env.Collections.AddPhotosAsync(collectionIds[0], sampleIds);
        var sampleIds2 = photoIds.Skip(1000).Take(1500).ToArray();
        await _env.Collections.AddPhotosAsync(collectionIds[1], sampleIds2);

        // Medição do tempo de cálculo dos contadores diretos (query agregada no SQLite)
        var swCounts = Stopwatch.StartNew();
        var counts = await ((ICollectionRepository)_env.Repository).GetDirectCountsAsync();
        swCounts.Stop();

        // Medição do tempo de montagem da árvore em memória
        var swTree = Stopwatch.StartNew();
        var tree = await _env.Collections.GetTreeAsync();
        swTree.Stop();

        static IEnumerable<CollectionTreeNode> Flatten(IEnumerable<CollectionTreeNode> nodes)
        {
            foreach (var n in nodes)
            {
                yield return n;
                foreach (var c in Flatten(n.Children))
                    yield return c;
            }
        }

        // Medição do planejamento em memória de mover uma coleção
        var treeLookup = Flatten(tree.Roots).ToDictionary(n => n.Id, n => (n.Name, n.ParentCollectionId));
        var swPlan = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            var req = new CollectionDropPlanRequest(
                collectionIds[50],
                "Col_0051",
                false,
                collectionIds[10],
                "Col_0011",
                false,
                treeLookup);
            _ = CollectionDropPlanner.Plan(req);
        }
        swPlan.Stop();

        // Asserções de integridade e volume
        Assert.Equal(2, counts.Count);
        Assert.Equal(1000, counts[collectionIds[0]]);
        Assert.Equal(1500, counts[collectionIds[1]]);
        Assert.Equal(collectionCount, treeLookup.Count);

        // Tempos devem ser confortavelmente menores que os limites (em memória / SQLite local)
        Assert.True(swCounts.ElapsedMilliseconds < 2000, $"Contagens diretas demoraram {swCounts.ElapsedMilliseconds} ms (limite: 2000 ms)");
        Assert.True(swTree.ElapsedMilliseconds < 1000, $"Árvore demorou {swTree.ElapsedMilliseconds} ms (limite: 1000 ms)");
        Assert.True(swPlan.ElapsedMilliseconds < 500, $"100 planejamentos demoraram {swPlan.ElapsedMilliseconds} ms (limite: 500 ms)");
    }
}
