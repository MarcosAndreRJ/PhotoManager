# Arquitetura do PhotoManager

## Projetos

- `PhotoManager.Domain`: entidade `Photo`, sem dependência de UI ou persistência.
- `PhotoManager.Application`: contratos e serviços independentes da UI (catálogo, organização, operações de arquivo, navegação). `PhotoFilter.Matches` concentra a regra de filtro.
- `PhotoManager.Infrastructure`: caminhos locais, configuração JSON, logging em arquivo, miniaturas, operações físicas de arquivo e leitura de metadados (`MetadataExtractorReader`, pacote MetadataExtractor).
- `PhotoManager.Persistence`: schema e repository SQLite.
- `PhotoManager.Wpf`: entrada da aplicação, shell, Views, ViewModels, temas e controles.
- `tests/PhotoManager.Tests`: xUnit sobre os serviços reais com banco/cache/fotos em pasta temporária, mais testes de fumaça de UI em thread STA.

Dependências: `Wpf → Application, Domain, Infrastructure, Persistence`; `Persistence → Application, Domain, Infrastructure`; `Infrastructure → Application`; `Application → Domain`.

## Decisões

- Plataforma: WPF nativo, `net10.0-windows`. MVVM simples com `RelayCommand`/`AsyncRelayCommand` próprios.
- Navegação: `INavigationService` mantém itens e publica a área ativa; `MainViewModel` expõe as abas (`NavigationTabViewModel`) e a `CurrentView`. A `LibraryViewModel` é única na sessão.
- DI: `Microsoft.Extensions.DependencyInjection` (caminhos, configuração, repositories, serviços, navegação, ViewModel principal, janela, logging).
- Logging em `%LocalAppData%\PhotoManager\Logs\photomanager.log` (inclui exceções). Configuração em `Data\settings.json`. Instância única por mutex.
- Banco: `Data\photomanager.db`, inicialização idempotente. Pasta raiz sobrescrevível pela variável `PHOTOMANAGER_ROOT` (testes/captura de tela).
- Miniaturas: `Cache\Thumbnails\{PhotoId}.jpg`; a grade nunca carrega originais. O preview decodifica o original com largura máxima de 1600 px em segundo plano e libera o arquivo.
- Organização: tags e coleções em tabelas normalizadas; categoria é texto em `Photos`; nota, avaliação e favorito ficam na foto.
- Edição em lote altera só dados do catálogo, nunca os arquivos.
- Operações físicas isoladas em `IFileOperationService`; exclusão vai para a Lixeira do Windows (sem exclusão permanente); mover/renomear nunca sobrescrevem.
- Formatos: `.jpg`, `.jpeg`, `.png`, `.webp`.

## Fluxo de dados da Biblioteca

```text
Banco ──ReloadAsync()──▶ _all (List<Photo>) ──PhotoFilter.Matches + lista inteligente + pasta──▶ Photos (cards)
                           │                                                        │
                           └──▶ sidebar (contagens)                                 └──▶ miniaturas em segundo plano
```

Filtros, sidebar e favorito no cartão atuam sobre `_all` (os cards compartilham os mesmos objetos `Photo`). Importar, copiar com catálogo, mover, renomear e excluir recarregam do banco. Respostas obsoletas são descartadas por contadores de versão (`_loadVersion`, `_viewVersion`, `_previewVersion`).

## Camada visual

```text
Wpf/
  App.xaml                 mescla Themes/* em ordem (Colors primeiro) — nunca aninhados
  Themes/                  Colors · Typography · Buttons · Inputs · Cards · Tabs
  Controls/                RatingControl · VirtualizingWrapPanel
  ViewModels/              MainViewModel · ViewModelBase · RangeObservableCollection
  Views/                   LibraryView(+ViewModel) e páginas das demais áreas
```

Layout da Biblioteca: `Sidebar (228) | Centro (toolbar, filtros, grade virtualizada, status) | Painel (preview + abas)`. Code-behind restrito a diálogos, seleção múltipla da `ListBox` e debounce da busca.

## Navegação atual

Biblioteca (fases 2–4, 7, 10), Metadados (fases 5–6), a janela de lote (fase 7), o workflow Microstock local (fases 8–9) e a ferramenta de duplicatas exatas (fase 10) estão completas. Configurações continua placeholder honesto.

## Escrita de metadados (Fase 6)

