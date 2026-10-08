# Coleções e subcoleções — análise do pedido e decisões

> Leia este arquivo primeiro. Ele explica **o que o código faz hoje**, **onde o pedido original precisa de ajuste** e **quais decisões ficam valendo** nos prompts das etapas. Os arquivos `01_…` a `06_…` são prompts independentes: cole **um por vez** no Codex e só passe ao seguinte depois de revisar o relatório e escrever `CONTINUE`.

## 1. Ordem e por que há 6 arquivos (e não 5)

| Arquivo | Etapa | Entrega | Mexe em código? |
|---|---|---|---|
| `01_ETAPA_A_AUDITORIA_E_PLANO.md` | A | Auditoria + `docs/COLLECTIONS_HIERARCHY_PLAN.md` | **Não** (só documentação) |
| `02_ETAPA_B_TODAS_SEM_COLECAO_E_CONTADORES.md` | B | "Todas", "Sem coleção", contadores + correção de defeito do filtro | Sim, pequeno, sem schema |
| `03_ETAPA_C1_MODELO_MIGRACAO_E_SERVICO.md` | C (parte 1) | Identidade por Id, hierarquia, migração, serviço `ICollectionService`, ciclos | Sim, **sem UI** |
| `04_ETAPA_C2_ARVORE_E_CRUD.md` | C (parte 2) | TreeView, menu de contexto, CRUD, ajuste dos pontos de edição por nome | Sim, UI |
| `05_ETAPA_D_DRAG_FOTOS.md` | D | Arrastar fotos → coleção (adicionar/mover) | Sim |
| `06_ETAPA_E_DRAG_COLECOES_E_CICLOS.md` | E | Arrastar coleção → coleção/raiz, ciclos na UI, fechamento | Sim |

A etapa C do pedido original foi dividida em **C1 (dados/serviço)** e **C2 (UI)** porque é a de maior risco (migração de schema + troca de identidade nome→Id + árvore + CRUD). Separar permite validar a migração e as regras (ciclos, exclusão) com testes **antes** de existir tela.

## 2. O que o código faz hoje (verificado)

| Tema | Situação atual | Impacto |
|---|---|---|
| Identidade | Coleção é identificada **pelo nome** em todo lugar: `Photo.Collections` é `List<string>`; filtro (`PhotoFilter.Collection`), combo, sidebar, lote (`OrganizationBatch.CollectionsToAdd`), mesclagem de duplicatas e caixa de texto "Coleções (separadas por vírgula)" usam nome | Com hierarquia, **nomes deixam de ser únicos** ("Natal" em Família e em Trabalho). É preciso migrar a identidade para **Id** |
| Schema | `Collections(Id, Name TEXT NOT NULL UNIQUE)`; `PhotoCollections(PhotoId, CollectionId, PK(PhotoId, CollectionId))` | `UNIQUE(Name)` impede o mesmo nome em pais diferentes; remover uma constraint inline no SQLite exige **recriar a tabela** |
| Gravação | `SaveAsync` **apaga todas** as linhas de `PhotoTags` e `PhotoCollections` da foto e reinsere pelos nomes (`INSERT OR IGNORE INTO Collections(Name)` cria coleção por nome) | Não serve para arrastar/mover (apagaria associações de outras coleções e criaria coleções "fantasma"). Precisa de operações pontuais por Id |
| Integridade | **CORRIGIDO (erro da 1ª análise):** `SqliteCatalogRepository` abre toda conexão com `ForeignKeys = true` (`SqliteConnectionStringBuilder`, linha 11) → `PRAGMA foreign_keys=ON`; os `ON DELETE CASCADE` **disparam**. Confirme com um teste que lê `PRAGMA foreign_keys` | **Risco real na migração:** `DROP TABLE Collections` com FK ligada executa um `DELETE` implícito e o cascade **apaga todas as linhas de `PhotoCollections`**. A recriação deve rodar com `PRAGMA foreign_keys=OFF` (fora da transação, na mesma conexão), seguir os passos oficiais de "alterar tabela" do SQLite, executar `PRAGMA foreign_key_check` antes do `COMMIT` e religar a FK. Exclusões explícitas continuam boas (transação + teste de órfãos), mas o cascade também protege |
| Sidebar | `LibraryViewModel.CollectionEntries` é lista **plana** de `SidebarEntry` (chave = nome); já existe **"Sem coleção"** (`NoCollectionKey`, filtro `_noCollectionOnly`) | Etapa B já está **parcialmente feita**: falta **"Todas"** e revisar contadores |
| Contagem | Entrada de coleção = nº de fotos com aquele nome em `Photo.Collections` (já é "direto") | Regra 1 do pedido já vale; só precisa continuar valendo com Id e hierarquia |
| **Defeito** | `RefillChoices(CollectionChoices, CollectionEntries, …)` copia `entry.Key` de **todas** as entradas, inclusive "Sem coleção" → o combo de filtro mostra o texto interno `__sem_colecao__` e escolher isso não filtra nada | Corrigir na Etapa B |
| Filtros combinados | `PhotoFilter.Matches` usa nome; `MatchesNoCollection` é filtro separado | Para "Todas"/Id, criar filtros equivalentes sem quebrar `PhotoFilter` público |
| Telas que tocam coleções | Painel Organização (1 foto: caixa de texto; lote: caixa de texto), `ReviewView` (texto), duplicatas (mescla por nome), Microstock/Metadados (não) | Todos precisam migrar para Id/picker na C2 |
| Padrão a copiar | `FolderNode` + `Themes/Tree.xaml` + `FolderTree` na sidebar (árvore, expansão lembrada, seleção) | A árvore de coleções deve seguir o mesmo padrão |
| Testes | `LibraryViewTests`, `LibraryRegressionTests`, `SelectionAndOrganizationTests`, `UiSmokeTests` usam `CollectionsText`/nomes | Serão ajustados na C1/C2 (não apagar cobertura: migrar) |

