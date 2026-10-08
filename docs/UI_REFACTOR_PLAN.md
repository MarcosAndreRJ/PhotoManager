# Plano de refatoração visual

Referências analisadas: as 11 imagens em `docs/Imagens/` (biblioteca com sidebar + grade + painel, preview ampliado com filmstrip, editor de metadados com chips, galeria com filtros em pílulas). Elas misturam variações (tema escuro, tema claro, nomes diferentes), então o plano extrai os padrões comuns em vez de copiar uma tela.

## A. Elementos recorrentes nas referências

| Elemento | Presente em | Decisão |
|---|---|---|
| Barra superior com marca + abas (Biblioteca, Metadados, Microstock, Ferramentas, Configurações) | todas | **Feito** (barra escura, aba ativa com sublinhado azul) |
| Sidebar: Todas, Favoritas, Recentes, Sem categoria, Duplicadas, Lixeira + Pastas/Categorias/Tags/Coleções com contagem | Biblioteca, Preview, Editor | **Feito** (sem Duplicadas e Lixeira — ver abaixo) |
| Grade de cards: miniatura grande, nome, estrelas, coração, selo de tipo | Biblioteca, Galeria | **Feito** (virtualizada) |
| Barra de ferramentas (Importar, Adicionar, Mover, Copiar, Excluir) + busca | Biblioteca | **Feito** |
| Barra de filtros (Categoria, Tag, Avaliação…) | Biblioteca, Galeria | **Feito** (Categoria, Tag, Coleção, Avaliação mínima, Favoritas) |
| Painel direito: preview, nome, estrelas, abas | Biblioteca, Preview, Editor | **Feito** (abas Informações / Organização / Lote e arquivos) |
| Preview com setas anterior/próxima e contador "n de total" | Preview, Editor | **Feito** |
| Zoom, 100 %, ajustar, tela cheia | Preview, Editor | Adiado (ver G) |
| Filmstrip inferior | Preview, Editor, Library-2 | Adiado (ver G) |
| Chips de tags/keywords com "x" e contadores de caracteres | Editor de metadados | Fases 6–7 |
| Abas Metadados / Microstock / Histórico, status "Pronta para envio" | todas | Fases 5–9 (não criar abas vazias) |
| Alternar grade/lista, slider de tamanho dos cards | Biblioteca | Adiado (ver G) |
| EXIF + mapa/GPS | Preview | Fase 5 |
| Botões "Sugerir keywords"/"Gerar com IA" | Editor | **Não implementar** (IA é fase 16; sem botões falsos) |

Itens das referências **deliberadamente não criados** por dependerem de fases futuras: *Duplicadas* (fase 10), *Lixeira* (a exclusão usa a Lixeira do Windows, que o app não lista), *Bancos/Status de envio* (fases 8–9), *Em upload/Enviadas* (fases 11–13), *Vídeos* (fora do escopo).

## B. Mapeamento UI atual → UI desejada

| Antes | Depois |
|---|---|
| `MainWindow`: cabeçalho claro + menu lateral de 230 px | Barra superior escura com marca e abas; a área ocupa toda a largura |
| `LibraryView`: título + filtros em linha + `WrapPanel` + painel único longo com tudo empilhado | Sidebar (228) · centro (toolbar, filtros, grade virtualizada, status) · painel direito (preview + abas) |
| Painel "Visualização e organização" | `TabControl`: **Informações** (dados do arquivo), **Organização** (categoria/tags/coleções/nota/favorita + Salvar), **Lote e arquivos** (edição em lote, renomear por template, copiar ao catálogo) |
| Botões Mover/Copiar/Excluir dentro do painel | Barra de ferramentas do centro |
| Filtros por texto livre (categoria/tag/coleção) + botão "Filtrar" | ComboBoxes populados com valores reais; refiltram na hora, sem tocar no banco |
| `Resources/Theme.xaml` | `Themes/` (ver E) |
| Páginas Metadados/Microstock/Ferramentas/Configurações | Mesmo conteúdo honesto ("será construída na fase N") no novo estilo |

## C. Arquivos mantidos (sem alteração funcional)

