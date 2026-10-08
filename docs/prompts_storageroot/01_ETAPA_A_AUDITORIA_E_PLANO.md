# PHOTOMANAGER — PORTABILIDADE · ETAPA A (auditoria e plano — SOMENTE DOCUMENTO)

## CONTEXTO COMUM (vale para todas as etapas)

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Fases 1–10 e rodada de UX concluídas. Leia antes: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/KNOWN_LIMITATIONS.md` e **`docs/prompts_storageroot/00_ANALISE_E_DECISOES.md`** (decisões S1–S14).

**Objetivo da frente:** a biblioteca deixa de depender de caminho absoluto. Localização = `StorageRoot` + `RelativePath`, resolvida por serviço central; raízes múltiplas, offline, relocalizáveis; backup/restore do **catálogo** (`.pmb`) preservando `PhotoId`.

**Regras inegociáveis:** não mexer em subcoleções/drag de coleções, IA, upload automático, similaridade visual; **não incluir fotos originais** em backup; **nunca** apagar, mover ou alterar arquivos de fotos por causa de relocalização/backup/restore; preservar dados e `PhotoId`; migrações **idempotentes**, com **backup prévio** e teste com banco antigo; **a FK do SQLite está LIGADA** (ver S6); sem mocks; **sem regra de negócio em `.xaml.cs`**; `ComboBox` usa `ItemTemplate`; temas em `App.xaml`; build Debug e Release **0 erros/0 avisos**; `dotnet test` verde **≥ 5× seguidas, uma execução por vez** (nunca em paralelo nem durante build; feche `PhotoManager.exe`); testes de janela com `[Collection("WpfUi")]`; `PHOTOMANAGER_ROOT` isola dados de teste; **nunca testar migração no banco real do usuário, só em cópia**; nunca simular mouse/teclado reais sobre a tela do usuário; docs registram o que **não** foi verificado. **Pare ao fim e relate** (feito / decisões / build-test reais / não verificado / defeitos / docs / "CONTINUE?") e espere `CONTINUE`.

---

## ESTA ETAPA — A: auditoria e plano (**não altere código nem banco**)

Produza `docs/STORAGE_ROOT_AND_BACKUP_PLAN.md` **com evidência** (arquivo:linha). Não confie no `00_ANALISE`: **confirme** cada afirmação dele e registre divergências.

### Checklist
1. **Modelo `Photo`** e todos os campos ligados a localização/identidade.
2. **`CurrentPath`:** lista completa de consumidores (`grep` em `src` e `tests`), agrupada por camada e por tipo de uso (ler arquivo, exibir, filtrar por pasta, gravar, comparar, montar caminho). Marque quais são "hot path" (grade, miniaturas, `PhotoCardViewModel`).
3. **Schema e migrações:** DDL de `Photos` e filhas, FKs/cascades; **confirme com um teste de leitura que `PRAGMA foreign_keys` vale 1** nas conexões do repositório; ausência de versão de schema; lista de tabelas que referenciam `Photos(Id)` (para a contagem antes/depois da D).
4. **Pastas importadas:** como `ImportFolderAsync` grava e o que se perde; `FolderNode.Build`; filtro por pasta (`_folderFilter`, `IsInFolder`).
5. **Candidatas a StorageRoot no banco existente:** rode **somente leitura sobre uma CÓPIA** do banco (nunca o do usuário; se não houver banco de exemplo, gere um com `docs/Fotos` em `PHOTOMANAGER_ROOT` temporário) e liste os agrupamentos por unidade/UNC, a raiz candidata segundo S5 e os casos ambíguos. Se não for possível, diga explicitamente.
6. **Ausente × offline:** `UpdateMissingStatesAsync`, `PhotoCardViewModel.IsMissing`, todos os `File.Exists` por foto, custo em volume (estime com 5.000 fotos).
7. **Miniaturas:** `ThumbnailService`, chave de cache, comportamento com original ausente.
8. **Operações físicas:** `FileOperationService` (mover/copiar/renomear/lote/lixeira), e quem chama; destino é pasta absoluta escolhida em diálogo.
9. **Metadados e demais consumidores:** `MetadataEditService`, `BatchMetadataService`, leitura/escrita XMP, Microstock, duplicatas, `ToolsView` ("abrir pasta"), revisão/zoom.
10. **UploadRecords e histórico:** dependem só de `PhotoId`? Confirme.
11. **Configuração:** `settings.json`, quais chaves existem e se guardam caminhos; `ApplicationPaths`.
12. **Testes:** 8 arquivos com `CurrentPath`; proponha o helper de criação de foto/raiz.
13. **Volume/serial/tipo de drive:** o que o .NET dá (`DriveInfo`) e o que exige `GetVolumeInformation` (P/Invoke); comportamento para UNC.
14. **Conexão/pool** e impacto para trocar o `.db` (restore).

### Conteúdo do plano (`docs/STORAGE_ROOT_AND_BACKUP_PLAN.md`)
- Resumo da auditoria (tabelas, com evidência), divergências do `00_ANALISE`.
- Arquitetura proposta: entidades, schema final (DDL completa de `StorageRoots` e `Photos` novo), resolver, serviços, fluxos (import, mover, offline, relocalizar, backup, restore), diagrama em texto das dependências entre projetos.
- Plano de migração (S5/S6) com a lista de verificações antes/depois.
- Mapa de consumidores → como cada um passa a usar o resolver, por etapa.
- Lista dos 18 testes do pedido mapeados às etapas, mais os extras sugeridos nas etapas.
- Riscos, decisões **abertas** que exigem resposta do usuário (marque com **DECISÃO NECESSÁRIA**) e o que está fora de escopo.

### Verificação e documentação
Nenhum código muda; confirme com `dotnet build` (Debug+Release 0/0) e `dotnet test` (1×) que nada foi alterado. Atualize `IMPLEMENTATION_STATUS.md` (seção "Portabilidade — Etapa A") e `HANDOFF.md`.

**Pare e relate.** Não inicie a Etapa B.
