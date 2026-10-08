# PhotoManager — Plano Mestre de Coleções e Subcoleções (Etapas A a E)

> Documento gerado na **Etapa A (Auditoria e Planejamento)** em conformidade com as diretrizes de `docs/prompt_colecoes/00_ANALISE_E_DECISOES.md` e do prompt `01_ETAPA_A_AUDITORIA_E_PLANO.md`.
> **Status:** **TODAS AS ETAPAS CONCLUÍDAS (A, B, C1, C2, D e E)** — 188 testes verdes em 5 passes consecutivos, 0 erros e 0 avisos em Debug e Release. Frente de coleções e subcoleções fechada.

---

## 1. Diagnóstico e Auditoria Detalhada (Situação Atual)

A auditoria inspecionou integralmente o código-fonte em `src/` e `tests/` para validar as afirmações da Seção 2 de `00_ANALISE_E_DECISOES.md`. Abaixo estão os achados com referências exatas de arquivos e linhas.

### 1.1 Tabela Comparativa de Auditoria

| Componente | Afirmação Original | Situação Real no Código (Evidência) | Status / Ajuste Necessário |
|---|---|---|---|
| **Identidade de Coleção** | Baseada em nome; `Photo.Collections` é `List<string>`. | `Photo.cs:28`: `public List<string> Collections { get; set; } = [];`. `PhotoFilter.cs:17` compara nomes: `photo.Collections.Any(c => string.Equals(c, Collection, StringComparison.OrdinalIgnoreCase))`. | **Confirmado.** Necessário migrar para `Photo.CollectionIds: List<long>` (D1). |
| **Schema e Constraints** | `Collections(Id, Name TEXT NOT NULL UNIQUE)`. `UNIQUE(Name)` impede mesmo nome sob pais diferentes. | `SqliteCatalogRepository.cs:39`: `CREATE TABLE IF NOT EXISTS Collections (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE);`. `PhotoCollections` em `SqliteCatalogRepository.cs:41`: `PRIMARY KEY(PhotoId, CollectionId)`. | **Confirmado.** SQLite não suporta `DROP CONSTRAINT` inline; recriação da tabela é obrigatória (D3). |
| **Gravação e Apagamento** | `SaveAsync` apaga todas as associações de coleções da foto e recria por nome. | `SqliteCatalogRepository.cs:165`: `DELETE FROM PhotoTags WHERE PhotoId = $id; DELETE FROM PhotoCollections WHERE PhotoId = $id`. `SqliteCatalogRepository.cs:172`: `INSERT OR IGNORE INTO Collections(Name) VALUES ($name); INSERT OR IGNORE INTO PhotoCollections(...) SELECT $id, Id FROM Collections WHERE Name = $name`. | **Confirmado.** `SaveAsync` destrói associações de coleções homônimas e apagaria vínculos feitos via drag/drop. |
| **Integridade e Foreign Keys** | Afirmação: "O SQLite não liga `PRAGMA foreign_keys` (nenhuma conexão faz isso); os `ON DELETE CASCADE` não disparam." | **Correção Crítica:** `SqliteCatalogRepository.cs:11` usa `new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true }.ToString()`. O driver `Microsoft.Data.Sqlite` ativa `PRAGMA foreign_keys = ON;` nessa conexão. Porém, conexões avulsas de testes (ex.: `MetadataEditorTests.cs:353`) abrem sem `ForeignKeys = true`. | **Ajustado / Esclarecido.** Embora a conexão do repositório principal habilite FKs, a Decisão D4 de manter **exclusão explícita transacional** permanece a melhor prática para evitar deleções indesejadas e garantir consistência em testes ou migrações legadas. |
| **Sidebar e "Sem coleção"** | Lista plana `CollectionEntries`; "Sem coleção" já existe como chave virtual `NoCollectionKey`. | `LibraryViewModel.cs:28`: `public const string NoCollectionKey = "__sem_colecao__";`. Linha 51: `_noCollectionOnly`. Linha 656: `FillEntries(CollectionEntries, ... NoCollectionKey, "Sem coleção", ...)`. | **Confirmado.** Metade da Etapa B já existe. Falta adicionar "Todas" no topo e organizar os contadores. |
| **Defeito no Combo de Filtro** | `RefillChoices` copia `entry.Key` de todas as entradas, expondo `__sem_colecao__` no combo sem filtrar. | `LibraryViewModel.cs:661`: `RefillChoices(CollectionChoices, CollectionEntries, nameof(CollectionChoice));`. Linha 675: `foreach (var entry in entries...) choices.Add(entry.Key);`. | **Confirmado.** O combo expõe a chave interna `__sem_colecao__` e, ao selecioná-la, gera filtro que retorna 0 fotos. Corrigir na Etapa B. |
| **Carregamento de Coleções** | Feito via subconsulta com `group_concat(c.Name, '\|')`. | `SqliteCatalogRepository.cs:513`: `(SELECT group_concat(c.Name, '\|') FROM PhotoCollections pc JOIN Collections c ON c.Id = pc.CollectionId WHERE pc.PhotoId = p.Id)`. Parse em `SqliteCatalogRepository.cs:531, 540`. | **Confirmado.** Precisará carregar também `pc.CollectionId` para popular `Photo.CollectionIds`. |
| **Pontos de Edição por Nome** | Painel Organização (1 foto e lote), `ReviewView`, mescla de duplicatas, testes. | `LibraryView.xaml:249, 266`, `LibraryViewModel.cs:223, 733`, `OrganizationBatch.cs:13, 23-34`, `ReviewView.xaml:138`, `PageViewModels.cs:130`. | **Confirmado.** Inventário detalhado na Seção 1.2. |
| **Microstock e Metadados** | Confirmar que não tocam coleções. | Verificado em `MicrostockViewModel.cs` e `MetadataEditorViewModel.cs`. Nenhum usa `Collections` nem tabela de coleções. | **Confirmado.** Isentos de alterações. |
| **Drag-and-Drop Atual** | Não existe no app hoje. | `LibraryView.xaml.cs` e `LibraryView.xaml`: apenas seleção múltipla, duplo clique para revisão e rolagem virtualizada. | **Confirmado.** Será implementado nas Etapas D e E sem conflitar com cliques e seleção. |

