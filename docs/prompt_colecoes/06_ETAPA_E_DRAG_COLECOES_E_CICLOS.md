# PHOTOMANAGER — COLEÇÕES E SUBCOLEÇÕES · ETAPA E (arrastar coleção → coleção/raiz, ciclos e fechamento)

## CONTEXTO COMUM

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Fases 1–10, rodada de UX e Etapas A, B, C1, C2 e D de coleções concluídas (árvore, CRUD, serviço com ciclos, arrastar fotos). Releia antes: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/COLLECTIONS_HIERARCHY_PLAN.md` e **`docs/prompt_colecoes/00_ANALISE_E_DECISOES.md`** (D1–D12).

**Regras inegociáveis:** coleções são **virtuais** (nunca tocar arquivos físicos nem excluir `Photos`); **não** mexer em StorageRoot, RelativePath, backup, restauração, upload, similaridade, IA; sem mocks; **sem regra de negócio em `.xaml.cs`**; build Debug e Release **0/0**; `dotnet test` verde **≥ 5× seguidas, uma execução por vez** (nunca em paralelo nem durante build; feche `PhotoManager.exe`); testes de janela com `[Collection("WpfUi")]`; `PM_SNAPSHOT_DIR` para captura; **nunca usar mouse/teclado reais sobre a tela do usuário**; docs com o que **não** foi verificado. **Pare ao fim e relate.**

---

## ESTA ETAPA — E: arrastar coleções (reparentar), raiz, ciclos na UI e fechamento da frente

### 1. Comportamento
- **Arrastar uma coleção real** sobre outra coleção real = **mover** (`ParentCollectionId = destino`). Ex.: arrastar "Roraima 2026" para "Viagens".
- **Arrastar uma subcoleção para o cabeçalho "COLEÇÕES"** (ou área de raiz claramente indicada) = **mover para a raiz** (`ParentCollectionId = null`). Raiz não é nó virtual comum: o cabeçalho funciona como destino só para coleções, e **não** é destino válido para fotos (Etapa D).
- **Itens virtuais** ("Todas", "Sem coleção"): não podem ser **arrastados**, **nem receber** coleção (D9); no arraste de coleção sobre eles mostrar bloqueio.
- **Destinos inválidos (feedback de bloqueio com motivo):** a própria coleção; **qualquer descendente** dela; o pai atual (no-op: "já está nesta coleção"); destino que estoure a **profundidade 8** (ao somar a altura da subárvore); destino com **irmão homônimo** (conflito de nome) — informar qual nome conflita (não renomear automaticamente nesta operação).
- Mover coleção **não altera** `PhotoCollection` (as fotos continuam nas mesmas coleções) nem arquivos.
- Pós-operação: árvore reordenada alfabeticamente, destino expandido, coleção movida mantida selecionada, contagens inalteradas (são diretas), status "«Roraima 2026» movida para «Viagens»".
- **Prevenção de ciclos:** a regra vive no **serviço** (C1: A→A, A↔B, A→B→C→A, descendentes). A UI **consulta** o serviço/função pura (`CanMove`) durante o `DragOver` (sem I/O pesado: use a árvore em memória) e **também** trata o erro do serviço no `Drop` (defesa dupla; nunca confiar só na UI).

### 2. Feedback visual
- Durante o arraste: `Mover “Roraima 2026” para “Viagens”` / `Mover “Roraima 2026” para a raiz`; destino válido destacado; inválido com cursor de bloqueio + motivo curto ("Não é possível mover para dentro de si mesma", "Criaria um ciclo", "Já existe «Natal» neste nível", "Limite de 8 níveis").
- Auto-expandir sobre nó recolhido (~700 ms) e auto-rolar a sidebar (reutilize o mecanismo da Etapa D).
- Alvo "raiz" aparece destacado (ex.: faixa/realce no cabeçalho "COLEÇÕES") só quando o arraste é de coleção.

### 3. Arquitetura (testável)
- Estenda o planejador da D (ou crie `CollectionDropPlanner`, função pura): entrada `{ draggedCollectionId, target (nó real/virtual/raiz/nenhum), tree }` → `Move(newParentId?) | Blocked(reason) | NoOp(reason)`.
- Payload do arraste de coleção em **formato próprio** (`PhotoManager.CollectionId`), distinto do de fotos; o `DragOver/Drop` decide pelo formato presente (um nó de árvore pode receber ambos os tipos).
- `LibraryViewModel`/VM da árvore executa via `ICollectionService.MoveAsync`; sem regra no code-behind.

### 4. Testes
- Planejador: mover para outra coleção; mover para raiz; no-op (mesmo pai); **bloqueios:** si mesma, descendente (1 e N níveis), A↔B, A→B→C→A, nó virtual, profundidade 8 excedida, homônimo no destino; destino "nenhum".
- Serviço/VM: `ParentCollectionId` atualizado; relações de fotos intactas; árvore e contagens atualizadas; erro do serviço tratado (ciclo forçado por chamada direta não passa); mover subárvore grande preserva descendentes.
- UI (STA): a árvore aceita ambos os formatos (fotos × coleção) no `DragOver` sintético, quando viável; senão, handler do VM + checklist manual.
Cobre do pedido original: 10, 11, 12 (drag) e reforço de 13/14/15/16.

### 5. Fechamento da frente (após a funcionalidade)
1. **Regressão completa**: todos os 16 testes do pedido mapeados no plano com status; `grep` final confirmando que não restou lógica de coleção por **nome** (`CollectionsText`, `Collections.Contains(nome)`, `INSERT … Collections(Name)`), exceto exibição derivada.
2. **Integridade:** teste/utilitário que varre o banco e prova **0 relações órfãs** e **0 ciclos**; confirma que `Photos` e arquivos não mudam em nenhuma operação de coleção.
3. **Desempenho:** teste de volume (≥ 5.000 fotos × ≥ 200 coleções em 4 níveis): árvore + contadores em tempo aceitável (registre os tempos), sem consulta por nó.
4. **Documentação consolidada:** `docs/COLLECTIONS_HIERARCHY_PLAN.md` com status final de cada item, `IMPLEMENTATION_STATUS.md`, `HANDOFF.md` (como estender: reordenação manual com `SortOrder`, coleções inteligentes — **não** implementar), `ARCHITECTURE.md`, `KNOWN_LIMITATIONS.md` (ex.: contador do pai ≠ soma dos filhos por decisão; expansão não persiste; sem reordenação manual), `MANUAL_TEST_CHECKLIST.md` (mouse real: arrastar coleção, raiz, ciclos bloqueados, convivência com arraste de fotos).
5. Remova código morto das pontes temporárias (C1) se ainda existir e confirme build sem avisos.

### Verificação
Build Debug+Release 0/0; `dotnet test` ≥ 5× seguidas; execução do app com dados isolados e captura (sem mouse real); migração de banco antigo reconfirmada ponta a ponta (flat → hierarquia → mover/excluir → sem órfãos).

**Pare e relate.** Esta é a última etapa da frente de coleções; **não** avance para StorageRoot/RelativePath/backup/upload/IA/similaridade.
