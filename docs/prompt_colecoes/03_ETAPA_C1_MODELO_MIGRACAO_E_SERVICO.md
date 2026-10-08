# PHOTOMANAGER — COLEÇÕES E SUBCOLEÇÕES · ETAPA C1 (modelo, migração e serviço — SEM UI nova)

## CONTEXTO COMUM

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Fases 1–10, rodada de UX e Etapas A/B de coleções concluídas. Releia antes: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/COLLECTIONS_HIERARCHY_PLAN.md` e **`docs/prompt_colecoes/00_ANALISE_E_DECISOES.md`** (D1–D12 vigentes — em especial D1 identidade por Id, D2 nome único entre irmãos, D3 migração por recriação com `.bak`, D4 exclusão sem tocar arquivos, D7 profundidade 8, D12 sem `SaveAsync` para coleções).

**Regras inegociáveis:** coleções são **virtuais** (nunca tocar arquivos físicos nem excluir `Photos`); **não** mexer em StorageRoot, RelativePath, backup/restauração do roadmap, upload, similaridade, IA; preservar dados; migração **idempotente** com **teste de banco antigo**; sem mocks; sem regra de negócio em `.xaml.cs`; build Debug e Release **0/0**; `dotnet test` verde **≥ 5× seguidas, uma execução por vez** (nunca em paralelo nem durante build; feche `PhotoManager.exe`); testes de janela com `[Collection("WpfUi")]`; use `PHOTOMANAGER_ROOT` para dados de teste; docs com o que **não** foi verificado. **Pare ao fim e relate** (feito / decisões / build-test reais / não verificado / defeitos / docs / "CONTINUE?").

---

## ESTA ETAPA — C1: dados, migração e serviço (camadas Domain/Application/Persistence + ajustes mínimos de ViewModel para compilar)

**Escopo:** hierarquia no banco, identidade por Id, serviço com todas as regras (CRUD, ciclos, exclusão, adicionar/mover fotos) **testado sem UI**. A árvore, o menu de contexto e os diálogos são a **Etapa C2**; arrastar é D/E. Nesta etapa a UI continua funcionando como hoje (lista plana), apenas alimentada pelos novos dados — não remova funcionalidades.

### 1. Modelo e schema
- `Collection`: `Id`, `Name`, `ParentCollectionId?`, `SortOrder`, `CreatedAt` (UTC). `PhotoCollection`: `PhotoId`, `CollectionId` (PK composta já existente = restrição única exigida).
- **Migração (D3):** `ALTER` não remove o `UNIQUE(Name)` inline → **recriar `Collections`** numa transação: criar `Collections_new` com as colunas novas, copiar as linhas **mantendo os Ids** (`ParentCollectionId = NULL`, `SortOrder` = ordem alfabética 0..n, `CreatedAt` = agora), recriar `PhotoCollections` se necessário para apontar à tabela nova, `DROP` da antiga, `RENAME`. Índice único: `(COALESCE(ParentCollectionId,0), Name COLLATE NOCASE)`. Índice em `ParentCollectionId` e em `PhotoCollections(CollectionId)`.
- Antes de migrar um banco **existente** com dados, copiar o `.db` para `photomanager.db.pre-colecoes.bak` (uma vez; não sobrescrever se já existir). Idempotência: rodar `InitializeAsync` N vezes não altera nada além da primeira.
- Verificação interna após migrar: nº de coleções e nº de relações iguais ao de antes; nenhuma relação órfã; se falhar, reverter a transação e lançar erro claro (não deixar o banco pela metade).
- **Atenção — a FK está LIGADA** (`ForeignKeys = true` na connection string do repositório). `DROP TABLE Collections` com FK ativa apaga em cascata as linhas de `PhotoCollections`. Na migração use uma conexão dedicada: `PRAGMA foreign_keys=OFF` **antes** de `BEGIN`, recriar/copiar/renomear, `PRAGMA foreign_key_check` (deve vir vazio) antes do `COMMIT`, depois `PRAGMA foreign_keys=ON`. Teste com relações reais provando que nenhuma se perdeu. Toda exclusão de coleção também apaga **explicitamente** `PhotoCollections` das coleções removidas, em transação. Escreva um teste de "sem órfãos" reutilizável.

### 2. Domínio/aplicação (D1)
- `Photo` ganha `CollectionIds : List<long>`. `Photo.Collections` (nomes) vira **derivado para exibição** (nome simples; se houver homônimos entre as coleções da foto, "Pai / Filho"), carregado junto com `CollectionIds` na consulta de fotos (uma passada, sem N+1).
- **`SaveAsync(photoId, …)` do repositório deixa de apagar/recriar coleções** (nem de criar coleção por nome). Ajuste a assinatura/uso (`IOrganizationRepository`, `OrganizationService`, chamadas) para que organização (categoria, nota, rating, favorito, tags) continue salvando **sem tocar** em `PhotoCollections`. Teste: arrastar/adicionar coleção e depois "Salvar organização" **não** desfaz a associação.
- Compatibilidade temporária (será removida na C2): onde a UI ainda grava por texto (`CollectionsText`, `OrganizationBatch.CollectionsToAdd`, mesclagem de duplicatas), resolver nomes → Ids **somente entre coleções raiz** via o novo serviço (criar raiz se não existir) e gravar com as operações novas; registre cada ponto no relatório como "ponte temporária até a C2".

### 3. `ICollectionService` (Application) — API sugerida
```
Task<CollectionTree> GetTreeAsync()                      // nós com Id, Name, ParentId, DirectCount (somente diretas), TotalDistinctInAny, WithoutCollection
Task<Collection> CreateAsync(string name, long? parentId)
Task<Collection> RenameAsync(long id, string newName)
Task<MoveResult> MoveAsync(long id, long? newParentId)   // null = raiz; valida ciclo, profundidade, nome entre irmãos
Task<DeleteResult> DeleteAsync(long id, DeleteMode mode) // PromoteChildren | WithDescendants
Task<int> AddPhotosAsync(long collectionId, IReadOnlyCollection<long> photoIds)        // ignora já existentes; retorna quantas foram adicionadas
Task<int> RemovePhotosAsync(long collectionId, IReadOnlyCollection<long> photoIds)
Task<MovePhotosResult> MovePhotosAsync(long fromId, long toId, IReadOnlyCollection<long> photoIds) // remove só de fromId, adiciona em toId, tudo numa transação; fotos que não estavam em fromId são só adicionadas
Task<IReadOnlyList<long>> GetAncestorsAsync(long id) / bool IsDescendantAsync(long id, long possibleAncestorId)
```
Regras e erros (mensagens em português, exceções/resultados tipados — não strings soltas):
- Nome: `Trim`, não vazio, ≤ 100 caracteres, **único entre irmãos** (sem diferenciar maiúsculas); conflito → erro claro.
- **Ciclos (obrigatório):** impedir mover para si mesmo, para um descendente, e qualquer volta (A↔B, A→B→C→A). Validar **no serviço** (fonte da verdade) antes de gravar; a UI só reflete.
- **Profundidade máxima 8 níveis** (ao criar e ao mover subárvore: altura da subárvore + novo nível ≤ 8).
- `DeleteAsync(PromoteChildren)`: filhos passam ao pai da excluída (ou raiz); se o nome já existir no destino, renomear o promovido com sufixo ` (2)`, ` (3)`… e devolver a lista de renomeações no resultado; remove só as relações `PhotoCollections` da coleção excluída. `DeleteAsync(WithDescendants)`: remove coleção + toda a subárvore + as relações dessas coleções. **Nunca** apaga linhas de `Photos` nem arquivos.
- Todas as operações compostas em **transação**; falha = nada muda.
- `AddPhotosAsync` não duplica (PK + `INSERT OR IGNORE`) e informa `AlreadyMember`. `MovePhotosAsync` com `from == to` = no-op sem erro.
- Atualizar os objetos `Photo` em memória é responsabilidade do chamador (ViewModel) usando o resultado; **não** recarregar o catálogo inteiro (D12). Exponha um método/evento para a `LibraryViewModel` aplicar `CollectionIds` às fotos afetadas.

### 3.1 Contagens (no serviço ou em função pura reutilizável)
- Direta por coleção: nº de fotos com relação direta (não soma descendentes).
- `Todas`: fotos distintas com ≥ 1 relação. `Sem coleção`: fotos sem relação. Invariante `Todas + Sem coleção = total`.

### 4. Ajustes de ViewModel (mínimos)
`LibraryViewModel`: a lista plana da sidebar passa a vir do serviço/`Photo.CollectionIds` (Ids, não nomes); filtro "coleção" por **Id** (substitua `CollectionFilter`/`PhotoFilter.Collection` por Id **sem quebrar** a API pública usada nos testes — se precisar quebrar, migre os testes mantendo a cobertura). Combo de filtro lista coleções reais (caminho "Pai / Filho" quando houver hierarquia; nesta etapa só há raízes, mas o código deve estar pronto).

### 5. Testes obrigatórios (do pedido, camada de serviço/dados)
6 adicionar à coleção · 7 mover para coleção (só da origem) · 8 não duplicar associação · 9 criar subcoleção · 10 mover coleção · 11 **impedir ciclos** (A→A, A→B→A, A→B→C→A, mover para descendente) · 12 mover coleção para a raiz · 13 excluir pai e promover filhos (incl. conflito de nome → sufixo) · 14 excluir hierarquia · 15 **fotos físicas e registros `Photos` nunca excluídos** (arquivo continua em disco; `Photos` intacto) · 16 contadores corretos (direta, Todas, Sem coleção, sem somar descendentes) · 1/2 coleção pai retorna só fotos diretas / subcoleção funciona · 3/4/5 Todas (distintas) e Sem coleção.
Extras: nome duplicado entre irmãos bloqueado e permitido entre pais diferentes; profundidade 8; migração de banco **antigo** (flat) preserva Ids/relações/contagens e cria `.bak`; migração idempotente; "sem órfãos" após cada exclusão; `SaveAsync` não mexe em coleções; transação reverte em erro simulado; renomear preserva relações.

### Verificação e documentação
Build Debug+Release 0/0; `dotnet test` ≥ 5× seguidas; teste manual da migração com cópia de um banco de exemplo (nunca o do usuário). Atualize `IMPLEMENTATION_STATUS.md`, `HANDOFF.md` (nova API, identidade por Id, pontes temporárias), `ARCHITECTURE.md` (modelo e fluxo), `KNOWN_LIMITATIONS.md`, `MANUAL_TEST_CHECKLIST.md` e o plano.

**Pare e relate.** Não inicie a C2 (árvore/CRUD na UI).
