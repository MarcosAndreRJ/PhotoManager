# Handoff — PhotoManager

## Estado atual

Fases 1–10, rodada de UX e Frente de Coleções Completa (Etapas A, B, C1, C2, D e E) concluídas, compilando e testadas (188 testes verdes em 5 passes consecutivos). A especificação de Coleções está em `docs/prompt_colecoes/` e o plano mestre em `docs/COLLECTIONS_HIERARCHY_PLAN.md`.

## Ambiente

.NET SDK 10.0.401 (Windows). Comandos:

```text
dotnet restore
dotnet build
dotnet test
```

Para rodar com dados isolados (sem tocar em `%LocalAppData%\PhotoManager`), defina a variável de ambiente `PHOTOMANAGER_ROOT` com uma pasta; banco, cache e logs ficam lá.

## Estrutura

```text
PhotoManager.sln
src/  Domain · Application · Infrastructure · Persistence · Wpf
tests/PhotoManager.Tests   (xUnit, net10.0-windows)
docs/
```

Arquivos centrais da UI: `Wpf/App.xaml`, `Wpf/Themes/*`, `Wpf/MainWindow.xaml`, `Wpf/ViewModels/MainViewModel.cs`, `Wpf/Views/LibraryView.xaml(.cs)`, `Wpf/Views/LibraryViewModel.cs`, `Wpf/Controls/*`.

## Regras aprendidas (importantes para as próximas fases)

1. **Tema:** todos os `ResourceDictionary` são mesclados em `App.xaml`, nunca aninhados. `StaticResource` dentro de conteúdo de `ControlTemplate` não enxerga dicionários irmãos aninhados (erro só aparece em runtime). `UiSmokeTests` detecta isso.
2. **Estilos implícitos** de controles com template (ComboBox, CheckBox) precisam de `BasedOn="{StaticResource {x:Type ...}}"`, senão perdem o template padrão.
3. **`Foreground` no `TabItem` vaza** para o conteúdo da aba; a cor do cabeçalho é aplicada no `ContentPresenter` do cabeçalho.
4. Em dicionários, declare estilos **antes** dos templates que os referenciam.
5. `UseWPF` remove `System.IO` dos implicit usings.
6. A `LibraryViewModel` é única por sessão (criada em `MainViewModel`). Alterações em dados vêm do banco só em `ReloadAsync`; filtros e sidebar operam em memória sobre `_all`.
7. Mutações de coleções ligadas à UI devem ocorrer na thread de UI (as continuações de `await` voltam ao Dispatcher no app real; os testes instalam um `DispatcherSynchronizationContext`).
8. Mover/renomear usam retentativa porque preview/miniatura podem estar lendo o arquivo.
9. **Coleções e Foreign Keys:** `SqliteCatalogRepository` tem `ForeignKeys = true` por padrão. Toda migração de DDL com drop/recriação de `Collections` DEVE usar conexão com `ForeignKeys = false`, recriar/trocar tabelas, rodar `PRAGMA foreign_key_check` e validar zero órfãos antes do commit.
10. **Identidade por Id de Coleções:** A filtragem na UI e no repositório opera por `CollectionId`. Em `SidebarEntry`, `Id` armazena o id numérico enquanto `Key` preserva o nome para compatibilidade com a UI plana.
11. **Drag-and-Drop de Fotos (Etapa D):**
    - Payload padronizado via `DataObject` no formato `PhotoManager.Application.Collections.CollectionDragDropFormats.PhotoIds` (`"PhotoManager.PhotoIds"`) carregando `long[]`. Nunca transfere referências a controles WPF.
    - Decisão 100% pura isolada em `DropPlanner.Plan(DropPlanRequest)`: define ações `Add`, `Move`, `AskMenu`, `Blocked` e `NoOp`, além de mensagens formatadas.
    - Seleção múltipla resolvida por `PhotoSelectionDragHelper`: clicar em foto selecionada arrasta todas as fotos marcadas na grade; clicar em foto não selecionada arrasta apenas ela sem desmarcar as demais.
    - Início do arraste condicionado a `SystemParameters.MinimumHorizontalDragDistance` / `VerticalDragDistance` com botão esquerdo e ignorado se clicado sobre `ButtonBase` (checkbox do card ou coração de favorito).
    - Feedback flutuante em tempo real via `DragFeedbackPopup` com texto dinâmico reagindo à tecla Shift.
    - Auto-expansão de nós recolhidos após 700 ms e auto-rolagem suave da sidebar perto das bordas.