`PhotoManager.Domain/*`, `PhotoManager.Persistence/*` (exceto bump de pacote), `INavigationService`/`NavigationService`/`NavigationItem`, `IFileOperationService`, `IOrganizationService`, `ThumbnailService`, `ApplicationPaths` (ganhou só o override de teste), `AsyncRelayCommand`, `RelayCommand`, `ViewModelBase`, `PageViewModels.cs`, code-behind das páginas placeholder.

## D. Arquivos alterados

| Arquivo | Alteração |
|---|---|
| `Wpf/App.xaml` | Mescla `Themes/*` diretamente |
| `Wpf/MainWindow.xaml` | Nova barra superior; sem lógica nova |
| `Wpf/ViewModels/MainViewModel.cs` | Abas com estado `IsSelected`; **a `LibraryViewModel` passa a ser única na sessão** (antes era recriada a cada navegação, perdendo filtros/seleção/miniaturas) |
| `Wpf/Views/LibraryView.xaml(.cs)` | Reescrita do layout; code-behind só com diálogos, seleção múltipla e debounce da busca |
| `Wpf/Views/LibraryViewModel.cs` | API pública das fases 2–4 **preservada**; adicionados sidebar, filtros em memória, favorito no cartão, preview assíncrono, anterior/próxima; `PhotoCardViewModel` com miniatura notificável e `IsMissing` calculado uma vez |
| `Wpf/Views/{Metadata,Microstock,Tools,Settings}View.xaml` | Novo estilo |
| `Application/Catalog/ICatalogService.cs` | `PhotoFilter.Matches(Photo)` (a lógica de filtro saiu do `CatalogService` e passou a ser reutilizável em memória) |
| `Application/Catalog/CatalogService.cs` | Usa `filter.Matches` (comportamento idêntico) |
| `Infrastructure/Files/FileOperationService.cs` | Retentativa em violação de compartilhamento ao mover/renomear (preview/miniatura podem estar lendo o arquivo) |
| `Infrastructure/Logging/FileLoggerProvider.cs` | Passa a registrar a exceção (antes só a mensagem — impedia diagnosticar falhas) |
| `*.csproj` | `Using System.IO` (ver riscos); `Microsoft.Data.Sqlite` 10.0.0 → 10.0.12 (corrige vulnerabilidade de `e_sqlite3`) |

## E. Arquivos criados

- `Wpf/Themes/Colors.xaml`, `Typography.xaml`, `Buttons.xaml`, `Inputs.xaml`, `Cards.xaml`, `Tabs.xaml`
- `Wpf/Controls/RatingControl.cs` — estrelas desenhadas via `OnRender` (um único elemento por avaliação, barato na grade)
- `Wpf/Controls/VirtualizingWrapPanel.cs` — painel de grade virtualizado para itens de tamanho fixo
- `Wpf/ViewModels/RangeObservableCollection.cs` — troca o conteúdo com uma única notificação
- `tests/PhotoManager.Tests/*` — 21 testes (regressão das fases 2–4, lógica nova, fumaça de UI)
- `docs/UI_REFACTOR_PLAN.md` (este)

Controles sugeridos no pedido e **não criados** por não haver ganho real agora: `PhotoThumbnailCard` (é um `DataTemplate`), `PhotoGrid`, `PhotoPreview`, `PhotoInfoPanel`, `TagChip`, `FilterBar`, `StatusBadge` (é um estilo), `EmptyState`, `MetadataEditor`. `TagChip`/`MetadataEditor` nascem na fase 6.

## F. Riscos de regressão e como foram tratados

| Risco | Tratamento |
|---|---|
| DataContext / bindings quebrados | Teste de fumaça carrega a janela real (STA), popula a grade, seleciona uma foto e varre a árvore visual |
| Comandos | `NavigateCommand` testado nas 5 áreas; novos comandos testados |
| Virtualização | `VirtualizingWrapPanel` próprio; antes o `WrapPanel` criava um card + bitmap por foto |
| Navegação | `LibraryViewModel` única; teste confirma que voltar à Biblioteca devolve a mesma instância |
| DI | `App.xaml.cs` e construtores de `MainViewModel`/`LibraryViewModel` **inalterados** |
| Performance | Miniaturas resolvidas em segundo plano (grade aparece na hora); filtros em memória; preview decodificado com largura limitada a 1600 px e arquivo liberado (`OnLoad`) |
| Seleção múltipla | `ListBox` `Extended` mantido; code-behind lê `SelectedItems`; seleção única preservada ao refiltrar |
| Thumbnails | Cache em disco por `PhotoId` reaproveitado sem mudança; cache de URIs em memória |
| Recursos WPF | **Dicionários aninhados não enxergam as cores uns dos outros dentro de `ControlTemplate`** → todos mesclados no nível do `App.xaml`, na ordem Colors → demais |
| `UseWPF` remove `System.IO` dos implicit usings | `Using Include="System.IO"` nos projetos WPF |

