# PHOTOMANAGER — PORTABILIDADE · ETAPA B (entidade StorageRoot, repositório e schema aditivo)

## CONTEXTO COMUM

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Fases 1–10, rodada de UX e Etapa A desta frente concluídas. Releia: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/STORAGE_ROOT_AND_BACKUP_PLAN.md` e **`docs/prompts_storageroot/00_ANALISE_E_DECISOES.md`** (S1–S14).

**Regras inegociáveis:** não mexer em subcoleções/drag de coleções, IA, upload, similaridade; originais nunca incluídos em backup; nunca alterar arquivos de fotos; preservar dados e `PhotoId`; migração idempotente com teste de banco antigo; **FK do SQLite está LIGADA**; sem mocks; sem regra de negócio em `.xaml.cs`; build Debug e Release **0/0**; `dotnet test` verde **≥ 5× seguidas, uma por vez** (feche `PhotoManager.exe`); `[Collection("WpfUi")]` em testes de janela; `PHOTOMANAGER_ROOT` isola dados; nunca testar no banco real do usuário; docs registram o que **não** foi verificado. **Pare ao fim e relate** e espere `CONTINUE`.

---

## ESTA ETAPA — B: entidade, tabela e repositório (migração **aditiva**; nenhuma foto é migrada)

### 1. Entidade (Domain)
`StorageRoot`: `Id`, `Name`, `OriginalPath`, `CurrentPath`, `VolumeLabel?`, `VolumeSerial?`, `DriveType` (enum: `Fixed`, `Removable`, `Network`, `Unknown`), `CreatedAt` (UTC), `LastSeenAt?` (UTC), `IsAvailable` (**runtime, não persistido** como verdade — S7), `Notes?`. `NetworkPath` não é coluna extra: raiz de rede tem `DriveType=Network` e `CurrentPath` UNC (ou unidade mapeada); documente. `OriginalPath` = onde a raiz estava quando foi criada (imutável; serve à detecção automática).

### 2. Schema (idempotente) e versão
- Tabela `StorageRoots` com as colunas acima (sem `IsAvailable`), `Name` único sem diferenciar maiúsculas, índice por `CurrentPath`.
- `Photos` ganha **`StorageRootId INTEGER NULL`** e **`RelativePath TEXT NULL`** via `ALTER ... ADD COLUMN` (S3). Nada é preenchido ainda; `CurrentPath` continua como está. Crie `INDEX` em `(StorageRootId, RelativePath COLLATE NOCASE)` **não único** por enquanto (a unicidade só entra na D).
- Introduza `PRAGMA user_version` (S4): ler o valor atual, gravar o novo ao fim de `InitializeAsync`; defina uma constante `CurrentSchemaVersion` e um ponto único onde cada migração declara a versão que produz. Bancos antigos sem versão = 0.
- `InitializeAsync` repetido N vezes não muda nada além da primeira (teste).
- **Não** use `foreign_keys=OFF` aqui: nada é recriado.

### 3. Repositório e normalização
- `IStorageRootRepository` (Application) implementado em `SqliteCatalogRepository` (ou classe irmã na Persistence): `GetAllAsync`, `GetAsync(id)`, `AddAsync`, `UpdateCurrentPathAsync(id, newPath)`, `RenameAsync`, `TouchLastSeenAsync(id)`, `FindContainingAsync(absolutePath)` (aplica S8: raiz **mais profunda** que contém a pasta; sem consulta por arquivo).
- `RelativePathNormalizer` (função pura, Application): normaliza separadores, remove barras iniciais/finais, rejeita `..`, caminho absoluto/UNC e caracteres inválidos (S2); `Combine(root, relative)` seguro (S9) e `TryGetRelative(root, absolute)`; compara sem diferenciar maiúsculas; trata raiz `D:\` e UNC `\\nas\fotos`.
- `VolumeInfoReader` (Infrastructure): `Read(path)` → rótulo, serial (`GetVolumeInformation`), tipo; UNC/falha → campos nulos, **sem exceção**. Interface para permitir fake nos testes.
- Registre tudo no DI (`App.xaml.cs`). Sem UI nesta etapa.

### 4. Testes
Criação/leitura/atualização de raiz; nome único (maiúsc./minúsc.); `FindContaining` (raiz mais profunda, raízes aninhadas, `D:\Fotos` não casa `D:\FotosX`); normalizador (separadores, `..`, absoluto, UNC, caixa, raiz de unidade, nomes com acento); `Combine` não escapa da raiz; idempotência do `InitializeAsync`; `user_version` gravado e bancos antigos viram versão atual **sem perder** nenhuma tabela/linha; colunas novas existem e são nulas em banco antigo; `VolumeInfoReader` com fake e caminho inexistente. Cobre do pedido: parte de 1, 3 (fundação), 9 (parse de UNC).

### Verificação e documentação
Build Debug+Release 0/0; `dotnet test` ≥ 5×; abrir o app com dados isolados e confirmar que tudo funciona como antes (a UI não muda). Atualize `IMPLEMENTATION_STATUS.md`, `HANDOFF.md` (schema, `user_version`, serviços novos), `ARCHITECTURE.md`, `KNOWN_LIMITATIONS.md` e o plano.

**Pare e relate.** Não inicie a Etapa C.