```text
MetadataViewModel ──▶ IMetadataEditService (Application) ──▶ IMetadataReader   (compara com o arquivo)
                                                         ├─▶ IMetadataWriter   (JpegMetadataWriter: tmp → validar → File.Replace)
                                                         ├─▶ ICatalogRepository.UpdateLocationAsync (tamanho/data)
                                                         └─▶ IMetadataVersionRepository (MetadataHistory + Photos.MetadataVersion)
```

`JpegMetadataWriter` opera em segmentos JPEG: substitui só o APP1-XMP (mesclando com `XmpCore`) e o recurso IPTC 0x0404 do APP13; o resto do arquivo é copiado byte a byte e a validação confirma isso antes de substituir o original.

## Workflow Microstock local (Fase 8)

`ValidationProfile` e `ValidationRules` ficam em `Application/Microstock` e são persistidos como JSON na tabela `ValidationProfiles`; a inicialização cria/seed um perfil genérico de forma idempotente. `ValidationProfileValidator.Validate(Photo, PhotoMetadata, ValidationProfile)` e `PreparationStatusCalculator.Calculate(...)` são funções puras, sem acesso a WPF, SQLite ou rede.

`MicrostockEvaluationService` lê metadata em segundo plano com no máximo quatro leituras concorrentes, informa progresso, respeita cancelamento e mantém cache apenas na sessão. A chave do cache inclui `PhotoId`, `MetadataVersion`, `FileSize` e `ModifiedAt`; assim uma edição de metadata ou mudança física força nova leitura sem criar uma segunda fonte de verdade no banco.

`MicrostockViewModel` compartilha a `LibraryViewModel` singleton da sessão, recalcula ao ativar a aba e navega para Metadados pelo mesmo objeto selecionado. A Fase 9 carrega o estado atual do histórico persistido em `IUploadHistoryService`; `UploadRecordSnapshot` continua sendo a entrada pura de `PreparationStatusCalculator`.

## Histórico de envio manual (Fase 9)

```text
MicrostockViewModel ──▶ IUploadHistoryService ──▶ IUploadHistoryRepository ──▶ SQLite
       │                         │                         ├─ Agencies
       │                         │                         └─ UploadRecords (append-only)
       └─ seleção/agências ──────┴─▶ MicrostockEvaluationService ──▶ estados puros
```

`UploadRecords` guarda cada ação manual e nunca sobrescreve um registro anterior. O repositório calcula o estado atual pela última linha de cada par `(PhotoId, AgencyId)`; `Pending` representa o desfazer. A lista de agências ativas é enviada ao cálculo para que “Enviada para todos” considere inclusive bancos sem histórico para aquela foto. A UI não faz chamadas de rede: apenas registra enviado, erro, rejeitado ou desfaz, com data, versão da metadata, nome remoto e observações.

## Duplicatas exatas (Fase 10)

```text
ToolsViewModel ──▶ IDuplicateDetectionService ──▶ ICatalogRepository
                            │                         └─ fotos por tamanho
                            ├─▶ IFileHashService (SHA-256 streaming)
                            └─▶ IDuplicateRepository ──▶ ContentHash/ignores
LibraryViewModel ◀──────────┴─ smart list Duplicadas
```

O detector agrupa por `FileSize` antes de calcular qualquer hash. O hash é calculado em blocos de 1 MiB, salvo por foto e invalidado por alteração de tamanho/data real ou por nova versão de metadata. Grupos ignorados são identificados pelo hash e persistidos em `DuplicateIgnores`. A tela de Ferramentas mantém a regra de segurança: não há exclusão automática, uma cópia precisa ser marcada como mantida e a mesclagem da organização só ocorre quando o usuário a solicita explicitamente.

## Coleções e subcoleções (Frente de Coleções — Etapas C1, C2, D e E)