## G. Itens da referência adiados (não são bloqueio para a fase 5)

Zoom/100 %/ajustar/tela cheia, filmstrip, alternar grade/lista, slider de tamanho, chips de tag (edição continua por texto separado por vírgula), arrastar e soltar, ícone próprio do aplicativo, janela sem moldura (usa a barra de título padrão do Windows).


---

# Rodada 2 — Refatoração de UX/fluxo após a Fase 10

Escopo: **somente UX/layout/fluxo**. Nenhuma fase nova; banco, migrations, serviços, repositórios, microstock, agências, duplicatas e metadados permanecem intactos. Baseline medido antes de começar: `dotnet build` 0 erros/0 avisos; `dotnet test` **95/95**.

> Nota: as 5 imagens citadas no pedido não chegaram como arquivos legíveis nesta conversa; o diagnóstico usa a descrição dos problemas, o código atual e os mockups de `docs/Imagens/` (preview com filmstrip: `…Cityscape Preview-4.png`, `…Pôr do Sol Urbano-7.png`; editor: `…Metadata Editor-6.png`).

## 1. Auditoria da UI atual

| Área | Onde está | Observação |
|---|---|---|
| Shell/navegação | `MainWindow.xaml`, `MainViewModel` | Estável. `LibraryViewModel` e `MetadataViewModel` únicos por sessão. |
| Grade | `LibraryView.xaml` (`ListBox` + `Controls/VirtualizingWrapPanel`) | Virtualizada. Seleção `Extended` do `ListBox`; a única pista visual é a borda azul. |
| Card | `PhotoCardTemplate` + `PhotoCardContainer` (Cards.xaml) | Miniatura, selo, coração, nome, estrelas. **Sem controle de seleção.** |
| Painel direito | `LibraryView.xaml` (340 px) | Preview 210 px + abas *Informações / Organização / Lote e arquivos*. |
| Seleção na VM | `LibraryViewModel.SelectionCount` (setado pelo code-behind) e `SelectedPhoto` (= 1º item) | A VM **não conhece quais fotos estão selecionadas**; o code-behind lê `PhotoList.SelectedItems`. |
| Metadados | `MetadataView/ViewModel` (1 foto), `BatchMetadataWindow` (modal, por campo) | Fluxos separados, sem lista de fotos nem edição por linha. |
| Estilos | `Themes/*` mesclados no `App.xaml` | `CardCheckBox` não existe. |

## 2. Diagnóstico (problemas → causa)

1. **"Lote e arquivos" confusa.** Mistura três responsabilidades num só scroll (edição em lote de categoria/tag/coleção + renomear + "adicionar cópias"), sem dizer **em quantas/quais fotos** vai agir; Mover/Copiar/Excluir/Metadados vivem na barra superior, longe do painel; o painel não muda quando há várias fotos selecionadas (continua mostrando a 1ª).
2. **Seleção múltipla invisível.** Só há borda azul; não há caixa de seleção, "Selecionar tudo", "Limpar seleção" nem contagem em destaque; o botão Metadados só habilita por contagem escondida.
3. **Preview sem a navegação do mockup.** O preview é uma miniatura de 210 px no painel (setas + posição). Faltam área central ampla, barra de ferramentas (zoom/ajustar/100 %/tela cheia), filmstrip e abas Metadados/Microstock/Histórico junto da foto. (**Etapa 3**)
4. **Metadados burocráticos.** Edição individual é um formulário em coluna; lote é uma janela modal por campo sem lista de fotos, sem edição por linha, sem chips compartilhados. (**Etapa 4**)

## 3. Plano em etapas

