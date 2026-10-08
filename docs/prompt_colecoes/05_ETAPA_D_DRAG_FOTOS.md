# PHOTOMANAGER — COLEÇÕES E SUBCOLEÇÕES · ETAPA D (arrastar fotos → coleção: adicionar e mover)

## CONTEXTO COMUM

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Fases 1–10, rodada de UX e Etapas A, B, C1 e C2 de coleções concluídas (árvore, CRUD, `ICollectionService`, identidade por Id). Releia antes: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/COLLECTIONS_HIERARCHY_PLAN.md` e **`docs/prompt_colecoes/00_ANALISE_E_DECISOES.md`** (D1–D12 — em especial **D6** sobre Shift/menu e origem do "mover").

**Regras inegociáveis:** coleções são **virtuais** (arrastar **nunca** move/copia arquivo físico nem apaga `Photos`); **não** mexer em StorageRoot, RelativePath, backup, restauração, upload, similaridade, IA; sem mocks; **sem regra de negócio em `.xaml.cs`**; build Debug e Release **0/0**; `dotnet test` verde **≥ 5× seguidas, uma execução por vez** (nunca em paralelo nem durante build; feche `PhotoManager.exe`); testes de janela com `[Collection("WpfUi")]`; `PM_SNAPSHOT_DIR` para captura; **nunca simular mouse/teclado reais sobre a tela do usuário** — verificação por testes e eventos sintéticos; docs com o que **não** foi verificado. **Pare ao fim e relate.**

---

## ESTA ETAPA — D: arrastar fotos para coleções

### 1. Comportamento (D6)
- **Arrastar** foto(s) da grade para um nó de coleção real = **ADICIONAR** (cria `PhotoCollection`; mantém as demais coleções).
- **Shift + arrastar** = **MOVER**: remove da **coleção de origem ativa** e adiciona ao destino. Se Shift não estiver pressionado, nunca remove nada.
- **Origem do mover:** a coleção real **selecionada na sidebar** (não vale "Todas", "Sem coleção", pasta, tag, categoria, busca etc.). Exemplo: vendo "Viagens" e arrastando para "Roraima 2026" com Shift → remove de Viagens, adiciona em Roraima 2026. **Não** remova de outras coleções.
- **Origem ambígua** (sem coleção real ativa): ao soltar com Shift, abrir **menu ao soltar** (pequeno, junto ao cursor ou diálogo leve): *Adicionar à coleção* · *Mover de «X» para «Destino»* (uma linha por coleção que **todas** as fotos arrastadas têm em comum, se houver) · *Cancelar*. Se não houver coleção de origem comum, só *Adicionar* e *Cancelar*.
- Soltar em coleção em que a foto **já está**: não duplicar (PK única + serviço); feedback "Já pertence à coleção" (e, se algumas já pertenciam, resumir: "2 adicionadas, 1 já pertencia").
- Destinos **inválidos:** "Todas", "Sem coleção" (virtuais), pastas/tags/categorias, área vazia — cursor de bloqueio e sem ação. Soltar na própria coleção de origem num "mover" = no-op informativo.
- Resultado aplicado via `ICollectionService.AddPhotosAsync/MovePhotosAsync` (transação, sem `SaveAsync`); atualizar `Photo.CollectionIds`/`Collections` das fotos afetadas **em memória**, as contagens da sidebar e, se a coleção ativa perdeu fotos (mover), a grade (as fotos movidas saem da visão atual, sem perder o restante da seleção).
- Mensagem de status após a operação ("3 fotos adicionadas a «Viagens»", "3 fotos movidas de «Viagens» para «Roraima 2026»").

### 2. Seleção múltipla (regra do pedido)
- Arrastar **uma foto selecionada** → arrasta **todas as selecionadas** (checkbox/Ctrl/Shift da grade; ver Etapa 2 da rodada de UX).
- Arrastar **uma foto não selecionada** → só ela (não altera a seleção atual; opcionalmente mostra que é uma só).
- Não quebrar clique simples, Ctrl+clique, Shift+clique, caixa de seleção, duplo clique (abre revisão) nem o coração: o início do arraste só ocorre após **limiar de movimento** (use `SystemParameters.MinimumHorizontalDragDistance`/`Vertical`) com o botão esquerdo pressionado, e **não** inicia a partir de `ButtonBase` (caixa de seleção/coração).
- Respeitar virtualização (`VirtualizingWrapPanel`): o payload carrega **Ids**, não elementos visuais.

### 3. Feedback visual
- Durante o arraste: texto flutuante (popup/adorner leve, sem travar) com a ação **atual**: `+ Adicionar 3 fotos a “Viagens”` (sem Shift) ou `→ Mover 3 fotos para “Viagens”` (com Shift), que **muda ao pressionar/soltar Shift**; destino inválido: ícone/cursor de bloqueio e texto do motivo.
- **Destino válido destacado** (nó da árvore com realce do tema, borda/fundo azul); sair do nó remove o realce.
- **Auto-expandir** nó recolhido após ~700 ms sobre ele; **auto-rolar** a sidebar perto das bordas.
- Contador de fotos no payload (n) visível no feedback.

### 4. Arquitetura (testável)
- **`DropPlanner` (função pura, Application ou Wpf/ViewModels):** entrada `{ photoIds, activeSourceCollectionId?, targetNode (real/virtual/nenhum), shiftPressed, photosCurrentCollectionIds }` → saída `DropPlan { Action = Add | Move(from) | AskMenu(options) | Blocked(reason) | NoOp(reason), Message }`. Toda a decisão (inclusive textos de feedback) vem dele.
- Formato de dados do arraste: `DataObject` com formato próprio (ex.: `PhotoManager.PhotoIds` = `long[]`) + origem; nunca expor objetos de UI.
- `LibraryViewModel` expõe `ExecuteDropAsync(plan, …)`; o code-behind só inicia `DragDrop.DoDragDrop`, repassa `DragOver/Drop` ao planejador/VM e mostra o menu.
- Permitir **drop também no cabeçalho "COLEÇÕES"?** **Não** nesta etapa (raiz é só para mover coleções, Etapa E): para fotos é destino inválido.

### 5. Testes
- `DropPlanner`: adicionar (sem Shift); mover (Shift + origem ativa); origem ambígua → menu com opções corretas (interseção das coleções de origem); destino virtual → bloqueado; já pertence → NoOp/mensagem; mover para a própria origem → no-op; mensagens/contagens (singular/plural); múltiplas fotos com estados mistos (algumas já pertencem).
- Serviço/VM: adicionar não duplica; mover remove **só** da origem e mantém outras coleções; transação (falha = nada muda); contadores da sidebar atualizados; grade atualizada ao mover; "Salvar organização" depois não desfaz.
- Seleção: regra "item selecionado arrasta todas / item não selecionado arrasta uma" (função pura que resolve o payload a partir de seleção + item de origem).
- Interface real (STA): tentativa de arraste **sintética** (eventos `PreviewMouseMove` + `DragEventArgs` construídos) apenas se viável; caso contrário, teste o handler do VM e registre no checklist o que depende de mouse real (arraste de verdade, Shift, auto-expandir, cursor).
Cobre do pedido original: 6, 7, 8 (UI) e seleção múltipla.

### Verificação e documentação
Build Debug+Release 0/0; `dotnet test` ≥ 5× seguidas; execução do app com dados isolados e captura (sem mouse real). Atualize `IMPLEMENTATION_STATUS.md`, `HANDOFF.md` (formato de dados do arraste, `DropPlanner`), `ARCHITECTURE.md`, `MANUAL_TEST_CHECKLIST.md` (lista **detalhada** para mouse real: arrastar 1 e N fotos, Shift, menu de origem ambígua, "já pertence", destino inválido, auto-expandir, convivência com clique/Ctrl/Shift/duplo clique), `KNOWN_LIMITATIONS.md`.

**Pare e relate.** Não inicie a Etapa E.