---

### 1.2 Inventário Completo de Usos de Coleção por Nome

A busca global identificou os seguintes pontos que tocam coleções pelo nome:

1. **Entidade de Domínio:**
   - [`Photo.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Domain/Photos/Photo.cs#L28): `public List<string> Collections { get; set; } = [];`
2. **Repositório e Persistência:**
   - [`SqliteCatalogRepository.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Persistence/SqliteCatalogRepository.cs#L39): DDL da tabela `Collections` com `Name TEXT NOT NULL UNIQUE`.
   - [`SqliteCatalogRepository.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Persistence/SqliteCatalogRepository.cs#L157): `GetCollectionsAsync()` retorna nomes.
   - [`SqliteCatalogRepository.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Persistence/SqliteCatalogRepository.cs#L159-L175): `SaveAsync` recebe `IReadOnlyCollection<string> collections`, apaga `PhotoCollections` e recria com `INSERT OR IGNORE INTO Collections(Name)`.
   - [`SqliteCatalogRepository.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Persistence/SqliteCatalogRepository.cs#L513): Subconsulta `group_concat(c.Name, '|')`.
3. **Serviços de Aplicação:**
   - [`ICatalogService.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Application/Catalog/ICatalogService.cs#L11-L20): `PhotoFilter.Collection` (string) e `Matches` que busca por igualdade de nome.
   - [`IOrganizationRepository.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Application/Catalog/IOrganizationRepository.cs#L9-L10): métodos baseados em coleções como strings.
   - [`IOrganizationService.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Application/Catalog/IOrganizationService.cs#L10-L18): repassa lista de nomes para o repositório.
   - [`OrganizationBatch.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Application/Catalog/OrganizationBatch.cs#L13-L34): `CollectionsToAdd` (`IReadOnlyList<string>`), `ParseList` e `ApplyTo(Photo photo)`.
4. **Interface e ViewModels:**
   - [`LibraryViewModel.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Wpf/Views/LibraryViewModel.cs#L223-L274): `BatchCollectionsText` (string separada por vírgulas).
   - [`LibraryViewModel.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Wpf/Views/LibraryViewModel.cs#L656-L661): Preenchimento de `CollectionEntries` e `CollectionChoices`.
   - [`LibraryViewModel.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Wpf/Views/LibraryViewModel.cs#L733): `PhotoCardViewModel.CollectionsText`: `string.Join(", ", Photo.Collections)` com setter que faz parse livre.
   - [`LibraryView.xaml`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Wpf/Views/LibraryView.xaml#L82): `ItemsControl ItemsSource="{Binding CollectionEntries}"`.
   - [`LibraryView.xaml`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Wpf/Views/LibraryView.xaml#L122): `ComboBox ItemsSource="{Binding CollectionChoices}"`.
   - [`LibraryView.xaml`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Wpf/Views/LibraryView.xaml#L249): `TextBox Text="{Binding SelectedPhoto.CollectionsText}"` (organização de 1 foto).
   - [`LibraryView.xaml`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Wpf/Views/LibraryView.xaml#L266): `TextBox Text="{Binding BatchCollectionsText}"` (organização em lote).
   - [`ReviewView.xaml`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Wpf/Views/ReviewView.xaml#L138): `TextBlock Text="{Binding CollectionsText}"` (somente leitura).
   - [`PageViewModels.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Wpf/Views/PageViewModels.cs#L130): Na mesclagem de fotos duplicadas: `kept.Collections = kept.Collections.Concat(photo.Photo.Collections).Distinct(StringComparer.OrdinalIgnoreCase).ToList()`.
5. **Testes Unitários e de Integração:**
   - [`SelectionAndOrganizationTests.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/tests/PhotoManager.Tests/SelectionAndOrganizationTests.cs#L32): `TagsAndCollectionsAreAddedWithoutRemovingOrDuplicating`.
   - [`LibraryViewTests.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/tests/PhotoManager.Tests/LibraryViewTests.cs#L26): `Sidebar_ShowsCountsForSmartListsFoldersCategoriesTagsAndCollections`.
   - [`LibraryViewTests.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/tests/PhotoManager.Tests/LibraryViewTests.cs#L68): `NoCollection_IsAVirtualFilter_AndUpdatesWhenPhotoJoinsACollection`.
   - [`LibraryRegressionTests.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/tests/PhotoManager.Tests/LibraryRegressionTests.cs#L43): `Organization_PersistsAcrossReload`.
   - [`LibraryRegressionTests.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/tests/PhotoManager.Tests/LibraryRegressionTests.cs#L58): `Filters_BySearchCategoryTagCollectionAndFavorites`.
   - [`LibraryRegressionTests.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/tests/PhotoManager.Tests/LibraryRegressionTests.cs#L74): `Batch_AppliesCategoryTagAndCollection_WithoutTouchingFiles`.

---

## 2. Modelo Proposto (Dados, Schema e Migração)

### 2.1 Schema do Banco de Dados

Conforme as Decisões D2 e D3:
- O nome da coleção é único **apenas entre coleções irmãs** (mesmo pai), com comparação insensível a maiúsculas/minúsculas (`COLLATE NOCASE`).
- Coleções raiz possuem `ParentCollectionId = NULL`.
- O índice único composto trata `ParentCollectionId = NULL` usando `COALESCE(ParentCollectionId, 0)`.

```sql
-- Nova estrutura da tabela Collections
CREATE TABLE Collections_new (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    ParentCollectionId INTEGER NULL REFERENCES Collections(Id) ON DELETE RESTRICT,
    SortOrder INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
);

-- Tabela associativa PhotoCollections (inalterada na essência, preservando integridade)
-- CREATE TABLE IF NOT EXISTS PhotoCollections (
--     PhotoId INTEGER NOT NULL,
--     CollectionId INTEGER NOT NULL,
--     PRIMARY KEY(PhotoId, CollectionId),
--     FOREIGN KEY(PhotoId) REFERENCES Photos(Id) ON DELETE CASCADE,
--     FOREIGN KEY(CollectionId) REFERENCES Collections(Id) ON DELETE CASCADE
-- );

-- Índices essenciais
CREATE UNIQUE INDEX IF NOT EXISTS UX_Collections_Parent_Name 
ON Collections (COALESCE(ParentCollectionId, 0), Name COLLATE NOCASE);

CREATE INDEX IF NOT EXISTS IX_Collections_ParentCollectionId 
ON Collections (ParentCollectionId);

CREATE INDEX IF NOT EXISTS IX_PhotoCollections_CollectionId 
ON PhotoCollections (CollectionId);
```

### 2.2 Migração Passo a Passo (Idempotente e com Backup - D3)

A rotina de migração em `SqliteCatalogRepository.InitializeAsync` será executada na inicialização:

1. **Detecção:**
   - Executa `PRAGMA table_info(Collections)`.
   - Verifica se a coluna `ParentCollectionId` já existe. Se existir, a migração estrutural já foi realizada.
2. **Cópia de Segurança Pré-Migração (D3):**
   - Se a migração for necessária e o arquivo de banco existir, copia `photomanager.db` para `photomanager.db.pre-colecoes.bak`.
   - Se o `.bak` já existir, não sobrescreve.
3. **Transação de Migração:**
   ```sql
   BEGIN TRANSACTION;

   -- 1. Cria a nova tabela
   CREATE TABLE Collections_new (
       Id INTEGER PRIMARY KEY AUTOINCREMENT,
       Name TEXT NOT NULL,
       ParentCollectionId INTEGER NULL REFERENCES Collections(Id),
       SortOrder INTEGER NOT NULL DEFAULT 0,
       CreatedAt TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
   );

   -- 2. Copia todos os registros existentes (todos viram coleções raiz)
   INSERT INTO Collections_new (Id, Name, ParentCollectionId, SortOrder, CreatedAt)
   SELECT Id, Name, NULL, 0, strftime('%Y-%m-%dT%H:%M:%SZ', 'now')
   FROM Collections;

   -- 3. Substitui a tabela antiga
   DROP TABLE Collections;
   ALTER TABLE Collections_new RENAME TO Collections;

   -- 4. Cria índices
   CREATE UNIQUE INDEX IF NOT EXISTS UX_Collections_Parent_Name 
   ON Collections (COALESCE(ParentCollectionId, 0), Name COLLATE NOCASE);

   CREATE INDEX IF NOT EXISTS IX_Collections_ParentCollectionId 
   ON Collections (ParentCollectionId);

   COMMIT;
   ```
4. **Preservação de Dados:**
   - Os valores de `Id` são copiados integralmente; portanto, todas as chaves estrangeiras em `PhotoCollections(PhotoId, CollectionId)` continuam perfeitamente válidas, sem nenhum registro órfão.

---

## 3. Entidades de Domínio e API de Aplicação (`ICollectionService`)

### 3.1 Alterações na Entidade `Photo` (Decisão D1)

```csharp
namespace PhotoManager.Domain.Photos;

public sealed class Photo
{
    // ... propriedades existentes ...
    
    /// <summary>
    /// Identificadores das coleções às quais a foto pertence. Fonte da verdade da associação (D1).
    /// </summary>
    public List<long> CollectionIds { get; set; } = [];

    /// <summary>
    /// Rótulos das coleções para exibição rápida (nome simples ou "Pai / Filho"). Derivado e somente leitura para regras de negócio (D1).
    /// </summary>
    public List<string> Collections { get; set; } = [];
}
```

### 3.2 Modelos de Coleção no Domínio / Aplicação

```csharp
namespace PhotoManager.Domain.Collections;

public sealed class CollectionItem
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public long? ParentCollectionId { get; init; }
    public int SortOrder { get; init; }
    public DateTime CreatedAt { get; init; }
}

public enum CollectionDeletionMode
{
    DeleteOnlyThisAndPromoteChildren, // Modo 1: Promove filhos para o pai da coleção excluída
    DeleteThisAndAllDescendants,       // Modo 2: Exclui esta e todas as subcoleções recursivamente
    Cancel                            // Modo 3: Cancela a operação
}

public sealed record CollectionOperationResult(bool Success, string? ErrorMessage = null, string? NoticeMessage = null);
```

### 3.3 Interface `ICollectionService`

```csharp
namespace PhotoManager.Application.Collections;

public interface ICollectionService
{
    Task<IReadOnlyList<CollectionItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<CollectionItem?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
    
    // CRUD de Coleções
    Task<CollectionOperationResult> CreateAsync(string name, long? parentId = null, CancellationToken cancellationToken = default);
    Task<CollectionOperationResult> RenameAsync(long collectionId, string newName, CancellationToken cancellationToken = default);
    Task<CollectionOperationResult> MoveCollectionAsync(long collectionId, long? newParentId, CancellationToken cancellationToken = default);
    Task<CollectionOperationResult> DeleteAsync(long collectionId, CollectionDeletionMode mode, CancellationToken cancellationToken = default);

    // Associação de Fotos por ID (operações pontuais sem recriar todas - D3 / D12)
    Task AddPhotosToCollectionAsync(long collectionId, IReadOnlyList<long> photoIds, CancellationToken cancellationToken = default);
    Task RemovePhotosFromCollectionAsync(long collectionId, IReadOnlyList<long> photoIds, CancellationToken cancellationToken = default);
    Task MovePhotosAsync(long sourceCollectionId, long targetCollectionId, IReadOnlyList<long> photoIds, CancellationToken cancellationToken = default);

    // Validações
    Task<bool> CanMoveCollectionAsync(long collectionId, long? targetParentId, CancellationToken cancellationToken = default);
    Task<int> GetDepthAsync(long? parentId, CancellationToken cancellationToken = default);
}
```

### 3.4 Regras de Negócio e Validações no Serviço

1. **Prevenção de Ciclos e Profundidade (Decisões D7 e D8):**
   - Antes de mover uma coleção para `newParentId`, verificar:
     - `collectionId == newParentId` (auto-referência direta).
     - Se `newParentId` é qualquer descendente de `collectionId` (ciclo indireto).
     - Se a profundidade resultante da subárvore excede **8 níveis** (`MaxDepth = 8`).
2. **Unicidade de Nome entre Irmãos (Decisão D2):**
   - Não permite dois irmãos com mesmo nome sob o mesmo pai (ignorando maiúsculas/acentos).
3. **Resolução de Conflitos na Promoção de Filhos (Decisão D4 e ponto 5 das decisões):**
   - Se a exclusão no modo `DeleteOnlyThisAndPromoteChildren` gerar colisão de nomes com irmãos no nível pai, aplicar sufixo sequencial ` (2)`, ` (3)` e registrar a alteração no `NoticeMessage` retornado ao usuário.
4. **Isolamento de `SaveAsync` (Decisão D12):**
   - `SaveAsync` do catálogo deixa de fazer `DELETE FROM PhotoCollections`. As operações pontuais (`AddPhotosToCollectionAsync`, `RemovePhotosFromCollectionAsync`, `MovePhotosAsync`) garantem alterações atômicas sem sobrescrever associações prévias.

---

## 4. Plano de Interface com o Usuário (UI)

### 4.1 Estrutura da Seção COLEÇÕES na Sidebar

A seção COLEÇÕES na barra lateral terá uma organização em dois blocos:

```text
COLEÇÕES
├── [Ícone Todas] Todas               (N)  <- Virtual (D9/D11), fixo no topo
├── [Ícone Vazio] Sem coleção         (N)  <- Virtual (D9), fixo no topo
└── ────────────────────────────────────    <- Separador sutil
    [TreeView de Coleções Reais]           <- Baseado no estilo FolderTreeView
    ├── ▾ Viagens                      (5)  <- Dica: "5 fotos diretas; 12 em subcoleções"
    │   ├── 2025                       (4)
    │   └── 2026                       (8)
    └── ▸ Trabalho                     (10)
```

### 4.2 Modelo de Nós da Árvore (`CollectionNode`)

Espelha a mecânica testada e estável de [`FolderNode.cs`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/src/PhotoManager.Wpf/Views/FolderNode.cs):
- `Id`: `long` identificador único.
- `Label`: `string` nome da coleção.
- `DirectCount`: `int` número de fotos associadas diretamente.
- `SubtreeCount`: `int` fotos nas subcoleções.
- `Children`: `ObservableCollection<CollectionNode>`.
- `IsExpanded` / `IsSelected`: lembrança de estado em memória (Decisão D10).
- `Path`: caminho hierárquico textual para ToolTip ("Viagens / 2026").

### 4.3 Menus de Contexto e Ações (C2)

Ao clicar com botão direito sobre um nó de coleção real:
1. **Nova subcoleção…** (desabilitado se profundidade atual >= 8).
2. **Renomear…**
3. **Excluir coleção…** (abre diálogo com 3 opções: Promover filhos, Excluir tudo, Cancelar).
4. **Adicionar fotos selecionadas** (se houver fotos marcadas na grade).

*Itens virtuais ("Todas", "Sem coleção") não possuem menu de contexto e não aceitam operações de coleção.*

### 4.4 Substituição da Caixa de Texto Livre por Seleção Estruturada (C2)

- Conforme decisão D1 e item 6 do documento mestre, caixas de texto com nomes separados por vírgula deixam de fazer sentido num modelo hierárquico com possíveis homônimos.
- No painel Organização (1 foto e lote), a caixa de texto será substituída por um botão seletor:
  - Exibe tags/pills das coleções atribuídas (ex.: `[Viagens / 2026 ✕]`).
  - Botão `+ Adicionar à coleção…` que abre popup/diálogo com a árvore de coleções para escolha rápida com caixa de busca.

### 4.5 Ajuste do Combo de Filtros da Barra Superior (Etapa B)

- O combo de filtros exibirá apenas coleções reais.
- O item inicial do combo será renomeado de "Todas" para **"Qualquer"** (evitando confusão com o item virtual "Todas").
- O valor `__sem_colecao__` é totalmente removido de `CollectionChoices`.

---

## 5. Plano de Drag-and-Drop (Etapas D e E)

### 5.1 Princípios Arquiteturais e Desacoplamento

1. **`DropPlanner` Puro:**
   Toda a regra de decisão sobre o que fazer ao soltar é implementada em uma classe pura em `PhotoManager.Application.Collections`, sem referências a WPF ou controles visuais, permitindo testes unitários de 100% dos cenários (arrastar grupo, mover vs adicionar, validação de ciclos).
2. **Formatos de Dados no `IDataObject`:**
   - `PhotoManager.PhotoIds`: `long[]` para fotos arrastadas.
   - `PhotoManager.CollectionId`: `long` para coleção arrastada.
3. **Detecção Segura de Limiar de Movimento:**
   - Na grade `PhotoList`, não iniciar o arraste imediatamente no `MouseDown` para não interferir com cliques de seleção, caixas de seleção ou duplo clique.
   - Armazenar o ponto inicial e só disparar `DragDrop.DoDragDrop` após o cursor percorrer a distância mínima do sistema (`SystemParameters.MinimumHorizontalDragDistance`).
4. **Diferenciação de Adicionar vs Mover (Decisão D6):**
   - **Arrastar normal (sem Shift):** Operação de **Adicionar** à coleção destino.
   - **Arrastar com tecla Shift:** Operação de **Mover**.
   - **Origem Ambígua no Mover:** Se as fotos forem arrastadas com Shift mas o filtro ativo na sidebar não for uma coleção real específica (ex.: estava em "Todas", "Sem coleção" ou numa Pasta), abre um menu flutuante ao soltar:
     - *Adicionar à coleção destino*
     - *Mover de «Coleção X» para esta coleção* (uma opção para cada coleção comum às fotos)
     - *Cancelar*

---

## 6. Mapa de Testes (16 Testes Oficiais + Extras)

| # | Teste | Etapa | Descrição / Invariante Validada |
|---|---|---|---|
| 1 | `Todas_ReturnsPhotosInAtLeastOneCollection_WithoutDuplicates` | **B** | Foto em múltiplas coleções conta exatamente 1 vez em "Todas". |
| 2 | `Todas_IsNotTheWholeLibrary` | **B** | Fotos sem coleção não aparecem em "Todas". |
| 3 | `SemColecao_ReturnsPhotosWithoutAnyAssociation_AndCreatesNoPhysicalCollection` | **B** | "Sem coleção" traz apenas fotos sem coleção e não cria registro no banco. |
| 4 | `Counters_AreDirectOnly_AndTodasPlusSemColecaoEqualsTotal` | **B** | Invariante: `Todas + Sem coleção = Total do Catálogo`. |
| 5 | `Counters_UpdateAfterSaveBatchAndDuplicateMerge` | **B** | Contadores recalculam em memória após ações do usuário sem recarregar o banco. |
| 6 | `CollectionCombo_DoesNotExposeInternalKeys` | **B** | Garante que `__sem_colecao__` não aparece no ComboBox e que o padrão é "Qualquer". |
| 7 | `Migration_OldFlatDatabase_MigratesToHierarchicalSchema_PreservingAssociations` | **C1** | Banco legado sem `ParentCollectionId` migra criando `.bak` e mantendo Ids e `PhotoCollections`. |
| 8 | `CollectionService_CreateAndRename_EnforcesSiblingNameUniquenessCaseInsensitive` | **C1** | Irmãos com mesmo nome são rejeitados; mesmo nome em pais diferentes é permitido. |
| 9 | `CollectionService_MoveCollection_PreventsCyclesAndEnforcesMaxDepth` | **C1** | Rejeita A->A, A->B->A, A->B->C->A e árvores com profundidade > 8. |
| 10 | `CollectionService_Delete_DeleteOnlyThis_PromotesChildrenAndHandlesNameCollisions` | **C1** | Exclusão modo 1 promove filhos e renomeia com sufixo ` (2)` em caso de conflito. |
| 11 | `CollectionService_Delete_DeleteWithSubcollections_CleansPhotoCollectionsExplicitly` | **C1** | Exclusão modo 2 remove subcoleções e limpa `PhotoCollections` em transação sem órfãos. |
| 12 | `Photo_CollectionIds_IsSourceOfTruth_AndCollectionsDerived` | **C1** | `Photo.CollectionIds` comanda a persistência e `Photo.Collections` reflete o caminho. |
| 13 | `LibraryView_CollectionTree_RendersHierarchyAndPreservesExpansion` | **C2** | TreeView reflete a estrutura em árvore e preserva nós abertos após atualização. |
| 14 | `OrganizationPanel_UsesPickerInsteadOfFreeText` | **C2** | Atribuição de coleções feita via IDs, eliminando erros de digitação. |
| 15 | `DropPlanner_DropPhotos_WithoutShift_AddsToCollection` | **D** | Planejador define ação "Adicionar" sem mexer nas coleções anteriores da foto. |
| 16 | `DropPlanner_DropPhotos_WithShift_MovesFromActiveCollection` | **D** | Planejador define ação "Mover" removendo da coleção ativa e adicionando na de destino. |
| 17 | `DropPlanner_AmbiguousSource_RequestsDisambiguationMenu` | **D** | Quando não há coleção ativa única, aciona o menu de desambiguação. |
| 18 | `DropPlanner_DropCollection_ValidatesHierarchyAndPreventsIllegalMoves` | **E** | Arraste de coleção para outra coleção ou raiz previne ciclos na UI. |

---

## 7. Cronograma e Entregas por Etapa

```text
[Etapa A] Auditoria e Plano Mestre (concluído)
    │
    ▼
[Etapa B] Itens Virtuais e Contadores (concluído — 127 testes verdes)
    ├── Adicionar "Todas" no topo (com tooltip)
    ├── Corrigir defeito do combo (__sem_colecao__ -> "Qualquer")
    ├── Separador visual e flag IsVirtual
    └── Contadores corretos com invariante (Todas + Sem Coleção = Total)
    │
    ▼
[Etapa C1] Modelo, Migração e Serviço (concluído — 140 testes verdes)
    ├── Schema com ParentCollectionId e índice por irmão (UX_Collections_Parent_Name)
    ├── Migração idempotente com cópia .bak e PRAGMA foreign_keys = OFF
    ├── ICollectionService com prevenção de ciclos, profundidade 8 e deleção com sufixos
    └── Photo.CollectionIds como fonte da verdade (Photo.Collections derivado)
    │
    ▼
[Etapa C2] Árvore e CRUD na UI (concluído — 155 testes verdes)
    ├── CollectionNode e TreeView integrado aos temas existentes
    ├── Menus de contexto (Criar subcoleção, Renomear [F2], Mover, Excluir [Del])
    ├── Diálogos modais WPF (CollectionNameDialog, DeleteCollectionDialog, MoveCollectionDialog)
    ├── Validações em tempo real e proteção de integridade (ciclos, profundidade 8)
    └── Substituição de campo de texto livre por Chips com botão (×) e ComboBox estruturado
    │
    ▼
[Etapa D] Drag-and-Drop de Fotos (concluído — 171 testes verdes)
    ├── Início por limiar de movimento na grade (sem quebrar cliques, Ctrl/Shift ou revisão)
    ├── Shift = mover / sem Shift = adicionar
    ├── DropPlanner (função pura de planejamento e desambiguação com menu AskMenu)
    ├── Payload leve PhotoIdsDragData (IDs de fotos selecionadas ou foto única)
    ├── Feedback visual flutuante (DragFeedbackPopup) e destaque do nó sob cursor (IsDragOver)
    └── Auto-expansão após 700 ms e auto-rolagem suave da sidebar
    │
    ▼
[Etapa E] Drag-and-Drop de Coleções e Fechamento (concluído — 188 testes verdes)
    ├── Arraste de coleção sobre coleção (reparentar / mover)
    ├── Arraste de subcoleção para o cabeçalho "COLEÇÕES" (mover para a raiz)
    ├── Prevenção completa de ciclos na UI (CollectionDropPlanner) e defesa no SQLite (ICollectionService)
    ├── Detecção e bloqueio de irmãos homônimos com indicação do nome conflitante
    ├── Validação de profundidade máxima de 8 níveis somando alturas de subárvores
    ├── Convivência segura de múltiplos formatos de payload no mesmo nó (PhotoIds vs CollectionId)
    ├── Realce visual no alvo da raiz (IsDragOverRoot) e nós (IsDragOver) + popup informativo flutuante
    ├── Auditoria de integridade do banco: 0 relações órfãs, 0 ciclos, fotos e arquivos intocados
    └── Teste de desempenho com 5.000 fotos x 200 coleções em 4 níveis (< 1000 ms)
```

---

## 8. Riscos de Regressão e Mitigações

1. **Risco de Corrupção no Banco Legado:**
   - *Mitigação:* Cópia atômica `photomanager.db.pre-colecoes.bak` antes de qualquer alteração; transação SQLite com `DROP TABLE` e `RENAME` apenas após sucesso da cópia.
2. **Risco de Sobrescrita de Associações por `SaveAsync` Antigo:**
   - *Mitigação:* `SaveAsync` não tocará mais na tabela `PhotoCollections`. Apenas `ICollectionService` fará mutações na tabela de relacionamento.
3. **Risco de Lentidão em Catálogos Grandes:**
   - *Mitigação:* Contadores na sidebar calculados inteiramente em memória numa única passada sobre `_all`, sem emitir queries repetitivas por nó.
4. **Risco de Conflito de Mouse no Drag-and-Drop:**
   - *Mitigação:* Uso rigoroso de `SystemParameters.Minimum*DragDistance` para diferenciar cliques simples e seleção em bloco do início do arraste.