### Etapa 2 — Biblioteca, seleção e painel direito (esta entrega)
- **Checkbox em cada card** (canto superior esquerdo, sempre visível; estado marcado forte). Liga-se ao `IsSelected` do `ListBoxItem` ⇒ continua valendo Ctrl/Shift/clique e a virtualização.
- **Barra de seleção** no topo da grade: `Selecionar tudo`, `Limpar seleção` e contador em destaque ("3 selecionadas"). A VM passa a conhecer a seleção (`SelectedCards`, `IsMultiSelection`, resumo, miniaturas).
- **Painel direito sensível à seleção:**
  - 0 fotos → estado vazio explicativo;
  - 1 foto → preview + detalhes (como hoje);
  - ≥ 2 fotos → cabeçalho "N fotos selecionadas" com miniaturas e resumo (tipos, tamanho total, quantas são JPEG editáveis).
- **Abas com uma responsabilidade cada**, todas mostrando o alvo ("Será aplicado a N fotos") e **desabilitadas sem seleção**:
  1. *Informações* (detalhes de 1 foto ou resumo da seleção);
  2. *Organização* — categoria, tags, coleções, avaliação, favorito, nota. 1 foto: edita e salva; N fotos: formulário em lote ("Manter" por padrão) + **Aplicar a N fotos**;
  3. *Arquivos* — mover, copiar (+ "adicionar cópias ao catálogo"), renomear (nome ou template), excluir (Lixeira, destacado como perigoso);
  4. *Metadados* — resumo do impacto (N fotos, X editáveis, demais ignoradas), operações disponíveis e botão **Editar metadados de N fotos…** (a edição em si é refeita na Etapa 4).
- **Barra de ferramentas enxuta:** *Adicionar pasta* + seleção; Mover/Copiar/Excluir/Metadados saem da barra (passam às abas) para acabar com a duplicidade.
- Lógica nova e testável fora do code-behind (`OrganizationBatch` em Application; seleção na `LibraryViewModel`).

### Etapa 3 — Navegação de preview (próxima, após aprovação)
Modo de revisão com foto central, barra de ferramentas (anterior/próxima, zoom −/+, ajustar, 100 %, tela cheia, favorito, avaliação), filmstrip virtualizado, "8 de 4.820" e painel lateral com abas Informações/Metadados/Microstock/Histórico. Reaproveita `LibraryViewModel.Photos/SelectedPhoto/Previous/Next` e `ImageLoader`.

### Etapa 4 — Metadados e lote estilo Xpiks (depois)
Editor com lista de fotos selecionadas (miniatura + Title/Description/Keywords por linha), painel global com operações por campo, chips compartilhados, remover/adicionar sem substituir, pré-visualização do impacto. Reaproveita `BatchMetadataPlan`/`IBatchMetadataService`/presets.

## 4. Arquivos (Etapa 2)

| Ação | Arquivo |
|---|---|
| Alterar | `Wpf/Views/LibraryView.xaml(.cs)`, `Wpf/Views/LibraryViewModel.cs`, `Wpf/Themes/Cards.xaml` (estilo `CardCheckBox`), `Application/Catalog/ImageFormats.cs` (`IsJpeg`) |
| Criar | `Application/Catalog/OrganizationBatch.cs`, `tests/…/SelectionAndOrganizationTests.cs`, testes de fumaça adicionais em `UiSmokeTests` |
| Manter | `Application/*` restante, Persistence, Infrastructure, Microstock/Duplicatas/Metadados, `BatchMetadataWindow` (até a Etapa 4) |

## 5. Riscos de regressão

| Risco | Mitigação |
|---|---|
| Virtualização e desempenho da seleção | Checkbox é binding ao `IsSelected` do container (nada de coleção paralela); a VM guarda só a lista dos selecionados, atualizada em `SelectionChanged` |
| `SelectedPhoto` (usado por Metadados, Prev/Next, preview) | Mantido como "foto primária"; apenas a apresentação do painel muda |
| Atalhos Ctrl/Shift/Ctrl+A | O `ListBox` continua `Extended`; o checkbox só alterna o item |
| Operações de arquivo movidas da barra | Mesmos métodos da VM; mesmos diálogos; testes de regressão das fases 2–4 continuam |
| Estilos/templates (lições do handoff) | `CardCheckBox` em `Cards.xaml`; `UiSmokeTests.ThemeControlTemplates_Instantiate` cobre; sem `DisplayMemberPath` em ComboBox |
| Largura mínima 1100 px | Painel 380 px; abas com padding menor; verificado em captura |

