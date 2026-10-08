# Módulo Transferência — plano e auditoria

Status: **Etapas 2 a 6 implementadas** — painéis, organização (pastas, cores), copiar/mover com progresso, desfazer, renomear no lugar e em lote, organizar por data, filtros, comparação, Locais, layout guardado, área de transferência, cor de pasta, duplicatas e painel de detalhes (ver 4.1 e 4.2).

## 1. Auditoria dos componentes existentes

| Tema | O que existe hoje | Decisão para Transferência |
|---|---|---|
| Navegação | `NavigationService` (`Application/Navigation`) com chaves; `MainViewModel.CreateViewModel` cria/guarda uma página por chave; `MainWindow.xaml` liga VM → View por `DataTemplate`. | Nova chave `Transfer` ("Transferência", entre Microstock e Ferramentas), `TransferViewModel` + `TransferView`. |
| Exibição de arquivos | Biblioteca: `ListBox` + `VirtualizingWrapPanel` (virtualização por item de tamanho fixo) + `PhotoCardViewModel`. | Mesmo painel virtualizado na grade; `ListView/GridView` virtualizado na lista. |
| Seleção múltipla | `ListBox SelectionMode=Extended` (Ctrl/Shift), `PhotoClickPolicy`, modo multi-seleção com caixas. | Mesmo `ListBox` Extended; Ctrl+A nativo. |
| Drag & drop | `PhotoDragData` (drag-out com FileDrop + formato interno), planejadores puros `DropPlanner`, `FolderDropPlanner`, `ExternalDropPlanner`, `CollectionDropPlanner`. | Etapa 5: reutilizar `ExternalDropPlanner` (soltar arquivos de fora) e `PhotoDragData`; **não** criar um segundo planejador. |
| Mover/copiar | `IFileOperationService`: `MoveAsync`/`CopyAsync` (itens **catalogados**, atualizam o catálogo) e `TransferExternalAsync` (caminhos **fora do catálogo**, leva o `.xmp`, nunca sobrescreve). | Etapa 4: nova `IFileTransferService` (Application) com progresso, cancelamento, colisões e verificação; para itens catalogados delega a `MoveAsync/CopyAsync` (para o catálogo seguir o arquivo), para o resto faz a cópia física. `TransferExternalAsync` continua para o drop do Explorer. |
| Marcação por cor | `Photo.ColorLabel` (enum `PhotoColor`) persistido na tabela `Photos` **por `PhotoId`**; `ICatalogService.SetColorLabelAsync` → `ICatalogRepository.UpdateColorLabelAsync`; nomes/hex em `PhotoColors`; pincéis em `ColorLookup`; `ColorChoice` para menus. | Etapa 5: usar exatamente essas peças. Arquivo físico → `ICatalogRepository.FindByPathAsync(caminho)`; se achar, mostra/altera a cor do `Photo`; **se não estiver no catálogo, a marcação fica indisponível** com a ação "Adicionar ao catálogo para usar marcações" (`ICatalogService.ImportPathsAsync`). Nenhum segundo sistema de cor. |
| Miniaturas | `ThumbnailService` (cache JPEG 280 px em `Cache/Thumbnails/{PhotoId}.jpg`; vídeos via `ShellThumbnail`); `IPhotoInfoReader` para dimensões/duração. | Cache por `PhotoId` não serve para arquivo fora do catálogo. Novo `IFileThumbnailService` (implementado pelo mesmo `ThumbnailService`, mesma rotina de geração) com cache por **hash de caminho+tamanho+data** em `Cache/Thumbnails/Files/`. Nunca carrega o original na grade. |
| Vídeo | `MediaFormats`/`MediaKind` (mp4, mov, m4v, avi, mkv…), `Mp4Info`, `VideoPlayer`, `MediaViewerWindow`. | Reconhecimento por `MediaFormats`; duração/dimensões na lista ficam para depois (leitura sob demanda, não na enumeração). |
| Preview | Modo Revisão (precisa de item do catálogo) e `MediaViewerWindow` (aceita qualquer `Photo` com caminho). | Duplo clique em mídia abre `MediaViewerWindow` com um `Photo` transitório; sem segundo visualizador. |
| Arquivos externos | Representados só como caminhos (`ExternalDropPlanner`/`TransferExternalAsync`), sem linha no catálogo. | O painel trabalha com caminhos; o catálogo é consultado só para cor/metadados quando existir. |
| StorageRoot/RelativePath | Somente prompts em `docs/prompts_storageroot`; **não implementado**. | Sem dependência. A comparação usa caminho relativo à pasta de cada painel. |
| Configurações | `LocalApplicationConfiguration` (`settings.json`, chave/valor). | Etapa 6: guardar pastas, modo, tamanho, ordenação e proporção dos painéis. |

