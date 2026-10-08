# PHOTOMANAGER — COLEÇÕES E SUBCOLEÇÕES · ETAPA C2 (árvore na sidebar e CRUD)

## CONTEXTO COMUM

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Fases 1–10, rodada de UX e Etapas A, B e C1 de coleções concluídas (hierarquia, `ICollectionService`, identidade por Id). Releia antes: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/COLLECTIONS_HIERARCHY_PLAN.md` e **`docs/prompt_colecoes/00_ANALISE_E_DECISOES.md`** (D1–D12).

**Regras inegociáveis:** coleções são **virtuais** (nunca tocar arquivos físicos nem excluir `Photos`); **não** mexer em StorageRoot, RelativePath, backup, restauração, upload, similaridade, IA; sem mocks; **sem regra de negócio em `.xaml.cs`** (code-behind só para diálogos/foco/eventos visuais); build Debug e Release **0/0**; `dotnet test` verde **≥ 5× seguidas, uma execução por vez** (nunca em paralelo nem durante build; feche `PhotoManager.exe`); testes de janela com `[Collection("WpfUi")]`; `ComboBox` usa `ItemTemplate`; temas em `App.xaml`; `PM_SNAPSHOT_DIR` para captura; docs com o que **não** foi verificado. **Pare ao fim e relate.**

---

## ESTA ETAPA — C2: TreeView de coleções + CRUD (sem drag-and-drop ainda)

### 1. Árvore na sidebar
- Seção **COLEÇÕES** vira um `TreeView` real (siga o padrão de `FolderNode`, `Themes/Tree.xaml` e o `FolderTree`): `CollectionNode` (Id, Name, ParentId, `DirectCount`, `Children`, `IsExpanded`, `IsSelected`, caminho).
- Topo fixo (fora da árvore ou como nós virtuais não editáveis, D9): **Todas** e **Sem coleção** (da Etapa B), depois as coleções reais em ordem alfabética entre irmãos (D5).
- **Contador de cada nó = só fotos diretamente associadas** (não soma descendentes); tooltip do nó: "N fotos diretas · M em subcoleções" (opcional, não muda o número exibido).
- **Selecionar** uma coleção real filtra por **Id**, mostrando **somente fotos diretas** (a coleção pai **não** inclui as das filhas — regra 1 do pedido). Selecionar nó limpa outros filtros (como as demais entradas); destaque do item ativo.
- Expansão/recolhimento lembrados em memória (D10); novas coleções expandem o pai.
- Cabeçalho "COLEÇÕES" com botão **[ + ]** (Nova coleção na raiz).
- Atualização em tempo real das contagens e da árvore após criar/renomear/mover/excluir e após alterações nas fotos (mesmo mecanismo da B; sem recarregar o catálogo todo).

### 2. Menu de contexto (itens virtuais **sem** menu de edição, D9)
Em coleção real: **Nova subcoleção…**, **Renomear…**, **Mover para…** (submenu/diálogo com a árvore, incluindo "(raiz)"; destinos inválidos — a própria, descendentes — desabilitados com motivo), **Excluir…**. No cabeçalho/área vazia: **Nova coleção**. Teclado: F2 renomear, Delete excluir, setas/expandir padrão do TreeView.

### 3. Diálogos (janelas WPF simples, estilo do app; sem regra de negócio no code-behind)
- **Nome** (nova/subcoleção/renomear): validação em tempo real (vazio, duplicado entre irmãos, tamanho) com mensagem; botão desabilitado se inválido; foco no campo; Enter/Esc.
- **Excluir sem subcoleções:** confirmação: "Excluir a coleção «X»? As N fotos **não** serão excluídas; apenas deixam de pertencer a ela." 
- **Excluir com subcoleções:** diálogo com as opções do pedido — (1) *Excluir somente esta e mover as subcoleções para o nível acima*; (2) *Excluir esta e todas as subcoleções*; (3) *Cancelar* — com resumo do impacto (quantas subcoleções, quantas relações serão removidas) e a frase **"Nenhum arquivo será excluído."** Se a promoção renomear alguma coleção (conflito), informar no resultado.
- Mensagens de erro do serviço (ciclo/profundidade/nome) exibidas de forma clara, sem exceções vazando.

### 4. Pontos que ainda editam coleção por nome (D1) — migrar agora e remover as pontes da C1
1. **Painel Organização (1 foto):** trocar a caixa "Coleções (separadas por vírgula)" por **chips das coleções da foto** (×) + seletor **"Adicionar à coleção ▾"** com a árvore (caminhos "Pai / Filho"). Remover/adicionar usam `ICollectionService` (imediato, sem depender do botão "Salvar organização"), e a UI reflete `CollectionIds` atuais.
2. **Painel Organização (lote):** campo "Adicionar às coleções" vira seletor de **uma ou várias coleções** da árvore; `OrganizationBatch.CollectionsToAdd` passa a **Ids** (ajuste `ApplyTo`, impacto em texto e testes).
3. **Filtro "Coleção"** da barra de filtros: lista coleções reais com caminho; padrão "Qualquer" (decisão da B).
4. **ReviewView / Informações:** mostrar caminhos das coleções (somente leitura).
5. **Duplicatas — mesclagem:** unir `CollectionIds` (distintos) da cópia excluída na cópia mantida (pelo serviço), não por nome.
6. Remover `CollectionsText`/parsing por nome e a criação de coleção por texto; coleções só nascem pelo CRUD.

### 5. Testes
- ViewModel: construção da árvore (pai/filhos/ordem/contagens diretas), seleção filtra só diretas (pai com 5 diretas e filhas 8 e 11 mostra **5**, contador 5), Todas/Sem coleção continuam corretos, CRUD refletido na árvore e nas contagens, excluir com os dois modos, expansão lembrada, itens virtuais sem comandos de edição.
- Painel Organização: adicionar/remover coleção da foto reflete na árvore e **não** é desfeito por "Salvar organização"; lote por Ids; duplicatas mesclam por Id.
- Interface real (STA): a seção COLEÇÕES renderiza a árvore com Todas/Sem coleção no topo e a hierarquia; nenhum `ControlTemplate` quebrado (`ThemeControlTemplates_Instantiate`); diálogos carregam (smoke). Captura `PM_SNAPSHOT_DIR`.
- Regressão: todos os testes da C1 e anteriores continuam verdes (migre os que usavam nomes, sem apagar cobertura).
Cobre do pedido original: 1, 2, 9, 12–14 (UI), 16 e CRUD.

### Verificação e documentação
Build Debug+Release 0/0; `dotnet test` ≥ 5× seguidas; execução real do app com dados isolados criando hierarquia (Viagens › Roraima 2026, Nordeste 2026; Família › Natal, Aniversários; Trabalho) e conferindo contagens e filtro por captura. Atualize `IMPLEMENTATION_STATUS.md`, `HANDOFF.md`, `ARCHITECTURE.md`, `MANUAL_TEST_CHECKLIST.md` (criar/renomear/excluir com os 3 caminhos, mover, contadores, painel Organização), `KNOWN_LIMITATIONS.md` e `UI_REFACTOR_PLAN.md` se pertinente.

**Pare e relate.** Não inicie a Etapa D (arrastar).
