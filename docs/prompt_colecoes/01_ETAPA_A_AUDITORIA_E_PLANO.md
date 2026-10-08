# PHOTOMANAGER — COLEÇÕES E SUBCOLEÇÕES · ETAPA A (auditoria e plano — SEM código)

## CONTEXTO COMUM (vale para todas as etapas desta frente)

Projeto **PhotoManager** (WPF · .NET 10 · C# · MVVM · SQLite) em `K:\Trabalho\Projetos\PHOTOMANAGER`. Fases 1–10 e a rodada de UX (Etapas 2–4) estão concluídas (≈118 testes verdes, 0 erros/0 avisos em Debug e Release). Você tem acesso a build/testes/execução.

**Antes de qualquer coisa**, leia: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/KNOWN_LIMITATIONS.md`, `docs/MANUAL_TEST_CHECKLIST.md` e **`docs/prompt_colecoes/00_ANALISE_E_DECISOES.md`** (as decisões D1–D12 são vigentes; se algo lá estiver errado em relação ao código, **diga no relatório**, não improvise em silêncio).

**Objetivo geral desta frente:** organização **virtual** por coleções com hierarquia (pai/subcoleções), itens virtuais "Todas" e "Sem coleção", contadores corretos, CRUD, arrastar-e-soltar (fotos → coleção; coleção → coleção/raiz) e prevenção de ciclos.

**Regras inegociáveis**
- Coleções são **virtuais**: nunca mover, renomear, copiar ou excluir **arquivos físicos**; nunca excluir registros de `Photos` por causa de coleção.
- **Não mexer** em StorageRoot, RelativePath, backup/restauração, upload, similaridade, IA ou fases futuras.
- Preservar dados e migrações existentes; mudança de schema = **migração idempotente** + **teste com banco antigo**.
- Sem mocks/botões falsos; sem regra de negócio em `.xaml.cs`; reaproveitar padrões existentes (ex.: `FolderNode` + `Themes/Tree.xaml` na árvore de pastas).
- Qualidade por etapa: `dotnet build` Debug **e** Release com **0 erros/0 avisos**; `dotnet test` verde **≥ 5 execuções seguidas** (execute **uma por vez**: nunca duas ao mesmo tempo nem durante build; feche `PhotoManager.exe` antes de compilar); testes de janela usam `[Collection("WpfUi")]`; documentação atualizada com o que foi e o que **não** foi verificado.
- Lições do projeto: temas mesclados em `App.xaml`; `ComboBox` tem template próprio (usar `ItemTemplate`, não `DisplayMemberPath`); toda decodificação de imagem via `ImageLoader`; `PM_SNAPSHOT_DIR=<pasta>` em testes de janela grava PNG para inspeção visual; nunca testar sobre `%LocalAppData%\PhotoManager` do usuário (use `PHOTOMANAGER_ROOT`).
- **Pare ao fim da etapa**, apresente o relatório e aguarde `CONTINUE`.

**Relatório de fim de etapa:** (1) o que foi feito/arquivos; (2) decisões e trade-offs; (3) resultado **real** de build/test (nº de testes, repetições) e o que foi executado no app; (4) o que **não** foi verificado; (5) defeitos encontrados/corrigidos; (6) docs atualizados; (7) pergunte "CONTINUE?".

---

## ESTA ETAPA — A: auditoria e plano (documentação apenas)

**Não altere código-fonte, schema nem testes nesta etapa.** O produto é um único documento: `docs/COLLECTIONS_HIERARCHY_PLAN.md` (+ atualizações curtas nos docs de status).

### 1. Auditar (com evidência: arquivo e linha)
Confirme ou corrija cada afirmação da seção 2 de `00_ANALISE_E_DECISOES.md`. Cobrir obrigatoriamente:
1. **Modelo/dados:** tabelas `Collections` e `PhotoCollections` (DDL exata, índices, constraints), como `Photo.Collections` é carregado (consulta `PhotoSelect`/`group_concat`), `SaveAsync` (apaga e recria), onde coleções nascem (`INSERT OR IGNORE INTO Collections(Name)`), se há `PRAGMA foreign_keys` em algum ponto.
2. **Consultas/filtros:** `PhotoFilter`, `CatalogService`, filtros no `LibraryViewModel` (`CollectionFilter`, `_noCollectionOnly`, `MatchesNoCollection`), `CollectionChoices`/`RefillChoices` (confirmar o defeito do `__sem_colecao__` no combo).
3. **Sidebar:** `LibraryViewModel` (`CollectionEntries`, `SidebarEntry`, `SelectSidebar`, `UpdateSidebarCounts`, `UpdateSidebarSelection`), `LibraryView.xaml` (seção COLEÇÕES), padrão `FolderNode`/`Tree.xaml`.
4. **Pontos que editam/usam coleção por nome** (lista completa por `grep`): painel Organização (1 foto e lote), `OrganizationBatch`, `ReviewView`, mesclagem de duplicatas (`PageViewModels.cs`), `CollectionsText`, microstock/metadados (confirmar que **não** usam).
5. **Testes existentes** que tocam coleções (arquivo + nome do teste) e como cada um será migrado.
6. **Drag-and-drop:** existe algo hoje? Como a grade (`ListBox` + `VirtualizingWrapPanel`, seleção Extended) e a árvore de pastas tratam mouse (para planejar D/E).
7. **Migrações:** como `InitializeAsync` aplica `ALTER`/`CREATE IF NOT EXISTS`; como será a recriação de `Collections` (SQLite não remove `UNIQUE` inline).

### 2. Produzir `docs/COLLECTIONS_HIERARCHY_PLAN.md` com
- **Diagnóstico** (tabela "como é hoje" com evidências) e **impacto da hierarquia**.
- **Modelo proposto**: `Collection(Id, Name, ParentCollectionId?, SortOrder, CreatedAt)`; `PhotoCollection(PhotoId, CollectionId)` PK composta; índice único por irmão; DDL completa; migração passo a passo (com rollback/cópia `.bak`); consultas-chave (contagem direta por coleção, "Todas", "Sem coleção", árvore em memória, verificação de ciclo/ancestrais).
- **API de aplicação proposta** (`ICollectionService`: criar, renomear, mover, excluir com modo, adicionar/remover/mover fotos, obter árvore com contagens, validações e mensagens) e **mudanças em `Photo`** (D1).
- **Plano da UI**: `CollectionNode`, árvore, itens virtuais, menu de contexto, diálogos (nome, exclusão com 3 opções), substituição da caixa de texto por seleção, filtro/combo.
- **Plano de drag-and-drop** (D6): formatos de dados, planejador puro (`DropPlanner`), feedback, Shift, origem do "mover", seleção múltipla, itens inválidos.
- **Mapa de testes**: os 16 testes do pedido + os extras que você achar necessários, cada um ligado à etapa em que entram.
- **Arquivos a alterar/criar por etapa (B, C1, C2, D, E)** e **riscos de regressão** com mitigação.
- **Perguntas em aberto** (se houver) com sua recomendação. **Não pare por dúvidas pequenas:** escolha o padrão recomendado e registre.

### 3. Verificação
Rode `dotnet restore`, `dotnet build`, `dotnet test` **uma vez** só para registrar o ponto de partida (nº de testes verdes). Nenhuma alteração de código deve aparecer no diff (apenas `docs/`).

### 4. Atualizar docs
`docs/IMPLEMENTATION_STATUS.md` (nova seção "Coleções e subcoleções — Etapa A concluída", sem marcar nada como implementado) e `docs/HANDOFF.md` (link para o plano e para esta pasta).

**Pare e relate.** Não implemente a Etapa B.