12. **Drag-and-Drop de Coleções e Prevenção de Ciclos (Etapa E):**
    - Payload padronizado via `DataObject` no formato `PhotoManager.Application.Collections.CollectionDragDropFormats.CollectionId` (`"PhotoManager.CollectionId"`) carregando `long` (Id da coleção arrastada).
    - Decisão 100% pura isolada em `CollectionDropPlanner.Plan(CollectionDropPlanRequest)` operando sobre dicionário em memória `(Name, ParentId)` sem fazer I/O síncrono no `DragOver`.
    - Arrastar sobre outra coleção move a subárvore (`ParentCollectionId = targetId`).
    - Arrastar sobre o cabeçalho "COLEÇÕES" move para a raiz (`ParentCollectionId = null`).
    - Prevenção de ciclos: validação na UI em memória (sobe a cadeia do alvo até a raiz; se encontrar o Id arrastado, bloqueia) e validação transacional defensiva no `ICollectionService.MoveAsync` (nunca confia só na UI).
    - Detecção de irmãos homônimos: bloqueia se o destino já contiver irmão com o mesmo nome (`COLLATE NOCASE`), informando o nome conflitante no feedback visual.
    - Limite de profundidade: soma a profundidade do caminho do destino com a altura da subárvore arrastada; bloqueia se ultrapassar 8 níveis.
    - Convivência de múltiplos formatos: o mesmo `TreeView` gerencia simultaneamente payloads de fotos (`PhotoIds`) e de coleções (`CollectionId`), inspecionando `e.Data.GetDataPresent`. O cabeçalho raiz só aceita coleções (rejeita fotos).

## Pontos de Extensão Futura (Não implementar nesta fase)

1. **Reordenação manual entre irmãs com `SortOrder`:**
   - A coluna `SortOrder INTEGER NOT NULL DEFAULT 0` já existe na tabela `Collections`.
   - Para suportar reordenação arbitrária (ex.: arrastar entre dois nós irmãos com linha de inserção antes/depois), estender `CollectionDropPlanner` com `DropPosition.Before / After / Inside` e atualizar os valores de `SortOrder` das irmãs no `CollectionRepository`.
2. **Coleções Inteligentes (Smart Collections):**
   - Coleções virtuais dinâmicas baseadas em filtros salvos (ex.: `Rating >= 4 AND IsFavorite = 1`).
   - O schema pode introduzir uma tabela `SmartCollections (Id, Name, RuleJson)` ou flag `IsSmart` em `Collections` sem alterar o modelo de coleções manuais.

## API pública da `LibraryViewModel` (preservada das fases 2–4)

`ImportFolderAsync`, `ApplyFiltersAsync` (recarrega do banco), `SaveSelectedAsync`, `ApplyBatchAsync`, `MoveSelectedAsync`, `CopySelectedAsync`, `RenameSelectedAsync`, `RenameBatchAsync`, `RecycleSelectedAsync`, propriedades `Photos`, `SelectedPhoto`, `SearchText`, `CategoryFilter`, `TagFilter`, `CollectionFilter`, `FavoritesOnly`, `StatusText`, `IsBusy`.
Novas: `ApplyFilters()` (em memória), `ToggleFavoriteAsync`, `SelectSidebarCommand`, `PreviousCommand`/`NextCommand`, `ClearFiltersCommand`, `PreviewImage`, `SmartLists`/`Folders`/`CategoryEntries`/`TagEntries`/`CollectionEntries`, `*Choice`, `PlanDrop`, `ExecuteDropPlanAsync`.

## Packages

`Microsoft.Extensions.DependencyInjection` 10.0.0 · `Microsoft.Extensions.Logging(.Abstractions)` 10.0.0 · `Microsoft.Data.Sqlite` 10.0.12 · testes: xunit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1.

## Próximos passos

1. **Frente Coleções e Subcoleções:** Etapas A (Auditoria e Plano), B ("Todas", "Sem coleção", contadores), C1 (Modelo, Migração e Serviço), C2 (Árvore e CRUD na UI) e D (Drag-and-Drop de Fotos: adicionar, mover com Shift, desambiguação e feedback) concluídas e testadas (171 testes verdes no total).
2. **Próxima etapa:** Etapa E (Drag-and-Drop de Coleções, reorganização da árvore, prevenção de ciclos no arraste e fechamento) aguarda autorização explícita (`CONTINUE`).
3. Nenhuma fase ou etapa deve começar sem autorização explícita.

## Riscos