## 3. Pontos do pedido que precisam de ajuste (e a solução adotada)

1. **"Etapa B" já tem metade pronta.** "Sem coleção" existe. A etapa B vira: adicionar "Todas", corrigir o combo, conferir/ajustar contadores e testes.
2. **Nomes únicos por pai, não globais.** O pedido quer subcoleções; o exemplo usa nomes distintos, mas a regra natural é *único entre irmãos* (sem diferenciar maiúsculas). Exige recriar `Collections` na migração.
3. **Mover/adicionar não pode usar `SaveAsync`.** Criar operações pontuais (`AddPhotos`, `RemovePhotos`, `MovePhotos`) por Id.
4. **Exclusão deve ser explícita e transacional** (apagar `PhotoCollections` das coleções removidas), mesmo com o cascade ativo (FK está ligada; ver correção na seção 2). Não desligue a FK globalmente; só a conexão de migração a desliga, de forma controlada.
5. **Promover filhos pode gerar conflito de nome** no nível de destino (já existe irmão com o mesmo nome). Regra: renomear o promovido com sufixo ` (2)`, ` (3)`… e informar no relatório da operação.
6. **Texto livre "Coleções, separadas por vírgula" deixa de fazer sentido** com hierarquia (qual "Natal"?). Troca por **seleção a partir da árvore** (picker). Isso é parte obrigatória da C2, não um extra.
7. **"Mover" precisa da coleção de origem.** Origem = coleção real selecionada na sidebar (não "Todas", não "Sem coleção", não pasta/tag etc.). Se ambígua: menu pergunta (ver decisão D6).
8. **Nome do item "Todas".** Fica como o pedido ("Todas") dentro da seção COLEÇÕES, com dica: "Fotos que pertencem a pelo menos uma coleção". Não confundir com "Todas as fotos" da seção BIBLIOTECA.
9. **Contador do pai ≠ soma dos filhos** (regra do pedido). Para evitar estranheza, mostrar dica (tooltip) "N fotos diretas; M em subcoleções" — opcional, sem alterar o número exibido.

## 4. Decisões vigentes (valem para todas as etapas)

