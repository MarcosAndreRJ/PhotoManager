# StorageRoot + RelativePath + Relocalização + Backup/Restore — Análise e decisões

Leia este arquivo antes de rodar qualquer etapa. Os prompts `01` a `08` referenciam as decisões **S1–S14** daqui. Se discordar de alguma, edite aqui antes de rodar a etapa que depende dela.

## 1. Como usar

| Arquivo | Etapa | O que entrega | Toca no banco? |
|---|---|---|---|
| `01_ETAPA_A_AUDITORIA_E_PLANO.md` | A | `docs/STORAGE_ROOT_AND_BACKUP_PLAN.md` (só documento) | Não |
| `02_ETAPA_B_STORAGEROOT_ENTIDADE.md` | B | Entidade, tabela, repositório, `user_version`, colunas novas **nulas** em `Photos` | Sim (aditivo) |
| `03_ETAPA_C_PATH_RESOLVER.md` | C | `IPhotoPathResolver` e migração de todos os consumidores de `CurrentPath` | Não |
| `04_ETAPA_D_MIGRACAO_DE_PHOTOS.md` | D | Inferência de raízes, confirmação, migração com backup, recriação de `Photos` | **Sim (crítico)** |
| `05_ETAPA_E_RAIZES_OFFLINE.md` | E | Disponibilidade, offline sem marcar "ausente", tela Bibliotecas e armazenamento | Não |
| `06_ETAPA_F_RELOCALIZACAO.md` | F | Validação, detecção automática, relocalizar raiz e pasta | Sim (UPDATE) |
| `07_ETAPA_G_BACKUP.md` | G | Pacote `.pmb` com API de backup do SQLite | Não |
| `08_ETAPA_H_RESTORE.md` | H | Restore com `PreRestoreBackup`, assistente de raízes, rollback | Sim |

Rode **uma por vez**, na ordem. Cada uma termina com relatório e espera `CONTINUE`. A ordem do pedido foi mantida; o único ajuste é que a Etapa B já cria as colunas novas (nulas) para que a C possa existir antes da D (ver S3).

## 2. Estado atual verificado no código