```text
LibraryView (Sidebar TreeView + Painel Organização com Chips + Drag-and-Drop)
       │
       ├─ Drag Source (PhotoList): limiar de movimento, PhotoSelectionDragHelper (seleção múltipla vs 1 foto)
       ├─ Drag Source (CollectionTree): PreviewMouseLeftButtonDown/Move com limiar de arraste para nós de coleção
       ├─ Drag Target (CollectionTree): convive com 2 formatos (PhotoIds vs CollectionId), highlight (IsDragOver)
       ├─ Drag Target Raiz (CollectionRootDropTarget): cabeçalho "COLEÇÕES" (IsDragOverRoot) aceita mover para raiz
       ├─ Feedback Flutuante: DragFeedbackPopup com mensagens em tempo real (ação planejada ou motivo de bloqueio)
       │
       ▼
LibraryViewModel ──▶ DropPlanner & CollectionDropPlanner (funções puras em Application.Collections)
       │         │    ├─ DropPlanner: Add, Move, AskMenu (interseção comum), Blocked, NoOp ("PhotoManager.PhotoIds")
       │         │    └─ CollectionDropPlanner: Move, Blocked (ciclo/profundidade/homônimo/virtual), NoOp ("PhotoManager.CollectionId")
       │         ├─ CollectionNode (árvore observável com contagens diretas e subárvore)
       │         ├─ CollectionNameDialog / DeleteCollectionDialog / MoveCollectionDialog (diálogos modais)
       │         ├─ Delegates testáveis (ShowNameDialog, ShowDeleteDialog, ShowMoveDialog)
       │         └─ OrganizationBatch (CollectionsToAdd : IReadOnlyList<long>)
       ▼
ICollectionService (Application) ──▶ CollectionService (regras puras e orquestração)
       │                                  ├─ Validação de nomes (único entre irmãos, trim, <= 100)
       │                                  ├─ Grafo de ancestrais / Prevenção de ciclos (A↔B, A→...→descendente)
       │                                  ├─ Verificação de profundidade máxima (<= 8 níveis)
       │                                  ├─ AddPhotosAsync / MovePhotosAsync (mutações transacionais pontuais)
       │                                  ├─ MoveAsync (reparentar coleção para novo pai ou para a raiz)
       │                                  └─ DeleteMode (PromoteChildren com sufixos / WithDescendants)
       ▼
ICollectionRepository (Persistence) ──▶ SqliteCatalogRepository (SQLite)
                                          ├─ Collections (Id, Name, ParentCollectionId, SortOrder, CreatedAt)
                                          ├─ PhotoCollections (PhotoId, CollectionId)
                                          ├─ UX_Collections_Parent_Name: (COALESCE(ParentCollectionId, 0), Name NOCASE)
                                          └─ Migração D3: recriação atômica com FK=OFF, .bak e PRAGMA foreign_key_check
```

- **Natureza estritamente virtual:** Coleções e subcoleções não representam pastas nem arquivos em disco; o drag-and-drop de fotos ou coleções ou exclusão nunca altera ou apaga fotos da tabela `Photos` nem arquivos no disco.
- **Independência de SaveAsync (D12):** A edição de organização (categoria, nota, avaliação, favorito, tags) via `IOrganizationRepository.SaveAsync` não altera `PhotoCollections`. Apenas `ICollectionService` muta a tabela de relacionamento.
- **Identidade por Id e Seletores Estruturados:** `Photo.CollectionIds` comanda a persistência; `Photo.Collections` é uma projeção derivada. A interface substituiu campos de texto livre por chips com botão `(×)` e ComboBoxes que listam apenas coleções disponíveis, eliminando erros de digitação e homonímias.
- **Contadores da Árvore:** Cada nó da árvore exibe exclusivamente a contagem de fotos diretamente associadas a ele (`DirectCount`). O tooltip do nó informa as fotos diretas e as pertencentes a subcoleções (`SubtreeCount`). As entradas virtuais ("Todas" e "Sem coleção") permanecem fixas no topo da seção COLEÇÕES.
- **Drag-and-Drop de Fotos (Etapa D):** Decisão completamente desacoplada da UI via `DropPlanner`. Suporte à seleção múltipla por IDs (`PhotoIdsDragData`), sem expor objetos WPF ou travar virtualização. Arraste padrão adiciona; Shift arrasta para mover (removendo apenas da coleção de origem ativa na sidebar e saindo do filtro atual sem desmarcar seleção). Origens ambíguas disparam menu `AskMenu` calculando a interseção de coleções comuns.
- **Drag-and-Drop de Coleções e Prevenção de Ciclos (Etapa E):** Função pura `CollectionDropPlanner` calcula a viabilidade da operação durante o `DragOver` em memória sem I/O síncrono. Mover subcoleção sobre o cabeçalho "COLEÇÕES" reparenta para a raiz (`ParentCollectionId = null`). Bloqueios estritos com motivo explícito no popup: tentativa de mover para si mesma, para qualquer de seus descendentes (ciclos A→A, A↔B, A→...→A), estouro de 8 níveis de profundidade (somando as alturas), e conflito de nome entre irmãos no nível de destino (`COLLATE NOCASE`). Defesa dupla no serviço (`CollectionService.MoveAsync`) garante integridade transacional mesmo contra chamadas externas.