- WEBP depende do codec do Windows.
- Filtro por texto/campos opera em memória; para dezenas de milhares de fotos, mover para SQL.
- Renomeação em lote não tem pré-visualização nem rollback automático.
- `ImportFolderAsync` roda na thread chamadora até o primeiro `await`; para pastas enormes, considerar `Task.Run` no enumerador.

## Fase 5 — notas para a Fase 6

- `MetadataViewModel` (único por sessão) depende da `LibraryViewModel` (seleção, preview, anterior/próxima) e do `IMetadataReader`; `MainViewModel` chama `SetActive(true/false)` ao navegar.
- `PhotoMetadata` é um record imutável; a edição deve criar um modelo separado (campos editáveis + contadores) e reler após salvar.
- A tela atual usa `TextBox` somente leitura e chips estáticos; a edição reaproveita o mesmo layout (foto à esquerda, painel à direita) trocando para controles editáveis e chips com "x".
- Ao escrever no arquivo, a `MetadataExtractorReader` abre com `FileShare.ReadWrite|Delete`, mas preview/miniatura também leem o arquivo: use a mesma retentativa do `FileOperationService` e invalide miniatura/preview.

## Fase 6 — notas para a Fase 7

- `MetadataEdit` (Application) é o modelo editável normalizado; `SameAs`/`ChangedFields` decidem se há mudança. Limites (200/2000/50/200/200) são só avisos visuais, não bloqueiam.
- `IMetadataEditService.SaveAsync` é seguro para uso repetido (lote): não faz nada se não há mudança, versiona por foto e lança exceção sem alterar o arquivo em caso de falha. Em lote, capture a exceção por foto.
- `JpegMetadataWriter` só trata `.jpg/.jpeg`. Para PNG/WEBP seria preciso outro `IMetadataWriter`.
- Apagar um campo cujo valor também existe no EXIF (Artist, Copyright, ImageDescription, XP*) faz o valor reaparecer na leitura (o EXIF não é alterado).
- Se o IPTC existente declarar charset diferente de UTF-8, os datasets não gerenciados (cidade etc.) são preservados byte a byte, mas o 1:90 é regravado como UTF-8. Raro.
- `MetadataViewModel.ConfirmDiscard` é definido pela View; o "clipboard" de metadados é estático (sessão).
- Tarefas longas (lote) devem usar `IsBusy`/progresso; o escritor já roda em `Task.Run`.
- Arquivos de teste reais: `docs/Fotos/*.CR2` (3 RAW Canon). Servem para validar leitura; não são catalogáveis hoje.

## Fase 7 — notas para as próximas fases

- **Lote:** `BatchMetadataPlan.Apply` é uma função pura (testável sem arquivos); `IBatchMetadataService` usa `IMetadataEditService` por foto. Novos recursos que gravam em várias fotos devem seguir o mesmo padrão: pré-visualizar → confirmar → aplicar com progresso/cancelamento → resultado por foto.
- **Presets** estão no SQLite (`MetadataPresets`, nome único sem diferenciar maiúsculas). A fase 8 pode reaproveitar `IMetadataPresetRepository` ou criar perfis de validação próprios (não misturar com presets de metadados).
- **RAW:** `ImageFormats` (Application) é a fonte única de formatos aceitos; `ImageLoader`/`RawPreview` (Infrastructure) abrem imagens. Qualquer código novo que precise decodificar uma foto deve usar `ImageLoader` (não `BitmapDecoder` direto), senão RAW quebra.
- **Árvore de pastas:** `FolderNode.Build` recebe as fotos e devolve as raízes; o filtro de pasta inclui subpastas (`IsInFolder`). Reconstruída a cada `ReloadAsync`.
- **ComboBox** tem template próprio: use `ItemTemplate` (não `DisplayMemberPath`) para itens que não são texto.
- **Teste de interface:** `PM_SNAPSHOT_DIR=<pasta> dotnet test --filter BatchWindow_Loads` grava um PNG da janela de lote (útil para inspeção visual sem depender de captura de tela).
- Mutações de UI feitas por callbacks `Progress<T>` podem chegar fora de ordem em testes sem `SynchronizationContext`; proteja mensagens finais (ver `BatchMetadataViewModel.ApplyAsync`).

## Fase 8/9 — notas para a continuidade

