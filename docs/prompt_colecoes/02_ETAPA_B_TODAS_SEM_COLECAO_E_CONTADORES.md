# PHOTOMANAGER — COLEÇÕES E SUBCOLEÇÕES · ETAPA B ("Todas", "Sem coleção", contadores)

## CONTEXTO COMUM

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Fases 1–10 e rodada de UX concluídas (≈118 testes verdes). Releia antes: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/COLLECTIONS_HIERARCHY_PLAN.md` (Etapa A) e **`docs/prompt_colecoes/00_ANALISE_E_DECISOES.md`** (decisões D1–D12 vigentes).

**Regras inegociáveis:** coleções são **virtuais** (nunca tocar arquivos físicos nem excluir `Photos`); **não** mexer em StorageRoot, RelativePath, backup, restauração, upload, similaridade, IA; preservar dados/migrações; sem mocks; sem regra de negócio em `.xaml.cs`; build Debug e Release **0 erros/0 avisos**; `dotnet test` verde **≥ 5× seguidas, uma execução por vez** (nunca em paralelo nem durante build; feche `PhotoManager.exe` antes de compilar); testes de janela com `[Collection("WpfUi")]`; docs atualizados com o que **não** foi verificado; `ComboBox` usa `ItemTemplate`; `PM_SNAPSHOT_DIR` para captura visual; usar `PHOTOMANAGER_ROOT` para dados de teste. **Pare ao fim e relate** (feito / decisões / build-test reais / não verificado / defeitos / docs / "CONTINUE?").

---

## ESTA ETAPA — B: itens virtuais e contadores (sem hierarquia, sem schema)

**Escopo:** só a sidebar/consultas atuais (coleções ainda por nome, lista plana). **Não** criar `ParentCollectionId`, não mexer em schema, não implementar árvore, CRUD nem drag.

### Estado de partida (confirme no código)
- "Sem coleção" **já existe** (`LibraryViewModel.NoCollectionKey`, `_noCollectionOnly`, `MatchesNoCollection`) — valide que atende ao pedido e **não** é uma coleção física.
- **Defeito conhecido:** `RefillChoices(CollectionChoices, CollectionEntries, …)` coloca a chave interna `__sem_colecao__` no combo de filtro "Coleção"; escolher isso não filtra nada.
- Contagem de cada coleção já é "direta" (nº de fotos com aquele nome em `Photo.Collections`).

### Requisitos
1. **"Todas"** (virtual) no topo da seção COLEÇÕES, acima de "Sem coleção": mostra **todas as fotos que pertencem a pelo menos uma coleção** (não "todas as fotos da Biblioteca"). Dica (tooltip): "Fotos que pertencem a pelo menos uma coleção".
2. **"Sem coleção"** (virtual): fotos sem nenhuma associação. Continua sendo só filtro (nada criado em `Collections`).
3. **Ordem na sidebar:** `Todas`, `Sem coleção`, (espaço/separador visual), coleções reais em ordem alfabética.
4. **Contadores (D11):** Todas = fotos **distintas** com ≥ 1 coleção; Sem coleção = fotos sem coleção; coleção = somente fotos diretamente associadas (ainda sem hierarquia, mas **não** somar nada além disso). Invariante: `Todas + Sem coleção = total de fotos do catálogo` (testar). Contagem inclui fotos com arquivo ausente (é contagem do catálogo).
5. **Itens virtuais** não são renomeáveis/excluíveis/arrastáveis nem recebem subcoleções (ainda não há menu; só garanta que o modelo os distingue: ex.: `SidebarKind.Collection` real vs. novo tipo/flag `IsVirtual`) e **não aparecem como valor no combo "Coleção"** (corrigir o defeito: combo = "Todas as coleções"? — atenção: o item padrão do combo hoje é `AllChoice` ("Todas") com significado "sem filtro"; **renomeie o rótulo padrão do combo para "Qualquer"** nesse combo para não confundir com o item virtual "Todas"; registre a mudança no relatório). O combo lista só coleções reais.
6. **Seleção e filtros:** clicar em "Todas"/"Sem coleção" limpa os outros filtros (como as demais entradas da sidebar), destaca o item e atualiza `HasActiveFilters`/"Limpar filtros". Selecionar uma coleção real continua filtrando por ela (direto). Os dois virtuais são mutuamente exclusivos entre si e com coleção real.
7. **Atualização dos contadores** sem recarregar o banco: ao salvar organização de uma foto, aplicar organização em lote, mesclar duplicatas, excluir/mover foto (reload) e ao importar, os números da seção COLEÇÕES refletem a mudança (reaproveite `RebuildSidebar`/`_all`; uma passada só sobre `_all`, sem consulta por coleção).
8. Sem regressão: busca, favoritas, pastas, categorias, tags e a seleção múltipla (Etapa 2 da rodada de UX) continuam como estão.

### Testes (adicione/ajuste; nomes sugeridos)
- `Todas_ReturnsPhotosInAtLeastOneCollection_WithoutDuplicates` (foto em 3 coleções conta/aparece **uma** vez)
- `Todas_IsNotTheWholeLibrary`
- `SemColecao_ReturnsPhotosWithoutAnyAssociation_AndCreatesNoPhysicalCollection` (verificar tabela `Collections` sem linha "Sem coleção")
- `Counters_AreDirectOnly_AndTodasPlusSemColecaoEqualsTotal`
- `Counters_UpdateAfterSaveBatchAndDuplicateMerge`
- `CollectionCombo_DoesNotExposeInternalKeys` (regressão do `__sem_colecao__`)
- `VirtualEntries_AreFlaggedVirtual_AndMutuallyExclusiveWithRealCollections`
- Teste de interface (STA): a seção COLEÇÕES mostra "Todas" e "Sem coleção" no topo com os números esperados e clicar filtra a grade.
Cobre do pedido original: itens 3, 4, 5 e 16 (parcial: sem hierarquia).

### Verificação
`dotnet build` Debug+Release (0/0), `dotnet test` ≥ 5× seguidas, e abra o app com dados isolados (`PHOTOMANAGER_ROOT`) para conferir visualmente (captura de janela; não use o mouse real sobre outras janelas do usuário).

### Documentação
`IMPLEMENTATION_STATUS.md` (seção "Coleções — Etapa B"), `HANDOFF.md`, `MANUAL_TEST_CHECKLIST.md` (itens manuais: clicar Todas/Sem coleção, números, combo), `KNOWN_LIMITATIONS.md` (se aplicável) e atualize o status no plano da Etapa A.

**Pare e relate.** Não inicie a Etapa C1.