### Status da Rodada 2
- **Etapa 2** (Biblioteca, seleção, painel direito): concluída.
- **Etapa 3** (modo de revisão: foto central, filmstrip, zoom/ajustar/100 %, tela cheia, abas Informações/Metadados/Microstock/Histórico): concluída — ver `IMPLEMENTATION_STATUS.md`.
- **Etapa 4** (edição de metadados e lote estilo Xpiks): concluída (ver abaixo).


### Etapa 4 — plano detalhado (edição de metadados e lote estilo Xpiks)

**Diagnóstico.** Hoje há dois fluxos separados e burocráticos: (1) a aba *Metadados* edita **uma foto** num formulário em coluna; (2) a janela modal de lote aplica operações **direto nos arquivos** sem mostrar o resultado por foto até depois (só pré-visualização textual). Não há lista de fotos, edição por linha, nem como revisar o efeito de uma operação global antes de gravar.

**Abordagem (Opção B do pedido — híbrido):** um único **Editor de metadados** (aba *Metadados*) com
- **lista das fotos selecionadas** (miniatura, nome, Título, Descrição, Autor, Copyright e palavras-chave em chips, tudo editável na linha), caixa para marcar/desmarcar quais fotos entram nas operações globais, marcador de "alterada" por linha e por campo, contadores por campo, reverter por linha;
- **painel de operações globais** (por campo, independentes): Título/Autor/Copyright (manter, substituir), Descrição (manter, substituir, acrescentar), Palavras-chave (manter, substituir, adicionar, remover, limpar; chips; sem duplicadas) + atalhos *Copiar da foto em foco* e *Eliminar palavras repetidas*; **a operação é aplicada aos rascunhos** (as linhas mudam na hora, em destaque) e **nada é gravado no arquivo até "Salvar"** — assim o efeito é visível antes de gravar;
- **presets** (os mesmos da Fase 7) e **foto em foco** (miniatura, EXIF, GPS, histórico);
- **barra inferior** com contagem de alteradas, *Reverter tudo* e *Salvar N alteradas* (pipeline seguro por foto, progresso, cancelar, resultado por linha; uma falha não interrompe as demais).

**Fluxo de entrada:** fotos marcadas na Biblioteca (ou a foto selecionada); *Editar na tela Metadados* (revisão) e *Abrir em Metadados* (Microstock) levam à mesma tela; o botão da aba *Metadados* da Biblioteca navega para o editor com a seleção atual (substitui a janela modal).

**Reaproveita:** `BatchMetadataPlan` (regras puras das operações), `IMetadataEditService` (grava com validação/versão), `IMetadataReader`, `IMetadataPresetRepository`, `ReviewViewModel` (navegação). **Substitui:** `MetadataViewModel`/`MetadataView` (formulário de 1 foto) e `BatchMetadataViewModel`/`BatchMetadataWindow` (modal) — os testes desses foram migrados para o novo editor. `BatchMetadataService` (aplicação direta em arquivos) permanece como API testada, mas sem uso na interface.

**Segurança dos dados:** rascunhos sobrevivem à troca de aba (linhas alteradas continuam na lista mesmo que a seleção da Biblioteca mude); fotos PNG/WEBP/RAW/ausentes aparecem desabilitadas com o motivo; leitura dos metadados é preguiçosa (só das linhas exibidas, ou de todas as marcadas ao aplicar/salvar), com concorrência limitada.

**Riscos:** regressão nos fluxos de salvar/versão/histórico (cobertos pelos testes migrados), desempenho com centenas de linhas (lista virtualizada + leitura preguiçosa), perder a seleção múltipla ao trocar de aba (corrigido junto: a Biblioteca restaura a seleção ao voltar).
- **Etapa 4** (editor de metadados e lote estilo Xpiks): concluída — ver `IMPLEMENTATION_STATUS.md`. **Rodada 2 encerrada.**
