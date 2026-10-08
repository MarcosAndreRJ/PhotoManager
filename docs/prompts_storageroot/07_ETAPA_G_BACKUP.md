# PHOTOMANAGER — PORTABILIDADE · ETAPA G (backup do catálogo, pacote .pmb)

## CONTEXTO COMUM

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Etapas A–F desta frente concluídas. Releia: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/STORAGE_ROOT_AND_BACKUP_PLAN.md` e **`docs/prompts_storageroot/00_ANALISE_E_DECISOES.md`** (S1–S14, em especial **S4, S10, S11**).

**Regras inegociáveis:** é backup do **catálogo**, não das fotografias — **originais nunca entram** (`IncludesOriginals=false` fixo; não crie opção para isso agora); não mexer em subcoleções/drag de coleções, IA, upload, similaridade; backup **nunca** altera o catálogo em uso nem arquivos; sem mocks; **sem regra de negócio em `.xaml.cs`**; build Debug e Release **0/0**; `dotnet test` verde **≥ 5× seguidas, uma por vez** (feche `PhotoManager.exe`); `[Collection("WpfUi")]`; `PM_SNAPSHOT_DIR`; `PHOTOMANAGER_ROOT`; nunca no banco real do usuário em testes; docs registram o que **não** foi verificado. **Pare ao fim e relate** e espere `CONTINUE`.

---

## ESTA ETAPA — G: gerar o `.pmb` (restore é a H)

### 1. Formato
`PhotoManager_Backup_YYYY-MM-DD[_HHmm].pmb` = ZIP (`System.IO.Compression`):
```
manifest.json
database/photomanager.db
settings/settings.json
optional/thumbnails/{PhotoId}.jpg   (somente se marcado)
```
O banco já contém `StorageRoots`, categorias, tags, coleções, notas, avaliações, favoritos, `MetadataHistory`/versões, workflow, `Agencies`, `UploadRecords`, presets e perfis de validação — **confirme por tabela** (liste no relatório as tabelas do banco e que todas foram cobertas; se existir alguma tabela fora do `.db`, diga).

### 2. Manifest (S10)
`BackupVersion`, `ApplicationVersion`, `DatabaseVersion` (= `PRAGMA user_version`), `CreatedAt` (UTC), `PhotoCount`, `StorageRoots[{ Id, Name, OriginalPath, CurrentPath, VolumeLabel, VolumeSerial, DriveType, PhotoCount }]`, `CollectionsCount`, `TagsCount`, `IncludesThumbnails`, `IncludesOriginals=false`, `MachineName` (informativo), `Entries[{ Path, Size, Sha256 }]`, `Notes`. JSON versionado e tolerante a campos novos.

### 3. Geração consistente (S11)
1. Abrir conexão de leitura e usar `SqliteConnection.BackupDatabase(destino)` para um arquivo **temporário** (nunca copiar o `.db` em uso).
2. Validar a cópia: `PRAGMA integrity_check` = `ok`, `foreign_key_check` vazio, contagens (fotos, raízes) iguais às do banco vivo no momento do snapshot.
3. Copiar `settings.json` (leitura segura; ausente = pacote sem a pasta `settings/`, manifest informa).
4. Miniaturas opcionais: incluir `Cache\*.jpg` (informar tamanho estimado antes; cancelável; arquivo temporário `.tmp` fora do `.pmb` até concluir).
5. Calcular SHA-256 de cada entrada, escrever o manifest, gravar o `.pmb` em arquivo temporário e **renomear** para o destino só no sucesso (falha/cancelamento nunca deixa `.pmb` parcial). Limpar temporários sempre. Erros de disco cheio/permissão = mensagem clara.
6. Backup **durante uso**: o app continua utilizável; testar com escrita concorrente (ex.: salvar organização enquanto o backup roda) — o `.pmb` deve ser um snapshot consistente.

### 4. UI e serviço
`BackupService.CreateAsync(options, destination, progress, ct)` → `BackupResult` (caminho, tamanho, contagens, avisos). Em **Configurações → "Backup e restauração"** (nova seção/aba da tela da Etapa E): "Criar backup do catálogo…" com checklist (SQLite, configurações, raízes, categorias, tags, coleções, notas, avaliações, favoritos, versões de metadados, workflow, agências, UploadRecords, presets — marcados e **informativos**), checkbox **"Incluir miniaturas (opcional)"** (desmarcado), texto fixo **"As fotografias originais não são incluídas"**, escolha de destino, progresso, resultado com caminho e botão "Mostrar na pasta". "Restaurar…" aparece **desabilitado com dica "Próxima etapa"** (não esconder, não fingir). Sugerir pasta padrão em `Documents\PhotoManager\Backups`; **não** criar rotina automática agora.

### 5. Testes
Backup gera `.pmb` válido (abre como ZIP; manifest legível; `Entries` batem com os SHA-256); contagens do manifest corretas; `IncludesThumbnails` conforme opção; **originais ausentes do pacote** (varrer entradas); banco do pacote abre, `integrity_check` ok, mesmas linhas por tabela (fotos, filhas, raízes, `PhotoId` idênticos) (**pedidos 10 e 18**); backup concorrente com escrita (consistência); falha no meio (disco simulado/cancelamento) não deixa `.pmb` parcial nem temporários; destino sem permissão; nome com acento; banco legado (D "Decidir depois") ainda gera backup válido com aviso; tamanho e tempo registrados com 20.000 fotos. UI (STA): seção carrega e opções refletem estado (captura).

### Verificação e documentação
Build Debug+Release 0/0; `dotnet test` ≥ 5×; gerar um `.pmb` real de dados isolados e **inspecioná-lo** (descompactar, conferir manifest/hashes, abrir o `.db` num leitor SQLite) — cole a evidência no relatório. Atualize `IMPLEMENTATION_STATUS.md`, `HANDOFF.md` (especificação do `.pmb` e versionamento), `ARCHITECTURE.md`, `KNOWN_LIMITATIONS.md` (sem backup automático, sem originais, sem criptografia), `MANUAL_TEST_CHECKLIST.md`, e o plano.

**Pare e relate.** Não inicie a Etapa H.
