# PHOTOMANAGER — PORTABILIDADE · ETAPA C (IPhotoPathResolver e migração dos consumidores)

## CONTEXTO COMUM

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Etapas A e B desta frente concluídas (`StorageRoots`, colunas nulas em `Photos`, `user_version`, `RelativePathNormalizer`). Releia: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/STORAGE_ROOT_AND_BACKUP_PLAN.md` e **`docs/prompts_storageroot/00_ANALISE_E_DECISOES.md`** (S1–S14).

**Regras inegociáveis:** não mexer em subcoleções/drag de coleções, IA, upload, similaridade; nunca alterar arquivos de fotos; preservar `PhotoId`; sem mocks; **sem regra de negócio em `.xaml.cs`**; build Debug e Release **0/0**; `dotnet test` verde **≥ 5× seguidas, uma por vez** (feche `PhotoManager.exe`); `[Collection("WpfUi")]`; `PHOTOMANAGER_ROOT` isola dados; nunca no banco real do usuário; docs registram o que **não** foi verificado. **Pare ao fim e relate** e espere `CONTINUE`.

---

## ESTA ETAPA — C: resolver central e migração gradual dos consumidores (sem migrar dados)

Nenhuma foto é convertida (isso é a D). Aqui o **código** passa a pedir o caminho ao resolver; enquanto `StorageRootId` é nulo, o resolver devolve o `CurrentPath` legado (S3), então o comportamento visível não muda.

### 1. `IPhotoPathResolver` (Application)
- `string ResolveAbsolutePath(Photo photo)` — nunca monta caminho fora do resolver; aplica S9 (dentro da raiz); sem raiz → legado.
- `bool TryResolve(Photo photo, out string path)`; `PhotoLocation Describe(Photo)` com `{ AbsolutePath, StorageRootId, RootName, RootAvailability }` (a disponibilidade real vem na E; aqui devolva `Online` quando o caminho legado é usado e exponha o ponto de extensão).
- `(long RootId, string Relative)? ToLocation(string absolutePath)` — inverso, via S8 (raiz mais profunda). Usado por import, mover, copiar e renomear.
- Baseado em **snapshot em memória** das raízes (carregado na inicialização e invalidado por evento/`Refresh()` quando uma raiz muda). **Proibido** consultar o banco por foto (caminho quente da grade).

### 2. Migração dos consumidores (um grupo por vez, build+teste a cada grupo)
Use o inventário da Etapa A. Substitua `photo.CurrentPath` por `resolver.ResolveAbsolutePath(photo)` (injetado) em:
1. **Application:** `MetadataEditService`, `BatchMetadataService`, `DuplicateModels`, `MicrostockModels`, `MicrostockEvaluationService`, `CatalogService` (import: `ToLocation`/criação de raiz fica para a D; aqui só leitura/`FindByPath` via resolver).
2. **Infrastructure:** `FileOperationService` (mover/copiar/renomear/lixeira): origem via resolver; destino absoluto → `ToLocation`; ainda gravando legado enquanto a D não vem, mas **por uma única função** `ApplyLocation(photo, absolute)` (ponto único que a D troca).
3. **Persistence:** `FindByPathAsync`, `AddAsync`, `UpdateLocationAsync`, `UpdateMissingStatesAsync` continuam por `CurrentPath`, **encapsulados** atrás de métodos que recebem/retornam localização, para a D trocar sem espalhar SQL.
4. **Wpf:** `LibraryViewModel` (inclui filtro de pasta e `PhotoCardViewModel`), `MetadataEditorViewModel`, `ReviewViewModel`, `PageViewModels` (duplicatas), `FolderNode`, `ToolsView` ("abrir pasta"), miniaturas. Os ViewModels recebem o resolver pelo construtor; **não** concatenam caminho.
5. Entrada nova no `Photo`: propriedades `StorageRootId`/`RelativePath` (da B) preenchidas pelo repositório quando existirem.

Ao final: `grep` em `src` não encontra `CurrentPath` fora da Persistence (e do resolver, para o fallback legado). Registre os usos restantes e por quê.

### 3. Testes
Resolver: raiz+relativo; fallback legado; não escapa da raiz (`..`, caminho absoluto, `D:\FotosX`); caixa; UNC; `ToLocation` com raízes aninhadas; snapshot invalidado ao alterar raiz; **volume** (≥ 5.000 resoluções em tempo desprezível, sem acesso a banco — mede e registra). Regressão: **todos os testes existentes continuam verdes**; migre os 44 usos de `CurrentPath` em testes para um helper `TestCatalog` (cria raiz+foto) **sem apagar cobertura**. Cobre do pedido: 1, 2 (parcial), 9.

### Verificação e documentação
Build Debug+Release 0/0; `dotnet test` ≥ 5×; app com dados isolados: importar `docs/Fotos`, abrir revisão, editar metadados, mover, renomear, miniaturas — idêntico a antes (captura de janela; sem mouse real). Atualize `IMPLEMENTATION_STATUS.md`, `HANDOFF.md` (regra: ninguém monta caminho), `ARCHITECTURE.md`, `KNOWN_LIMITATIONS.md`, `MANUAL_TEST_CHECKLIST.md`.

**Pare e relate.** Não inicie a Etapa D.