## 2. Arquitetura

```
Application/Transfer            (puro, testável sem UI)
  TransferEntry                 registro de um item de pasta (nome, tipo, tamanho, data…)
  FolderLister                  enumeração eficiente de uma pasta (EnumerateFileSystemInfos), com erro tipado
  TransferListing               filtro (nome/extensão) + ordenação (nome, data, tamanho, tipo; pastas primeiro)
  NavigationHistory             voltar/avançar/subir
  Breadcrumbs                   caminho → trechos clicáveis (inclui UNC)
  (etapa 3) FolderComparer      compara dois listados por caminho relativo/tamanho/data
  (etapa 4) IFileTransferService / TransferPlan / ConflictPolicy / TransferProgress

Infrastructure
  ThumbnailService : IFileThumbnailService   cache por caminho+tamanho+data

Wpf/Views
  TransferViewModel             LeftPane, RightPane (+ fila na etapa 4)
  TransferPaneViewModel         um lado: CurrentPath, Items, SelectedItems, SearchText, SortField,
                                SortDescending, FoldersFirst, ViewMode, ThumbnailSize, histórico,
                                comandos Back/Forward/Up/Refresh/Navigate/Choose, estado (carregando/erro/vazio)
  TransferItemViewModel         item exibido (miniatura preguiçosa, textos formatados)
  TransferView / TransferPaneView   View sem lógica de arquivo
```

Princípios: nenhum acesso a disco na View; enumeração e miniaturas em segundo plano com `CancellationToken` (navegar cancela o carregamento anterior); cada painel é uma instância independente (nada compartilhado entre esquerdo e direito); pasta inexistente/offline vira mensagem no painel, não exceção.

## 3. Decisões de comportamento (a confirmar nas próximas etapas)

- **Arrastar**: normal = **copiar**; Shift = **mover**; Ctrl = copiar (mais seguro que o padrão do Explorer, pois cartão/HD de origem não perde arquivos por acidente).
- **Comparação ≠ sincronização**: só marca; nunca apaga "sobras" nem espelha. Hash SHA-256 só sob demanda ("Verificar conteúdo").
- **Colisão**: Substituir / Ignorar / Manter ambos (`nome (1).ext`) / Comparar / Cancelar, com "aplicar aos próximos". Nunca sobrescreve em silêncio.
- **Cancelar**: arquivo parcial vira `.tmp` e é apagado; nunca é contado como transferido.

## 4. Etapas

1. Auditoria + este plano — feito.
2. **Painéis (feito):** navegação "Transferência", dois painéis com `GridSplitter`, navegação (voltar/avançar/subir/atualizar/breadcrumb/caminho editável/escolher pasta), busca e ordenação independentes, lista e miniaturas (tamanho ajustável), seleção com contagem/tamanho, duplo clique (pasta entra; mídia abre o visualizador).
3. Comparação visual e filtros.
4. Copiar/mover (botões centrais), progresso, cancelamento, colisões, verificação opcional.
5. Drag & drop (entre painéis, em subpastas, do Explorer) e integração de cores com a Biblioteca.
6. Persistência do layout, atalhos (F2, Del, Ctrl+C/X/V, F5, Backspace, Enter, Space), estados vazios e ajustes de desempenho.

### 4.1 Organização (adiantada das etapas 5 e 6)

- **Pastas e arquivos, nos dois painéis:** "Nova pasta" (botão, botão direito no vazio, Ctrl+Shift+N; sugere "Nova pasta", "Nova pasta (2)"…), "Renomear" (F2; arquivo mantém a extensão e leva o `.xmp`), "Excluir" (Del; **sempre Lixeira**, pasta com todo o conteúdo, com confirmação). Regras de nome em `TransferNames` (puro, testado).
- **Catálogo acompanha** (`ITransferOrganizer` → `Infrastructure/Files/TransferOrganizer`): renomear pasta/arquivo reescreve `CurrentPath` dos itens catalogados (`ICatalogRepository.UpdatePathsAsync`) — mesmo Id, tags, coleções, cor e histórico; excluir marca como ausente. A Biblioteca é avisada (`CatalogChanged`) e relê o catálogo.
- **Outro painel coerente:** mesma pasta → atualiza; dentro da pasta renomeada → segue o novo nome; dentro da pasta excluída → sobe para a pasta que a continha.
- **Cores:** a mesma `PhotoColor` da Biblioteca, lida por pasta numa consulta leve (`GetEntriesUnderAsync`). Miniatura com o tom da etiqueta + bolinha; lista com a linha tingida + bolinha. Marcar: botão "Cor", botão direito ▸ Cor, teclas 1–6 (0 remove). Arquivo fora do catálogo: pergunta antes de **adicioná-lo ao catálogo no lugar** (nada é copiado); recusar marca só os já catalogados. Pastas não recebem cor (não há onde guardar). Cor mudada aqui atualiza os cartões da Biblioteca no lugar; cor mudada na Biblioteca aparece ao voltar para a Transferência.