- `ValidationProfileValidator.Validate` e `PreparationStatusCalculator.Calculate` são funções puras e devem continuar sendo a fonte dos estados da Central.
- `UploadRecordSnapshot` continua sendo o contrato puro do cálculo; a Fase 9 o alimenta pelo histórico persistido via `IUploadHistoryService`.
- `Agencies` e `UploadRecords` são criadas com `CREATE TABLE IF NOT EXISTS`; o seed de cinco agências é idempotente. O histórico é append-only e o estado atual usa o maior `UploadRecords.Id` por foto/agência.
- A Fase 9 só registra ações manuais. Não há login, credenciais, rede, fila ou tentativa de upload.
- `MicrostockViewModel` mantém seleção múltipla, filtros por agência/estado, confirmação via `ConfirmAction` e histórico da foto; o code-behind só conecta seleção de `DataGrid` e confirmação WPF.
- A tela Microstock é criada uma vez por sessão, compartilha a mesma `LibraryViewModel` e chama `SetActive` ao voltar à aba para recarregar após edições de metadata.
- O cache de leitura é de sessão e usa `PhotoId`, `MetadataVersion`, `FileSize` e `ModifiedAt`; não criar uma segunda fonte persistida sem necessidade.
- Na validação da Fase 8, o bootstrap do WPF foi corrigido para aguardar `InitializeAsync` sem bloquear o Dispatcher. A captura visual nativa deve ser repetida em uma sessão com superfície Windows disponível.

## Fase 10 — notas para continuidade

- `DuplicateDetectionService` agrupa primeiro por tamanho e só abre arquivos em grupos de tamanho repetido; o hash é SHA-256 em streaming.
- `Photos.ContentHash`, `HashedAtSize` e `HashedAtModified` formam o cache persistido. `RecordVersionAsync` limpa esses campos para forçar novo hash após edição de metadata.
- `DuplicateIgnores` persiste grupos ignorados por hash. A smart list `duplicates` usa somente hashes já calculados e não dispara leitura de arquivos.
- A tela Ferramentas exige uma cópia marcada como mantida antes de mover ou enviar outra para a Lixeira. A mesclagem de tags, coleções, nota e avaliação é opcional e explícita.
- A busca pode ser cancelada entre arquivos e retoma hashes salvos; falhas individuais entram em avisos e não interrompem os demais arquivos.

## Nova etapa de organização — ETAPA 2 concluída

- **Sem coleção** é uma entrada virtual da barra lateral (`LibraryViewModel.NoCollectionKey`); não cria registro no banco. O filtro mostra as fotos cuja coleção está vazia, e a contagem acompanha alterações em memória após salvar.
- O ícone próprio da aplicação está em `src/PhotoManager.Wpf/Assets/PhotoManager.ico`, configurado no `.csproj` e na janela principal. O arquivo atual é um ícone inicial de 32×32; pode ser substituído por um pacote multi-resolução sem mudar o contrato da UI.
- A auditoria e o plano das próximas etapas estão em `docs/DRAG_DROP_AND_FOLDERS_PLAN.md`; o plano de subcoleções está em `docs/SUBCOLLECTIONS_PLAN.md`.
- **Não iniciar ainda:** ETAPA 3 (serviço e CRUD de pastas), drag-and-drop, integração de entrada/saída com o Explorer e subcoleções. Tudo isso aguarda autorização explícita.
- Validação desta entrega: build Debug, teste de smoke da janela WPF, 119 testes automatizados e build Release, todos sem erros.

## Rodada de UX pós-Fase 10 — notas (Etapa 2)

- A seleção vive no `ListBox`; a `LibraryViewModel` recebe cópias via `UpdateSelection(...)` (code-behind) e deriva painel/contadores. Qualquer ação em lote deve usar `SelectedCards`, não o `SelectedPhoto` (que é só a "foto primária").
- Depois de `ApplyView()` a VM dispara `SelectionRestoreRequested`; a View remarca. Testes sem View podem chamar `UpdateSelection` direto.
- `OrganizationBatch` (Application) é a regra pura de organização em lote; novos tipos de ação em lote devem seguir: plano puro → impacto em texto → aplicar → remarcar seleção.
- Mover/Copiar/Excluir/Renomear agora vivem na aba *Arquivos*; a barra de ferramentas só tem Adicionar pasta e seleção.
- Próximas etapas pedidas: **Etapa 3** (navegação de preview estilo mockup) e **Etapa 4** (metadados/lote estilo Xpiks). Ver `UI_REFACTOR_PLAN.md`.

## Rodada de UX pós-Fase 10 — notas (Etapa 3)