| Tema | Fato (arquivo) | Consequência |
|---|---|---|
| Modelo | `Photo` tem só `CurrentPath` (absoluto), `FileName`, `FileSize`, `ContentHash`, `IsMissing`… (`Domain/Photos/Photo.cs`) | Não existe raiz, caminho relativo nem registro de "pastas importadas" |
| Schema | `Photos.CurrentPath TEXT NOT NULL UNIQUE` + índice (`SqliteCatalogRepository.cs:23,36`) | Trocar identidade do caminho exige **recriar a tabela `Photos`** (UNIQUE inline não sai com `ALTER`) |
| **FK ligada** | Toda conexão usa `ForeignKeys = true` (`SqliteCatalogRepository.cs:11`). 6 tabelas filhas de `Photos` com `ON DELETE CASCADE` (`PhotoTags`, `PhotoCollections`, `MetadataHistory`, `UploadRecords`…) | **Risco nº 1:** `DROP TABLE Photos` com FK ativa apaga em cascata tags, coleções, **histórico de metadados e UploadRecords**. A migração tem de usar `PRAGMA foreign_keys=OFF` (fora de transação) + `foreign_key_check` (S6). A 1ª análise de coleções dizia o contrário; já foi corrigida em `docs/prompt_colecoes` |
| Versão do schema | Não há tabela nem `user_version`; migrações são `CREATE IF NOT EXISTS` + `ALTER` com `catch` | Backup precisa de `DatabaseVersion` e o restore de checar compatibilidade → introduzir `PRAGMA user_version` (S4) |
| Consumidores de `CurrentPath` | ~35 usos em `src` (Application: `MetadataEditService`, `BatchMetadataService`, `DuplicateModels`, `MicrostockModels`, `MicrostockEvaluationService`, `CatalogService`; Infrastructure: `FileOperationService` (9); Persistence: 11; Wpf: `LibraryViewModel` (5), `MetadataEditorViewModel` (2), `ReviewViewModel` (2), `PageViewModels`, `FolderNode`) e **44 em testes** (8 arquivos) | Migração gradual com resolver; testes precisam de um *helper* que cria raiz+foto |
| Pastas importadas | `ImportFolderAsync(folderPath)` varre recursivamente e grava `info.FullName`; **a pasta escolhida não é guardada** (`CatalogService.cs`) | Raiz lógica tem de ser **inferida** na migração e **definida** no import novo |
| Árvore de pastas | `FolderNode.Build` deriva a árvore de `Path.GetDirectoryName(photo.CurrentPath)` e colapsa cadeias sem fotos | É a mesma heurística que a migração precisa; reaproveitar a ideia, não o código de UI |
| Ausente × offline | `UpdateMissingStatesAsync` roda `File.Exists` em **todas** as fotos a **cada** `GetPhotosAsync` e grava `IsMissing=1` (`SqliteCatalogRepository.cs:120-139`); `PhotoCardViewModel` também chama `File.Exists` por cartão (`LibraryViewModel.cs:716`) | **Risco nº 2:** com HD desconectado todas as fotos seriam gravadas como "ausentes" de forma persistente. Em NAS lento, `File.Exists` em massa trava. Offline precisa ser estado **da raiz**, não da foto (S7) |
| Thumbnails | `ThumbnailService.GetOrCreateAsync` devolve `null` se o **original** não existe, antes de olhar o cache; cache em `Cache\{PhotoId}.jpg` (`ThumbnailService.cs:15-18`) | Raiz offline perde miniatura mesmo existindo em cache. Inverter a ordem. Como a chave é `PhotoId`, o cache sobrevive a relocalização |
| Operações físicas | `FileOperationService`: mover/copiar/renomear/lixeira recebem pasta absoluta e gravam `CurrentPath` via `UpdateLocationAsync` | Passam a calcular `(raiz, relativo)`; mover entre raízes troca `StorageRootId` |
| Hash | `ContentHash`, `HashedAtSize`, `HashedAtModified` existem; gravar metadados zera o hash e atualiza o tamanho | Validação de relocalização por hash só usa fotos com hash válido (`HashedAtSize == FileSize`); tamanho é o sinal principal |
| Configuração | `Data\settings.json` (chave/valor), `Cache\`, `Logs\` em `ApplicationPaths` (`PHOTOMANAGER_ROOT` isola dados) | Backup inclui `settings.json`; thumbnails são opcionais |
| Tela de configurações | Item "Configurações" existe na navegação, mas `SettingsViewModel` é uma **classe vazia** | A tela "Bibliotecas e armazenamento" é construída do zero na Etapa E |
| Exclusão de foto no catálogo | Não existe `DELETE FROM Photos` (lixeira só marca `IsMissing`) | `PhotoId` já é estável; o backup só precisa preservá-lo |
| Conexões SQLite | Uma conexão por chamada, com *pool* padrão do `Microsoft.Data.Sqlite` | Para trocar o `.db` no restore é preciso `SqliteConnection.ClearAllPools()` antes (S12) |

## 3. Ajustes ao pedido original

1. **Offline não é "ausente".** O pedido diz "fotos permanecem no catálogo"; o código atual transformaria offline em `IsMissing` persistente. A Etapa E separa os conceitos (S7).
2. **Migração de `Photos` exige recriar a tabela** e é a operação mais perigosa do projeto inteiro por causa da FK em cascata (S6). Por isso a D é isolada e tem backup obrigatório.
3. **Não há "pastas importadas" salvas**, então "descobrir a raiz lógica" é inferência. A D produz uma **proposta** que o usuário confirma antes de gravar (S5), em vez de decidir sozinha.
4. **`DatabaseVersion` do manifest** não tem de onde vir hoje → `PRAGMA user_version` (S4).
5. **`CurrentPath` sai do modelo no fim da C/D**, não fica "transitório para sempre". Durante B→C o resolver cai no caminho legado quando `StorageRootId` é nulo (S3).
6. **Restore troca o arquivo do banco com o app aberto**: exige limpar *pool* de conexões e recarregar a biblioteca (S12).
7. **Segurança do `.pmb`:** um backup vem de fora; `RelativePath` com `..` ou caminho absoluto não pode escapar da raiz (S9). O manifest leva SHA-256 de cada entrada para detectar corrupção (S10).
8. Itens do pedido **fora de escopo** (coleções hierárquicas, IA, upload, similaridade) continuam proibidos; a frente de coleções tem prompts próprios e **migra o mesmo schema** — ver coordenação em S14.

## 4. Decisões vigentes

| # | Decisão |
|---|---|
| S1 | **Identidade da localização = `(StorageRootId, RelativePath)`.** `Photo.Id` nunca muda por relocalização, mudança de letra, mover ou restore. |
| S2 | `RelativePath`: separador `\`, sem barra inicial/final, comparação **sem diferenciar maiúsculas** (`COLLATE NOCASE`), proibido `..`, caminho absoluto e nomes reservados; normalizado por **uma** função pura (`RelativePathNormalizer`). Índice único `(StorageRootId, RelativePath COLLATE NOCASE)`. |
| S3 | **Transição:** na B, `Photos` ganha `StorageRootId INTEGER NULL` e `RelativePath TEXT NULL` (aditivo, sem recriar). Na C, o resolver usa `CurrentPath` legado quando `StorageRootId IS NULL`. Na D, a tabela é recriada **sem** `CurrentPath`, com as duas colunas `NOT NULL`, e o fallback é removido. `Photo.CurrentPath` sai do modelo na C/D (a UI e os serviços só usam o resolver). |
| S4 | `PRAGMA user_version` passa a ser a versão do schema (B grava; D e coleções incrementam). `DatabaseVersion` do manifest = esse valor. Banco com versão **maior** que a do app → restore recusa. |
| S5 | **Inferência de raízes na D (sem filesystem, só texto):** por unidade/compartilhamento UNC, agrupar pelas pastas das fotos; raiz candidata = maior ancestral comum do grupo que **não** seja a raiz da unidade. Se a unidade tem ramos independentes (`D:\Fotos` e `D:\Trabalho\X`), cada ramo vira raiz candidata. Fotos diretamente em `D:\` → raiz `D:\` marcada **ambígua**. Resultado = **proposta** com nome sugerido (nome da pasta), contagem e flag de ambiguidade; **só grava depois que o usuário confirma** (pode ajustar o caminho para um ancestral ou renomear). Nunca assumir `D:\`. |
| S6 | **Migração de `Photos`:** backup `photomanager.db.pre-storageroot.bak` (uma vez, sem sobrescrever), conexão dedicada com `PRAGMA foreign_keys=OFF` **antes** do `BEGIN`, tabela nova, cópia mantendo `Id`, `DROP`/`RENAME`, recriar índices, `PRAGMA foreign_key_check` vazio e contagens iguais (fotos e todas as tabelas filhas) antes do `COMMIT`, depois `foreign_keys=ON`. Falha = `ROLLBACK` e erro claro; banco intacto. Idempotente. |
| S7 | **Disponibilidade é da raiz** (`Online` / `Offline`), calculada em memória e cacheada por *snapshot* (não persistida como verdade). `IsMissing` só é recalculado para fotos de raízes **online**; fotos de raiz offline mantêm o valor persistido e ganham o estado de exibição "Raiz indisponível". Checar a raiz **uma vez** por ciclo (com *timeout*), nunca `File.Exists` por foto em raiz offline. |
| S8 | **Raízes aninhadas permitidas; vence a mais profunda.** Dada uma pasta absoluta, a raiz é a mais profunda que a contém. Import de uma pasta que **não** está em nenhuma raiz cria uma raiz nova **na pasta escolhida** (nome = nome da pasta). Importar um ancestral de raízes existentes: arquivos dentro delas continuam nelas; o restante vai para a raiz nova. Duplicata física é impedida porque a resolução do caminho absoluto → `(raiz, relativo)` é determinística. |
| S9 | O resolver **sempre** valida que o caminho final fica dentro da raiz (`GetFullPath` + prefixo). Falha de validação = foto tratada como indisponível, nunca exceção para a UI. |
| S10 | `.pmb` = ZIP com `manifest.json`, `database/photomanager.db`, `settings/settings.json`, `optional/thumbnails/` (se pedido). Manifest com os campos do pedido **mais** `Entries[{Path, Sha256, Size}]`, `AppVersion`, `CreatedBy` e `Notes`. Originais **nunca** entram nesta versão (`IncludesOriginals=false` fixo). |
| S11 | Banco do backup é gerado com a **API de backup do SQLite** (`SqliteConnection.BackupDatabase`) para um arquivo temporário, validado (`PRAGMA integrity_check`, `foreign_key_check`) e só então zipado. Nunca copiar o `.db` aberto. |
| S12 | **Restore:** (1) validar `.pmb` por completo em pasta temporária; (2) criar `PreRestoreBackup` (um `.pmb` do estado atual, mantido; não é apagado automaticamente); (3) `ClearAllPools`, trocar banco e settings; (4) rodar `InitializeAsync` (migrações de bancos antigos); (5) assistente de raízes; (6) recarregar a biblioteca. Cancelar ou falhar antes do passo final → **rollback** a partir do `PreRestoreBackup`. Restore **nunca** altera arquivos de fotos. |
| S13 | **Automático só com certeza:** remapeamento sem pergunta apenas quando `VolumeSerial` confere **e** a validação é "forte" **e** só há um candidato. Qualquer dúvida vira sugestão que o usuário confirma. |
| S14 | **Coordenação com a frente de coleções** (`docs/prompt_colecoes`): as duas recriam tabelas e usam `user_version`. Rodar **uma frente inteira por vez**; qual primeiro é decisão sua. Se coleções (C1) rodar antes, a D daqui parte do schema novo de `Collections`; se StorageRoot rodar antes, a C1 de coleções incrementa `user_version` a partir do valor desta frente. Nenhuma das duas pode rodar enquanto a outra está pela metade. |

## 5. Riscos consolidados

| Risco | Etapa | Mitigação |
|---|---|---|
| `DROP TABLE Photos` apagando tags, coleções, histórico e `UploadRecords` por cascata | D | S6: FK desligada na conexão, `foreign_key_check`, contagem de todas as filhas antes/depois, `.bak`, teste com banco real de exemplo |
| Raiz inferida errada (fotos agrupadas sob raiz incorreta) | D | S5: proposta + confirmação; ambiguidade documentada; relocalização posterior (F) conserta |
| Offline virando `IsMissing` persistente | E | S7 + teste "desconectar raiz não altera `IsMissing`" |
| `File.Exists` em massa em NAS lento | C/E | Checar disponibilidade da raiz uma vez, *timeout*, nunca por foto |
| Resolver em caminho quente (grade com milhares de fotos) | C | *Snapshot* de raízes em memória, sem consulta ao banco por foto; teste de volume |
| Regressão em ~35 usos + 44 em testes | C | Migração por consumidor, build/test a cada grupo, helper de testes |
| Relocalização aplicada na raiz errada | F | Validação por amostra/tamanho/hash, nunca automática em dúvida (S13), mostrar resultado antes de gravar |
| Restore apagando o catálogo atual | H | `PreRestoreBackup` obrigatório, rollback, restore em área temporária antes do swap |
| Trocar `.db` com conexões em *pool* | H | `ClearAllPools`, recarregar ViewModels, teste de restore com app "em uso" |
| `.pmb` malicioso ou corrompido | G/H | SHA-256 por entrada, limites de tamanho e de entradas, proteção contra *zip slip*, `RelativePath` validado (S9) |
| Colisão de migrações com a frente de coleções | D/H | S14 |

## 6. Arquivos prováveis

`Domain`: `StorageRoot`, `Photo` (+`StorageRootId`, `RelativePath`). `Application`: `IStorageRootRepository`, `IPhotoPathResolver`, `RelativePathNormalizer`, `StorageRootService` (disponibilidade, relocalização), `RootInferenceService`, `RootValidator`, `BackupService`/`RestoreService`. `Persistence`: `SqliteCatalogRepository` (+ migração, `user_version`), repositório de raízes. `Infrastructure`: `VolumeInfoReader` (rótulo/serial/tipo), `FileOperationService`, `ThumbnailService`, `PmbPackage`. `Wpf`: `SettingsView`/`SettingsViewModel` (Bibliotecas e armazenamento), diálogos de relocalização/backup/restore, `FolderNode`/sidebar por raiz. Testes: novos arquivos + helper `TestCatalog`.