### 4.2 Recursos de organização (12 itens)

| # | Recurso | Onde está |
|---|---|---|
| 1 | Copiar/mover: botões centrais, arrastar entre painéis/pastas/breadcrumb/Locais (normal = copiar, Shift = mover), do e para o Explorer e a Biblioteca. Progresso (arquivos e bytes), Cancelar, conflito perguntado uma vez (Manter os dois / Ignorar / Substituir → antigo vai para a Lixeira). Cópia em `.pmpart` + renomeia no fim; datas preservadas; `.xmp` junto. Mover no mesmo disco = instantâneo e o catálogo segue. | `ITransferOrganizer.TransferAsync`, `TransferViewModel.RunTransferAsync` |
| 2 | Desfazer (Ctrl+Z / botão): renomear, lote, criar pasta, copiar (cópias → Lixeira), mover, organizar por data (pastas criadas somem se vazias). Excluir não entra (restaurar pela Lixeira). Pilha de 30. | `TransferUndoStack`, `TransferViewModel.UndoAsync` |
| 3 | Renomear no lugar (F2 / nova pasta já abre o nome) e em lote com modelo `{nome} {data} {ano} {mes} {dia} {hora} {seq}` + pré-visualização; trocas de nome resolvidas por nome provisório. | `BatchRenamePlanner`, `BatchRenameDialog`, `MovePathsAsync` |
| 4 | Organizar por data: AnoMês, AnoMês com nome, AnoDia, Dia, Ano-Mês; nesta pasta ou na do outro painel; mover ou copiar; data = catálogo → EXIF → vídeo → modificação. | `DateFolderPlanner`, `OrganizeByDateDialog` |
| 5 | Filtros por tipo (fotos, vídeos, pastas, outros, duplicados) e cor, por painel. | `TransferPaneViewModel.TypeFilter/ColorFilter` |
| 6 | Comparar: Só aqui / Diferente / Igual (nome + tamanho + data ±2 s), resumo e "Só diferenças". Nunca sincroniza. | `FolderComparer` |
| 7 | Locais: pastas do Windows, discos (espaço livre), fixadas (alfinete no painel) e recentes; abrem no painel ativo e aceitam arquivos soltos. | `TransferViewModel.Locations/Pinned/Recent` |
| 8 | Layout guardado: pasta, modo, miniatura, ordenação, detalhes de cada painel, proporção do divisor, Locais visível, fixadas e recentes. | `ITransferSettings` → `TransferSettingsStore` (settings.json) |
| 9 | Ctrl+C / Ctrl+X / Ctrl+V com o formato do Explorer (funciona nos dois sentidos); colar na mesma pasta duplica. | `IFileClipboard` → `WpfFileClipboard` |
| 10 | Cor de pasta (tabela `FolderColors`), acompanha renomear/mover e some ao excluir. | `ITransferRepository` |
| 11 | Duplicado no catálogo: só calcula SHA-256 quando há item do mesmo tamanho; reaproveita o hash guardado. Selo, dica com os locais e filtro. | `ITransferOrganizer.FindDuplicatesAsync` |
| 12 | Painel de detalhes: resolução, data da foto, câmera, lente, exposição, GPS, duração, status no catálogo, duplicata, comparação; vários itens = resumo. | `ITransferOrganizer.GetDetailsAsync` |

Limitações: arrastar com Shift para o Explorer move pelo próprio Explorer (o catálogo não acompanha esse caso; Atualizar na Biblioteca marca como ausente). Mover pasta entre discos diferentes é feito arquivo a arquivo (com progresso).

## 5. Melhorias futuras (fora do escopo)

- Tratar RAW + `.xmp` como grupo na transferência.
- Duração/dimensões na lista (leitura sob demanda por linha visível).
- Barra lateral de atalhos (Este computador, Downloads, Storage Roots, recentes).