| # | Decisão |
|---|---|
| D1 | **Identidade por Id.** `Photo` ganha `CollectionIds` (`List<long>`); `PhotoCollections` é a fonte da verdade. `Photo.Collections` (nomes) passa a ser **derivado só para exibição** (nome simples; no caso de homônimos, caminho "Pai / Filho"). Nenhuma regra nova pode depender de nome. |
| D2 | Nome **único entre irmãos**, sem diferenciar maiúsculas/acentos de caixa (`COLLATE NOCASE`), garantido no banco (índice único sobre `(COALESCE(ParentCollectionId, 0), Name COLLATE NOCASE)`) **e** no serviço (mensagem clara). |
| D3 | Migração por **recriação da tabela** `Collections` dentro de transação, preservando os Ids existentes (todas as atuais viram raiz). Idempotente. Teste com banco antigo (flat) e com dados em `PhotoCollections`. Antes de migrar um banco existente, copiar o arquivo `.db` para `photomanager.db.pre-colecoes.bak` (cópia simples, uma vez; não é a feature de backup do roadmap). |
| D4 | Exclusão **nunca** toca arquivos nem registros de `Photos`; apaga só `Collections` e `PhotoCollections` (explicitamente, em transação). Três modos: *somente esta (promover filhos)*, *esta e todas as subcoleções*, *cancelar*. |
| D5 | Ordem de exibição: alfabética (cultura atual) entre irmãos. `SortOrder` é gravado (para futuro) mas **não** há reordenação manual nesta rodada. |
| D6 | Soltar fotos: **arrastar = adicionar**; **Shift+arrastar = mover**; ao soltar sem Shift não há menu. Se for "mover" e a origem for ambígua (sem coleção real ativa), abrir um **menu ao soltar** com: *Adicionar à coleção*, *Mover de «X» para esta coleção* (uma entrada por coleção de origem comum às fotos, quando houver), *Cancelar*. O texto de feedback durante o arraste muda conforme Shift. |
| D7 | Profundidade máxima da árvore: **8 níveis** (protege a UI e valida entrada). |
| D8 | Cada foto pode estar em várias coleções; **uma coleção tem um único pai** (árvore, não grafo). |
| D9 | Itens virtuais ("Todas", "Sem coleção") ficam fixos no topo, **não** são coleções, não aceitam renomear/excluir/subcoleção/arrastar, e **não** aparecem como valor no combo de filtro nem no picker de destino (exceto como filtro próprio). |
| D10 | Expansão da árvore lembrada só em memória (como na árvore de pastas). |
| D11 | "Todas" conta **fotos distintas** com ≥ 1 relação (inclui fotos ausentes do disco: contagem é do catálogo). |
| D12 | Operações de coleção que alteram fotos atualizam **os mesmos objetos `Photo` em memória** e as contagens da sidebar sem recarregar o catálogo inteiro; não usar `SaveAsync` para isso. |

## 5. Riscos (consolidado)

| Risco | Etapa | Mitigação |
|---|---|---|
| Migração destrutiva de `Collections` | C1 | Cópia `.pre-colecoes.bak`, transação, idempotência, testes com banco antigo e com relações existentes, verificação de contagens antes/depois |
| Regressão por troca nome→Id (filtros, lote, duplicatas, revisão, testes) | C1/C2 | Inventário por `grep` de `Collections`/`CollectionsText`; migrar cada uso; manter testes (adaptados, não removidos) |
| `SaveAsync` sobrescrevendo associações feitas por arraste | C1 | `SaveAsync` deixa de gravar coleções (ou grava só se a lista de Ids mudou); operações pontuais por Id; teste de concorrência lógica (salvar organização depois de arrastar não desfaz a coleção) |
| **Cascade apagando `PhotoCollections` ao recriar `Collections`** (FK está ligada) | C1 | `PRAGMA foreign_keys=OFF` na conexão de migração, `foreign_key_check` antes do commit, teste com relações reais, `.bak` |
| Órfãos em `PhotoCollections` | C1 | Exclusões explícitas; rotina de checagem em teste |
| Ciclos | C1/E | Validação no serviço (fonte da verdade) + feedback na UI; testes de todos os casos (A→A, A↔B, A→B→C→A) |
| Arrastar com seleção múltipla + virtualização + ListBox Extended | D | Início de arraste por limiar de movimento; regra "arrastar item selecionado = grupo"; não conflitar com clique/Ctrl/Shift de seleção |
| Feedback de arraste e Shift em WPF | D/E | `DragEventArgs.KeyStates`, adorner/popup de texto, teste do planejador puro + checklist manual com mouse real |
| Performance (milhares de fotos × contadores) | B/C | Contadores derivados em memória de `_all` numa passada; sem consultas por nó |
| Concorrência de edição (painel Organização tem rascunho em memória) | C2 | Painel passa a refletir `CollectionIds` atuais; salvar organização não regrava coleções |

## 6. Arquivos prováveis (visão geral)

`Domain/Photos/Photo.cs`; `Application/Catalog/*` (novo `Collections/` com modelos e `ICollectionService`, `OrganizationBatch`, `PhotoFilter`/filtros, `IOrganizationRepository`); `Persistence/SqliteCatalogRepository.cs` (schema, migração, consultas, operações); `Wpf/Views/LibraryViewModel.cs`, `LibraryView.xaml(.cs)`, novos `CollectionNode`, diálogos, `Themes/Tree.xaml`; `Wpf/Views/ReviewView*`, `PageViewModels.cs` (duplicatas); testes em `tests/PhotoManager.Tests`.

## 7. Como usar

1. Cole `01_…` no Codex → revise `docs/COLLECTIONS_HIERARCHY_PLAN.md` → ajuste decisões se discordar (edite este arquivo antes) → `CONTINUE`.
2. Repita para `02_…` a `06_…`.
3. Em cada etapa o Codex deve **parar** e relatar; não deixe avançar sozinho.
4. Se discordar de uma decisão D1–D12, altere-a **aqui** antes de rodar a etapa que depende dela (cada prompt manda o Codex reler este arquivo).