- **Modo de revisão** é um estado da `LibraryViewModel` (`IsReviewMode`, `EnterReview/ExitReview`, `IsFullScreen`); a `LibraryView` hospeda a `ReviewView` (DataContext = `ReviewViewModel`, criada no `MainViewModel` e atribuída a `Library.Review`). Lista, seleção e navegação continuam sendo da Biblioteca; o filmstrip é um segundo `ListBox` sobre `Library.Photos` com `SelectedItem` bidirecional em `SelectedPhoto`.
- **Imagem em duas etapas:** `Library.PreviewImage` (1600 px) aparece na hora; `ReviewViewModel.DisplayImage` troca pela resolução natural (`ImageLoader.LoadFull`) depois de ~220 ms parado. Qualquer nova leitura de imagem deve passar por `ImageLoader`.
- **`ZoomViewer`** calcula tudo em DIPs com o DPI do próprio controle; "Ajustar" nunca amplia; trocar de foto (outra proporção) volta a Ajustar, trocar prévia→completa (mesma proporção) preserva o enquadramento.
- **Tela cheia:** `MainViewModel.IsFullScreen/ShowNavigation` espelham a Biblioteca; `MainWindow.xaml.cs` cuida de `WindowStyle/WindowState`. Esc: sai da tela cheia; novo Esc: sai da revisão.
- **Testes de janela em série:** classes que criam janelas WPF usam `[Collection("WpfUi")]`. Não rode duas execuções de `dotnet test` ao mesmo tempo nem durante um build: a suíte tem esperas com limite e falha sob carga forte (aconteceu uma vez nesta etapa).
- Para capturas sem tocar na tela do usuário: scripts de UI Automation + `PrintWindow` (nunca mouse/teclado reais); botões só com ícone precisam de `AutomationProperties.Name`.
- Próxima: **Etapa 4** (metadados e lote estilo Xpiks). Ver `UI_REFACTOR_PLAN.md`.

## Rodada de UX pós-Fase 10 — notas (Etapa 4)

- A aba Metadados é o `MetadataEditorViewModel` (substitui `MetadataViewModel`, `BatchMetadataViewModel` e a janela de lote). Cada foto é uma `MetadataRowViewModel` (rascunho + baseline lido do arquivo). **Operações globais mexem só nos rascunhos; gravar é sempre `SaveAllAsync/SaveRowsAsync` → `IMetadataEditService.SaveAsync`** (pipeline seguro). Não crie caminhos que gravem direto sem passar por ele.
- Alvos do editor: `Library.SelectedCards` (ou `SelectedPhoto` se não houver marcadas). Linhas alteradas nunca somem no `RefreshTargets`.
- Leitura de metadados é preguiçosa por linha (`EnsureLoadedAsync`, concorrência 4); operações/salvar carregam as marcadas antes.
- `BatchMetadataService` ficou sem uso na UI (continua testado); pode servir a automações futuras (ex.: aplicar preset em massa pela Central de Produção).
- A `LibraryView` restaura a seleção múltipla ao ser recriada (volta de outra aba) usando o snapshot da VM; `AutomationProperties.Name` foi adicionado a controles só com ícone/abas para automação de UI.
- Fim da Rodada 2 (Etapas 2–4). Pendências de produto registradas nos documentos: rascunhos não persistem ao fechar, "restaurar versão" do histórico, RAW com sidecar `.xmp`.

## Coleções e subcoleções — notas da Etapa B

- **Itens virtuais:** `Todas` (`AllCollectionsKey = "__todas_colecoes__"`) e `Sem coleção` (`NoCollectionKey = "__sem_colecao__"`) são virtuais (`SidebarEntry.IsVirtual = true`). Não criam nem dependem de linhas na tabela `Collections`.
- **Filtros e mútua exclusão:** `_allCollectionsOnly` e `_noCollectionOnly` são mutuamente exclusivos entre si e com coleções reais. A seleção de "Todas" exibe apenas fotos com `Photo.Collections.Count > 0` e marca `HasActiveFilters = true`.
- **Separador e Tooltip:** `SidebarEntry` expõe `ToolTipText` e `HasSeparatorAfter = true` (em "Sem coleção"), criando separador visual entre virtuais e coleções reais no XAML.
- **ComboBox de Filtros:** Renomeado o item neutro para `"Qualquer"` (`AnyCollectionChoice = "Qualquer"`). `RefillCollectionChoices` filtra entradas virtuais (`!e.IsVirtual`), impedindo vazamento de chaves internas.
- **Invariante de Contagem:** `Todas.Count + SemColecao.Count == Total de fotos no catálogo` (calculado em `UpdateSidebarCounts()` via passada única em memória sobre `_all`). Fotos ausentes contam normalmente porque pertencem ao catálogo.
- **Próxima etapa:** Etapa C1 (Modelo, Migração e Serviço sem UI). Não mexer em UI na Etapa C1.

