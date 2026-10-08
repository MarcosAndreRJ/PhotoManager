# Estado geral

Fase atual: **Coleções e subcoleções — Etapa E concluída (Arrastar coleções, raiz, ciclos e fechamento)** (188 testes verdes em 5 passes consecutivos, 0/0 em Debug/Release). Frente de coleções integralmente finalizada.

## Fases

- [x] Fase 0 — Auditoria e planejamento
- [x] Fase 1 — Shell funcional da aplicação
- [x] Fase 2 — Catálogo local de fotos
- [x] Fase 3 — Organização do acervo
- [x] Fase 4 — Operações de arquivo
- [x] Refatoração visual (`docs/UI_REFACTOR_PLAN.md`)
- [x] Fase 5 — Leitura de metadados (somente leitura)
- [x] Fase 6 — Edição segura de metadados (JPEG)
- [x] Fase 7 — Edição em lote + presets
- [x] Fase 8 — Workflow microstock local
- [x] Fase 9 — Agências e histórico de envio manual
- [x] Fase 10 — Duplicatas exatas
- [x] Nova etapa de organização — ETAPA 2: Sem coleção e ícone
- [x] Coleções e subcoleções — Etapa A: Auditoria e Plano Mestre (`docs/COLLECTIONS_HIERARCHY_PLAN.md`)
- [x] Coleções e subcoleções — Etapa B: "Todas", "Sem coleção", contadores e correção do combo
- [x] Coleções e subcoleções — Etapa C1: Modelo, Migração e Serviço (sem UI nova)
- [x] Coleções e subcoleções — Etapa C2: Árvore e CRUD na UI
- [x] Coleções e subcoleções — Etapa D: Arrastar fotos para coleções (adicionar e mover)
- [x] Coleções e subcoleções — Etapa E: Arrastar coleções, raiz, prevenção de ciclos e fechamento

## Auditoria das Fases 1–4 (01/10/2026)

O handoff anterior declarava as fases 1–4 "concluídas", mas **nenhum build havia sido executado** (não havia SDK). Ao compilar pela primeira vez havia **45 erros em 4 causas**:

| Problema | Correção |
|---|---|
| `UseWPF` remove `System.IO` dos implicit usings (Infrastructure e Wpf: `File`/`Path` inexistentes) | `Using Include="System.IO"` nos dois csproj |
| `BeginTransactionAsync` retorna `DbTransaction`; os helpers esperam `SqliteTransaction` | cast explícito em `SqliteCatalogRepository.SaveAsync` |
| Method group com parâmetro opcional não converte para `Func<Photo, Task>` | lambda em `RecycleSelectedAsync` |
| `System.IO` no projeto WPF | idem primeiro item |

Outros achados corrigidos:

- Mensagens de sucesso eram **sobrescritas** logo em seguida pelo refresh ("Organização salva" nunca aparecia).
- `CopySelectedAsync` com "adicionar ao catálogo" **não atualizava a lista**.
- `LibraryViewModel` era recriada a cada navegação (perdia filtros, seleção e miniaturas).
- `ImportFolderCommand` chamava `ImportFolderAsync("")`, ou seja, não fazia nada (removido; a importação usa o diálogo no code-behind).
- `StatusText` do card chamava `File.Exists` a cada binding.
- Miniaturas carregadas em série **antes** de mostrar a grade; grade sem virtualização.
- Preview carregava o arquivo original em resolução cheia, de forma síncrona e travando o arquivo.
- Corrida real: mover/renomear falhava se o preview/miniatura estivesse lendo o arquivo naquele instante (reproduzido nos testes) → retentativa.
- `FileLoggerProvider` descartava a exceção, escondendo a causa de erros de UI.
- Pacote `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 com vulnerabilidade alta (NU1903) → `Microsoft.Data.Sqlite` 10.0.12 (traz 2.1.12).

Achado **não corrigido** (sem impacto atual): a tabela `Categories` nunca é populada — categoria é gravada como texto em `Photos.CategoryName`. Por isso `GetCategoriesAsync` retorna sempre vazio; a UI deriva as categorias das fotos. Decidir na Fase 6/7 se categoria vira entidade.

## Situação real das fases 1–4

| Fase | Situação |
|---|---|
| 1 Shell/navegação | Completa e funcionando (agora com a nova barra superior) |
| 2 Catálogo | Completa: importação recursiva idempotente, tolerante a arquivo corrompido, dimensões, thumbnails, ausentes |
| 3 Organização | Completa: categoria, tags, coleções, nota, avaliação, favorito, busca/filtros, edição em lote |
| 4 Operações | Completa: mover, copiar (com/sem catálogo), renomear, template em lote, Lixeira; sem sobrescrever destino |

Nada é simulado. Recursos sem implementação (Metadados, Microstock, Ferramentas, Configurações) mostram texto honesto de "será construído na fase N".

## Build e testes (verificado)

- SDK 10.0.401, `dotnet restore` / `dotnet build` (Debug e Release): **0 erros, 0 avisos**.
- `dotnet test`: **118 aprovados, 0 falhas** (5 execuções seguidas); Debug e Release com 0 erros e 0 avisos.
- Aplicação executada de verdade com catálogo de exemplo isolado (`PHOTOMANAGER_ROOT`), em 1100×650, 1366×768 e 1920×1040; navegação, seleção, preview, abas, filtro por sidebar e favorito verificados.

### Testes (`tests/PhotoManager.Tests`)

- `LibraryRegressionTests` (10): importação, corrompido, persistência, filtros, lote, mover (identidade + sem sobrescrever), copiar, renomear, Lixeira.
- `LibraryViewTests` (7): sidebar, filtros em memória, favorito, anterior/próxima + preview, seleção preservada, miniaturas.
- `UiSmokeTests` (4): janela real populada, todos os `ControlTemplate`/`DataTemplate` instanciam, navegação entre as 5 áreas.

## Não testado automaticamente

Diálogos de pasta, caixa de confirmação da Lixeira e cliques reais em Mover/Copiar/Excluir (cobertos pelos testes de ViewModel e pelo checklist manual). WEBP depende do codec do Windows.

## Fase 5 — Leitura de metadados (concluída)

**Biblioteca:** MetadataExtractor 2.9.3 (nenhum parser próprio). **Somente leitura**: o arquivo é aberto com `FileShare.ReadWrite|Delete` e há teste garantindo que os bytes não mudam.

| Camada | Arquivo |
|---|---|
| Application | `Metadata/IMetadataReader.cs` (`PhotoMetadata`, `IMetadataReader`) |
| Infrastructure | `Metadata/MetadataExtractorReader.cs` |
| Wpf | `Views/MetadataViewModel.cs`, `Views/MetadataView.xaml(.cs)`; DI em `App.xaml.cs`; ligação em `MainViewModel` |

Campos lidos quando existem: título, descrição, palavras-chave, autor, copyright (XMP → IPTC → EXIF/XP*, nessa prioridade; palavras-chave são a união sem duplicar), câmera, lente, ISO, exposição, abertura, distância focal, data da foto, GPS. A tela mostra de quais blocos vieram (EXIF · IPTC · XMP · GPS), "—" para campos ausentes, mensagem amigável para arquivo corrompido/ausente, e "Abrir no mapa" (OpenStreetMap, só ao clicar) quando há GPS.

Comportamento: a tela usa a mesma seleção/lista/preview da Biblioteca (anterior/próxima funcionam em ambas); só lê arquivos enquanto a área Metadados está ativa; leituras obsoletas são descartadas.

Testes novos (9): EXIF real, ISO, XMP+IPTC com prioridade e união de palavras-chave, arquivo sem metadados, corrompido/ausente, leitura não altera o arquivo, ViewModel (segue a navegação, inativa não lê, arquivo ausente).

Não coberto por teste automático: GPS, lente, abertura, exposição e distância focal (o encoder do WPF não grava racionais facilmente; usam as descrições do MetadataExtractor). Conferir com fotos reais de câmera/drone no checklist.

## Fase 6 — Edição segura de metadados (concluída)

**Escopo:** título, descrição, palavras-chave, autor e copyright, gravados como **XMP (dc:\*)** e **IPTC-IIM (UTF-8)** em arquivos **JPEG**. Fotos PNG/WEBP continuam somente leitura (a tela explica o motivo). RAW (`.cr2`) não é catalogado nem editável (ver `KNOWN_LIMITATIONS.md`).

**Abordagem:** nenhuma biblioteca disponível grava IPTC/XMP sem reescrever o EXIF; por isso a escrita é feita no nível de segmentos JPEG (`JpegMetadataWriter`), com `XmpCore` para montar/mesclar o pacote XMP. Só o segmento XMP (APP1) e o recurso IPTC (0x0404 do APP13) são substituídos; **EXIF, ICC, tabelas e dados da imagem não são tocados**. Propriedades XMP e datasets IPTC não relacionados (cidade, país, Lightroom/Photoshop…) são preservados.

**Fluxo seguro (nesta ordem):**
1. lê o arquivo e monta os novos bytes em memória;
2. grava `arquivo.pm-tmp`;
3. **valida** o temporário: bytes idênticos ao esperado; todos os segmentos fora de XMP/IPTC idênticos e na mesma ordem; dados da imagem idênticos; imagem decodifica com as mesmas dimensões; releitura independente (só XMP+IPTC) devolve exatamente os campos pedidos;
4. confere que o original não mudou desde a leitura (senão aborta);
5. `File.Replace` com backup temporário (com retentativa), remove o backup;
6. o serviço atualiza tamanho/data no catálogo e registra a versão.

Qualquer falha antes do passo 5 deixa o original intacto e remove os temporários (testado).

**Versionamento:** `Photos.MetadataVersion` (coluna adicionada por migração idempotente; bancos antigos preservados — testado) e tabela `MetadataHistory` (versão, data, valores, nota com os campos alterados). A primeira edição grava também a versão 0 ("Original"). Se nada mudou, não toca no arquivo e não cria versão.

**Tela (Metadados):** foto grande à esquerda; à direita título, descrição, palavras-chave como chips (Enter/vírgula adiciona, × remove, Limpar), autor, copyright, contadores (`n/200`, `n/2000`, `n/50`… vermelhos ao ultrapassar, sem bloquear), EXIF e GPS somente leitura, histórico de versões; barra fixa **Salvar alterações · Reverter · Copiar · Colar**. Trocar de foto com alterações pendentes pergunta antes de descartar; edições pendentes sobrevivem à troca de aba.

**Mudança na Fase 5:** palavras-chave agora vêm da primeira fonte que tiver valores (XMP → IPTC → EXIF XP), em vez da união — a união ressuscitaria palavras apagadas.

| Camada | Arquivos |
|---|---|
| Domain | `Photo.MetadataVersion` |
| Application | `Metadata/MetadataEditing.cs` (`MetadataEdit`, contratos), `Metadata/MetadataEditService.cs` |
| Infrastructure | `Metadata/JpegMetadataWriter.cs`; `MetadataExtractorReader` (modo sem fallback EXIF); pacote `XmpCore` |
| Persistence | `MetadataHistory`, coluna `MetadataVersion`, `RecordVersionAsync`/`GetHistoryAsync` |
| Wpf | `MetadataViewModel` (edição), `MetadataView.xaml(.cs)`, DI |

**Testes novos (20):** escrita preservando EXIF/pixels; IPTC em UTF-8; preservação de XMP/IPTC alheios; limpar campos; falhas deixam original intacto e sem temporários; arquivo bloqueado; decodificação pós-escrita; serviço (versões só em mudança real, histórico, catálogo); ViewModel (round-trip, reverter, confirmar descarte, rascunho entre abas, copiar/colar, não-JPEG, contadores, falha); migração de banco antigo.

**Validado com arquivos reais:** as 3 CR2 em `docs/Fotos` (leitura: câmera, lente, ISO, exposição, abertura, distância focal, data, autor XMP) e um JPEG de 2,7 MB (preview embutido de uma CR2) passando pelo pipeline de escrita.

**Bug encontrado e barrado pela validação:** o `XmpCore` antepõe um BOM (U+FEFF) ao pacote; dentro do JPEG isso inutiliza o arquivo para o decodificador do Windows. A etapa de validação impediu a gravação (nenhum arquivo foi alterado) e o BOM passou a ser removido.

## Fase 7 — Edição em lote (concluída) + ajustes pedidos

### Ajustes pedidos junto com a fase
1. **Combos enormes → corrigido.** O `ComboBox` usava o template padrão do Windows (gradiente cinza e altura exagerada). Agora há um template próprio, plano, com 32 px de altura, borda arredondada, chevron e popup com sombra (`Themes/Inputs.xaml`). Cuidado registrado no handoff: com template próprio, `DisplayMemberPath` não aparece na caixa de seleção; usar `ItemTemplate`.
2. **CR2 não reconhecido → corrigido (RAW somente leitura).** O catálogo agora indexa `.cr2` (`ImageFormats.Raw`, uma lista só para estender). Miniatura, preview e dimensões vêm do JPEG embutido no RAW (`RawPreview`: lê só os diretórios TIFF e o trecho do JPEG, nunca os ~28 MB inteiros; respeita a orientação) e as dimensões reais do sensor vêm do EXIF (5184×3456 nas fotos de teste). Metadados EXIF/XMP de RAW são lidos (Fase 5). **RAW nunca é gravado**: a edição fica bloqueada com explicação.  Outros formatos RAW (NEF, ARW, DNG…) não foram adicionados por não haver arquivo para validar; basta incluí-los em `ImageFormats.Raw` se o contêiner for TIFF little-endian com JPEG embutido (testar antes).
3. **Subpastas em árvore.** A seção *Pastas* da barra lateral é um `TreeView`: importar uma pasta mostra a ramificação de subpastas, com contagem recursiva por nó. A cadeia inicial de pastas sem fotos próprias e com um só filho é recolhida numa raiz (rótulo abreviado, caminho completo na dica). Clicar numa pasta filtra pela pasta **e subpastas**; o que o usuário expandiu/recolheu é lembrado entre recarregamentos. Importação agora ignora pastas inacessíveis em vez de abortar.

### Fase 7: o que existe
- **Onde:** botão **Metadados** na barra de ferramentas da Biblioteca (habilitado com ≥ 1 foto selecionada) abre a janela *Editar metadados em lote*.
- **Operações por campo, independentes:** Título (Manter, Substituir) · Descrição (Manter, Substituir, Acrescentar) · Palavras-chave (Manter, Substituir, Adicionar, Remover, Limpar) · Autor e Copyright (Manter, Substituir). "Manter" nunca toca o campo; cada foto preserva seus próprios valores nos campos mantidos (testado: títulos diferentes permanecem diferentes).
- **Pré-visualizar** mostra, por foto, o que mudaria (Será alterada / Sem mudança / Ignorada / Erro) sem gravar nada.
- **Aplicar** pede confirmação, grava foto a foto pelo mesmo pipeline seguro da Fase 6 (temporário → validação → substituição, versão e histórico por foto), com progresso e botão **Cancelar gravação**. Uma falha não interrompe as demais; PNG/WEBP/RAW/ausentes são ignorados com o motivo. Rodar duas vezes o mesmo plano não altera nada nem cria versões.
- **Presets:** salvar o plano atual com um nome (atualiza se o nome já existir, sem diferenciar maiúsculas), carregar e excluir; persistidos no banco (`MetadataPresets`), visíveis em qualquer janela futura. Ex.: *Microstock Marcos* = Autor "Marcos André" + Copyright "© Marcos André" + Adicionar keywords "Brazil, travel".

| Camada | Arquivos |
|---|---|
| Application | `Metadata/BatchMetadata.cs` (`BatchMetadataPlan`, operações, presets, resultados), `Metadata/BatchMetadataService.cs`, `Catalog/ImageFormats.cs` |
| Infrastructure | `Images/RawPreview.cs`, `Images/ImageLoader.cs` (+ `ThumbnailService` usando-os) |
| Persistence | tabela `MetadataPresets`; `IMetadataPresetRepository` |
| Wpf | `BatchMetadataViewModel`, `BatchMetadataWindow.xaml(.cs)`, `FolderNode`, `Themes/Tree.xaml`, `Themes/Inputs.xaml` (ComboBox) |

### Testes (79 no total; +29 nesta rodada)
Plano (manter/substituir/acrescentar/adicionar/remover/limpar, validação, JSON), presets (CRUD, nome único), serviço em arquivos reais (valores por foto preservados, EXIF intacto, idempotência, ignorados/falhas, pré-visualização sem gravar, plano inválido, progresso/cancelamento), ViewModel (contagem editável, validação, confirmação, presets, janela real em STA), CR2 reais de `docs/Fotos` (extração, dimensões, importação, somente leitura), árvore de pastas (ramificação, contagens, subpastas, prefixo, expansão preservada, mover). Debug e Release: 0 erros e 0 avisos; suíte repetida 5× sem falhas.

### Defeitos encontrados nesta rodada (todos corrigidos)
- Barra de progresso com binding bidirecional em propriedade somente leitura (derrubava a janela; pego pelo teste de fumaça).
- Callback de progresso tardio sobrescrevendo a mensagem final do lote.
- Combos de operação exibindo o nome do tipo (template próprio do ComboBox).
- Lote tratava foto apagada do disco como falha de gravação em vez de "ausente".

## Fase 8 — Workflow microstock local (concluída)

Implementado sem rede e sem upload: `ValidationProfile`, regras puras de validação, estados de preparação com precedência documentada, cache de metadados por sessão, progresso/cancelamento e a Central de Produção na aba Microstock. O perfil genérico inicial é editável, duplicável e persistido em `ValidationProfiles`; o último perfil não pode ser excluído. A persistência de agências e histórico manual foi adicionada na Fase 9.

| Camada | Arquivos |
|---|---|
| Application | `Microstock/MicrostockModels.cs`, `Microstock/MicrostockEvaluationService.cs` |
| Persistence | `SqliteCatalogRepository.cs` e tabela `ValidationProfiles` (migração idempotente + seed genérico) |
| Wpf | `Views/MicrostockViewModel.cs`, `Views/MicrostockView.xaml`, navegação/DI em `MainViewModel` e `App` |
| Tests | `MicrostockTests.cs` (regras, precedência, migração, CRUD, volume de 2.000 fotos, cancelamento) |

Decisão de cache: o resumo não é gravado no banco; fica em memória e é invalidado naturalmente por `Photo.MetadataVersion`, tamanho e data de modificação. Isso evita persistir metadata obsoleta e mantém a migração pequena.

Resultado desta fase: 85 testes aprovados, incluindo o smoke de navegação que instancia a tela Microstock. Debug/Release foram verificados sem erros/avisos; execução visual manual da nova tela ainda permanece pendente.

Defeito preexistente encontrado e corrigido durante a validação: `App.OnStartup` bloqueava o Dispatcher em `InitializeAsync().GetAwaiter().GetResult()`. O bootstrap agora aguarda a inicialização do SQLite de forma assíncrona antes de mostrar a janela; o processo isolado criou a janela e o banco sem usar dados do usuário. A captura visual nativa não ficou disponível neste ambiente.

## Fase 9 — Agências e histórico de envio manual (concluída)

Implementado sem rede e sem automação de upload. O SQLite agora cria/migra `Agencies` e `UploadRecords` de forma idempotente, sem perder fotos ou perfis existentes, e sem apagar histórico. Cinco agências iniciais são semeadas (`Adobe Stock`, `Shutterstock`, `Depositphotos`, `Dreamstime`, `123RF`).

O histórico é append-only: o estado atual é o último registro por foto/agência; marcar como enviado, erro, rejeitado ou desfazer sempre cria um novo registro. O registro preserva versão de metadata, nome remoto, motivo, observações e data. Agências podem ser adicionadas, renomeadas, ativadas/desativadas e reordenadas; o conjunto ativo alimenta o cálculo `UploadedToAll`/`PartiallyUploaded`.

Na Central de Produção, a seleção múltipla permite marcar envio manual, erro, rejeição e desfazer com confirmação, além de filtrar por agência/estado e consultar o histórico da foto. Nenhuma chamada de rede ou credencial foi adicionada.

| Camada | Arquivos |
|---|---|
| Application | `Microstock/UploadModels.cs`, extensão de `MicrostockModels.cs` e `MicrostockEvaluationService.cs` |
| Persistence | `SqliteCatalogRepository.cs`, tabelas `Agencies`/`UploadRecords` e migração/seed |
| Wpf | `MicrostockViewModel.cs`, `MicrostockView.xaml(.cs)`, DI em `App.xaml.cs` e `MainViewModel` |
| Tests | `UploadHistoryTests.cs` + smoke existente da Central |

Resultado desta fase: **90 testes aprovados**, Debug/Release com 0 erros e 0 avisos; a execução visual manual da tela permanece pendente por indisponibilidade da superfície nativa neste ambiente.

## Fase 10 — Duplicatas exatas (concluída)

Implementado SHA-256 em streaming com pré-filtro por `FileSize`: arquivos cujo tamanho não se repete não são abertos pelo hasher. Hashes válidos são reutilizados por tamanho/data real do arquivo; mudanças de conteúdo ou metadata invalidam o cache. Cada resultado é salvo em `Photos.ContentHash`, `HashedAtSize` e `HashedAtModified`, permitindo cancelar e retomar sem perder o trabalho já persistido.

A aba Ferramentas mostra grupos com miniaturas, caminho, tamanho e organização. Ações explícitas: escolher a cópia mantida, mover outra, enviar outra para a Lixeira com confirmação, ignorar o grupo e mesclar opcionalmente tags/coleções/nota/avaliação antes da exclusão. A smart list **Duplicadas** aparece na Biblioteca com contagem e respeita grupos ignorados persistidos em `DuplicateIgnores`.

Arquivos ausentes e falhas de leitura são ignorados individualmente; nunca há exclusão automática nem comparação par a par. O fluxo exige manter ao menos uma cópia antes de mover/excluir.

| Camada | Arquivos |
|---|---|
| Domain | `Photos/Photo.cs` — assinatura do hash e invalidação |
| Application | `Catalog/DuplicateModels.cs` — streaming, serviço, contratos e grupos |
| Persistence | `SqliteCatalogRepository.cs` — colunas de hash, `DuplicateIgnores` e consultas |
| Wpf | `Views/PageViewModels.cs`, `Views/ToolsView.xaml(.cs)`, `LibraryViewModel.cs`, `MainViewModel.cs` e DI |
| Tests | `DuplicateTests.cs` + smoke da navegação |

Resultado desta fase: **95 testes aprovados**, Debug/Release verificados sem erros/avisos; a conferência visual manual completa da tela permanece pendente por indisponibilidade da superfície WPF nativa.


---

## Rodada de UX pós-Fase 10 — Etapa 2 concluída (Biblioteca, seleção, painel direito)

Nenhuma fase nova; banco, migrations, serviços e as telas de Microstock/Duplicatas/Metadados não foram tocados. Baseline antes: 95 testes. Agora: **104 testes, 0 falhas** (5 execuções seguidas), Debug e Release com 0 erros e 0 avisos. Plano e diagnóstico completos em `UI_REFACTOR_PLAN.md` (seção "Rodada 2").

### O que mudou
- **Checkbox em cada card** (canto superior esquerdo, sempre visível; marcado = azul cheio com check). Está ligado ao `IsSelected` do item da grade: continuam valendo clique, Ctrl, Shift, Ctrl+A e a virtualização. Cartão selecionado ganha borda/fundo azuis mais fortes.
- **Barra de seleção** acima da grade: *Selecionar tudo*, *Limpar seleção* e um contador em destaque ("3 selecionadas", azul quando há seleção). A barra de ferramentas ficou enxuta (*Adicionar pasta* + seleção): Mover/Copiar/Excluir/Metadados saíram dela e foram para as abas, que é onde se vê em quantas fotos a ação vai agir.
- **A VM agora conhece a seleção** (`SelectedCards`, `IsSingleSelection`, `IsMultiSelection`, `SelectionTitle`, `TargetText`, `SelectionDetails`, `EditableSelectionCount`, `SelectionThumbnails`). Depois de qualquer reconstrução da grade (filtro, recarga, aplicar em lote) a seleção é remarcada (`SelectionRestoreRequested`).
- **Painel direito conforme a seleção:** 0 fotos → estado vazio explicando como selecionar · 1 foto → preview e detalhes (como antes) · 2+ fotos → cartão "N fotos selecionadas" com tipos, tamanho total e miniaturas.
- **Abas (cada uma com uma responsabilidade, todas dizendo o alvo e desabilitando sem seleção):**
  1. *Informações* — detalhes de 1 foto ou orientação para 2+.
  2. *Organização* — 1 foto: edita e salva (como antes); 2+: formulário em lote (categoria, adicionar tags, adicionar a coleções, avaliação, favorito, nota; **vazio/"Manter" não altera**), com resumo do impacto ("Serão alteradas 3 foto(s): categoria “Viagens”, +2 tag(s)…") e botão **Aplicar a N fotos**.
  3. *Arquivos* — Mover para…, Copiar para… (+ "Adicionar as cópias ao catálogo"), Renomear (1 foto: novo nome; várias: template), Enviar para a Lixeira… (botão de perigo, com confirmação que mostra a quantidade).
  4. *Metadados* — "X de N fotos são JPEG e podem ser gravadas", o que cada campo permite (manter/substituir/acrescentar/adicionar/remover/limpar), aviso de pré-visualização e botão **Editar metadados de N foto(s)…** (a edição em si será refeita na Etapa 4).
- Painel direito 340 → 380 px; verificado em 1100×700 e 1366×768.

### Código novo
`Application/Catalog/OrganizationBatch.cs` (lote de organização, função pura), `ImageFormats.IsJpeg`, estilo `CardCheckBox` (`Themes/Cards.xaml`), seleção/organização em lote em `LibraryViewModel`, `LibraryView.xaml(.cs)` reescrito (code-behind só com diálogos e ListBox).

### Testes novos (9)
Lote de organização (manter, adicionar sem duplicar/remover, rating/favorito/nota, idempotência); estado da seleção; aplicar em lote persiste e informa impacto; "já estavam assim"; renomear 1 foto vs template; restauração da seleção; **teste em WPF real**: marcar caixas = seleção múltipla sem Ctrl, painel mostra "2 fotos selecionadas", Selecionar tudo/Limpar, e a seleção permanece depois de aplicar em lote. Um teste de fumaça antigo foi estabilizado (esperava 400 ms fixos pelo preview; agora espera com limite) depois de falhar uma vez com a máquina carregada.

### Não verificado / observações
- Cliques reais do mouse nas caixas e botões não foram exercitados (as capturas e testes usam UI Automation/binding); está no checklist.
- Shift+clique para faixa depende do comportamento nativo do `ListBox` (não há teste automatizado).
- O botão de Metadados abre a janela modal da Fase 7 como estava; a nova experiência é a Etapa 4.
- Mover/Copiar/Excluir deixaram de estar na barra superior (decisão de UX para evitar duplicidade); atalhos de teclado não foram adicionados.


---

## Rodada de UX pós-Fase 10 — Etapa 3 concluída (modo de revisão / navegação de preview)

Só UX: nenhuma fase nova, nenhuma mudança de banco/serviços/schema. **118 testes, 0 falhas** (5 execuções seguidas); Debug e Release com 0 erros e 0 avisos (baseline antes da etapa: 104).

### O que existe
Um **modo de revisão** dentro da Biblioteca, que substitui a grade enquanto ativo:
- **Como abrir:** duplo clique numa foto (fora da caixa de seleção e do coração), Enter na grade, ou o botão **Revisar** sobre o preview do painel direito. **Voltar** (ou Esc) retorna à grade com a mesma foto selecionada.
- **Barra superior (escura):** Voltar · anterior/próxima · nome e índice formatado ("8 de 4.820") · zoom −/% /+ · **Ajustar** · **100 %** · **Tela cheia** · favorito (grava na hora) · avaliação por estrelas (grava na hora).
- **Visualizador (`Controls/ZoomViewer`)**: Ajustar (nunca amplia além de 100 %), 100 % em pixels reais, roda do mouse com zoom ancorado no cursor, arrastar para mover, duplo clique alterna Ajustar/100 %. Troca de foto volta a "Ajustar"; a prévia sendo substituída pela imagem completa mantém o enquadramento. Setas grandes sobre a imagem; selo com dimensões/tamanho/tipo; selo "Resolução completa".
- **Imagem:** aparece primeiro o preview reduzido já usado na Biblioteca; se o usuário **parar** na foto (≈ 220 ms), a resolução natural é decodificada em segundo plano (`ImageLoader.LoadFull`, limite de 40 MP, nunca amplia). Passar rápido pelas fotos não dispara decodificações pesadas.
- **Filmstrip inferior virtualizado** (`VirtualizingStackPanel` horizontal, miniaturas do cache existente), com a foto atual destacada e rolagem automática; clicar numa miniatura troca a foto.
- **Tela cheia:** a janela fica sem moldura e maximizada, a barra de navegação e o painel/filmstrip somem (fica a foto e a barra superior). Esc sai da tela cheia; um segundo Esc sai da revisão.
- **Atalhos:** ← → PgUp PgDn Espaço · Home/End · F (ajustar) · 1 (100 %) · + − · Enter/F11 (tela cheia) · Esc.
- **Painel lateral (somente leitura) com 4 abas:**
  - *Informações* — arquivo, dimensões, datas, tags, coleções e nota;
  - *Metadados* — título, descrição, palavras-chave (chips), autor, copyright, câmera/lente, exposição, data + botão **Editar na tela Metadados**;
  - *Microstock* — status calculado pelo serviço existente, pendências do perfil ativo, envios por banco + botão **Abrir Central de Produção**;
  - *Histórico* — versões dos metadados e histórico de envios.
  Os dados só são lidos enquanto a revisão está ativa e acompanham a navegação.

### Código
`Controls/ZoomViewer.cs`, `Views/ReviewView.xaml(.cs)`, `Views/ReviewViewModel.cs` (reaproveita `IMetadataReader`, `IMetadataEditService`, `IValidationProfileRepository`, `IMicrostockEvaluationService`, `IUploadHistoryService`, `INavigationService`), estilos `DarkToolButton` e `FilmstripItem`, `ImageLoader.LoadFull`; `LibraryViewModel` (modo, tela cheia, `SavePhotoAsync`, posição com separador de milhar), `MainViewModel`/`MainWindow` (tela cheia).

### Testes novos (14 + serialização)
Modo (entrar/sair, setas, formatação), tela cheia só em revisão e no shell, dados do painel acompanhando a navegação, nada lido com a revisão inativa, envios/histórico somente leitura, arquivo ausente + botões de abrir outras telas, avaliação gravada na hora, imagem completa substituindo a prévia, `ZoomViewer` (ajustar/100 %/passos, nunca amplia, enquadramento preservado, foto nova volta a ajustar) e um teste em **WPF real** (visualizador, filmstrip com 3 itens, a revisão substitui a grade, teclas, clique no filmstrip, tela cheia escondendo o filmstrip, Esc duplo).
Também: os testes que criam janelas WPF agora rodam **em série** (`[Collection("WpfUi")]`) — a `Application` do WPF é única por processo e a execução paralela causava falhas raras sob carga.

### Não verificado / observações
- Mouse real: duplo clique no card, roda/arrastar no zoom, cliques nos botões e no filmstrip (testado por binding/UI Automation e eventos sintéticos).
- Tela cheia real em monitor e com a barra de tarefas (a lógica de janela está no code-behind e não tem teste automatizado).
- Fotos grandes de câmera (CR2 de ~24 MP, JPEG 40 MP+): memória e fluidez de navegação; foi verificado só com as imagens de exemplo.
- Windows 10: glifos dos novos ícones.
- Itens do mockup **não** incluídos por não serem pedidos/estarem fora do escopo: classificar/rejeitar, excluir pela revisão, mapa/GPS, abrir em nova janela, compartilhar.


---

## Rodada de UX pós-Fase 10 — Etapa 4 concluída (editor de metadados e lote estilo Xpiks)

Só UX/fluxo: sem fases novas, sem mudança de banco/schema/serviços. **118 testes, 0 falhas** (5 execuções seguidas; a suíte leva 40–75 s, mais lenta com a máquina ocupada); Debug e Release com 0 erros e 0 avisos. (Contagem: saíram os testes do editor individual e da janela modal, entraram 20 do novo editor.)

### O que mudou
A aba **Metadados** virou um **Editor** único (Opção B do pedido), substituindo o formulário de 1 foto e a janela modal de lote:
- **Lista das fotos** (as marcadas na Biblioteca, ou a selecionada): cada linha tem miniatura, nome, formato, status e **Título, Descrição, palavras-chave em chips (Enter/vírgula adiciona, × remove, sem repetidas), Autor e Copyright editáveis na própria linha**, com contadores (`n/200`, `n/2000`, `n/50`, vermelhos ao passar do limite) e **campos alterados em azul**. Botões por linha: *Reverter* e *Salvar* (só aparecem quando alterada).
- **Marcar/desmarcar fotos** da lista (caixa na linha + *Marcar todas*/*Desmarcar todas*) para decidir quem recebe as operações globais.
- **Painel "Aplicar às fotos marcadas"** com operação **por campo**: Título/Autor/Copyright (manter, substituir), Descrição (manter, substituir, acrescentar), Palavras-chave (manter, substituir, adicionar, remover, limpar; chips; sem duplicadas). Resumo do impacto antes de aplicar. **A operação muda os rascunhos das linhas na hora — nada vai ao arquivo até "Salvar"**, então o efeito é visível antes de gravar (cada foto mantém os próprios valores nos campos "Manter").
- Atalhos: **Copiar da foto em foco** (título, descrição, palavras-chave, autor e copyright para as demais marcadas), **Eliminar palavras repetidas**, **Limpar** (volta o formulário a "Manter").
- **Presets** (mesmos da Fase 7) no próprio editor.
- **Foto em foco** (a linha onde se digita): miniatura, câmera/lente, exposição, data, GPS (+ "Abrir no mapa") e histórico de versões.
- **Barra inferior:** status, progresso, *Cancelar*, *Reverter tudo* (com confirmação) e **Salvar N alteradas**. Salva foto a foto pelo pipeline seguro (temporário → validação → substituição, versão e histórico), relê o arquivo depois de gravar e mostra "Salva — vN" por linha; **uma falha não interrompe as demais** (a foto com erro continua como rascunho, com o motivo).
- PNG/WEBP/RAW/ausentes aparecem **desabilitados com o motivo** ("Somente leitura (PNG)").
- **Rascunhos não se perdem:** linhas alteradas continuam na lista mesmo que a seleção da Biblioteca mude; linhas sem alteração são substituídas.
- **Leitura preguiçosa e concorrência limitada (4):** os metadados de uma linha só são lidos quando ela aparece (lista virtualizada) ou quando uma operação/salvar precisa dela.

### Fluxo
Biblioteca (marcar fotos) → aba **Metadados** do painel → *Abrir editor com N foto(s)…* (ou aba superior *Metadados*). *Editar na tela Metadados* (revisão) e *Abrir em Metadados* (Microstock) levam ao mesmo editor.
Correção junto: ao voltar de outra aba a Biblioteca agora **restaura a seleção múltipla** (antes a grade nova a reduzia a uma foto).

### Código
Novos: `Views/MetadataEditorViewModel.cs`, `Views/MetadataRowViewModel.cs`, `Views/MetadataView.xaml(.cs)` (reescrita). Removidos: `MetadataViewModel`, `BatchMetadataViewModel`, `BatchMetadataWindow` e seus testes (migrados). Reaproveitados sem mudança: `BatchMetadataPlan`, `IMetadataEditService`, `IMetadataReader`, `IMetadataPresetRepository`. `BatchMetadataService` (aplica direto nos arquivos) continua existindo e testado, mas não é mais usado pela interface.

### Testes (20 no editor + migração)
Alvos e fallback, leitura preguiçosa, somente leitura, edição/contadores/reverter, salvar tudo (só alteradas, versões, EXIF preservado, releitura), falha em uma foto, salvar linha, operações globais (rascunho ≠ arquivo, valores próprios mantidos, só marcadas, adicionar/remover/limpar/substituir/acrescentar, validação), copiar da foto em foco, eliminar repetidas, rascunhos entre seleções, reverter com confirmação, presets, foto em foco (EXIF/histórico), migração de banco antigo, e um teste em **WPF real** (marcar na grade → editor com as mesmas fotos → linhas lidas sob demanda → editar → voltar à Biblioteca com a seleção restaurada).

### Não verificado / limitações
- Digitação e cliques reais do mouse (testado por binding e eventos); fluidez com centenas de linhas e miniaturas grandes (só dezenas testadas).
- Rascunhos **não persistem** ao fechar o aplicativo (ficam na memória; o app não avisa ao fechar com rascunhos).
- Reverter não cobre o arquivo já gravado (não há "restaurar versão" do histórico — os valores continuam guardados).
- O campo vazio de uma operação "Substituir" é recusado (use *Limpar* nas palavras-chave; não há "limpar título/autor" em lote).
- Apagar um campo cujo valor vem do EXIF (Artist/Copyright/ImageDescription/XP*) faz o valor reaparecer ao reler (EXIF não é alterado) — a linha avisa "algum campo continua vindo do EXIF".

---

## Nova etapa de organização — ETAPA 2 concluída

- **Sem coleção** é uma entrada virtual da barra lateral (`LibraryViewModel.NoCollectionKey`); não cria registro no banco. O filtro mostra as fotos cuja coleção está vazia, e a contagem acompanha alterações em memória após salvar.
- O ícone próprio da aplicação está em `src/PhotoManager.Wpf/Assets/PhotoManager.ico`, configurado no `.csproj` e na janela principal. O arquivo atual é um ícone inicial de 32×32; pode ser substituído por um pacote multi-resolução sem mudar o contrato da UI.
- A auditoria e o plano das próximas etapas estão em `docs/DRAG_DROP_AND_FOLDERS_PLAN.md`; o plano de subcoleções está em `docs/SUBCOLLECTIONS_PLAN.md`.
- **Não iniciar ainda:** ETAPA 3 (serviço e CRUD de pastas), drag-and-drop, integração de entrada/saída com o Explorer e subcoleções. Tudo isso aguarda autorização explícita.
- Validação desta entrega: build Debug, teste de smoke da janela WPF, 119 testes automatizados e build Release, todos sem erros.

---

## Coleções e subcoleções — Etapa A concluída

- **Auditoria e Plano Mestre aprovados:** elaborado o documento [`docs/COLLECTIONS_HIERARCHY_PLAN.md`](file:///k:/Trabalho/Projetos/PHOTOMANAGER/docs/COLLECTIONS_HIERARCHY_PLAN.md) com base em `docs/prompt_colecoes/00_ANALISE_E_DECISOES.md` e no prompt da Etapa A.
- **Nenhum código ou schema foi alterado nesta etapa.**
- **Pontos centrais auditados:**
  - Identidade migrará de nome para `Id` (`Photo.CollectionIds`), mantendo `Photo.Collections` apenas como exibição rápida derivada.
  - Recriação de `Collections` com suporte a `ParentCollectionId` e índice único por irmão `(COALESCE(ParentCollectionId, 0), Name COLLATE NOCASE)`.
  - Migração idempotente com cópia de segurança `photomanager.db.pre-colecoes.bak`.
  - Nova interface `ICollectionService` com operações pontuais por ID (sem `SaveAsync` destrutivo) e prevenção de ciclos.
  - Defeito do ComboBox de coleções (`__sem_colecao__`) mapeado e com correção planejada para a Etapa B.
- **Próximo passo cumprido:** Etapa B implementada e validada.

---

## Coleções e subcoleções — Etapa B concluída ("Todas", "Sem coleção", contadores e correção do combo)

- **Itens virtuais e filtros:**
  - Entrada virtual `"Todas"` criada no topo da seção COLEÇÕES: exibe fotos distintas associadas a pelo menos 1 coleção (`Photo.Collections.Count > 0`), com tooltip *"Fotos que pertencem a pelo menos uma coleção"*. Não equivale a toda a biblioteca.
  - Entrada virtual `"Sem coleção"` mantida: exibe fotos sem nenhuma associação (`Photo.Collections.Count == 0`). Nenhuma coleção física é criada na tabela `Collections`.
  - Mútua exclusão rigorosa entre "Todas", "Sem coleção" e coleções reais.
  - `HasActiveFilters` e o botão "Limpar filtros" refletem adequadamente a seleção de "Todas".
- **Separador visual e ordenação:**
  - `SidebarEntry` recebeu `bool isVirtual`, `string? toolTip`, `bool hasSeparatorAfter` e a propriedade auxiliar `ToolTipText`.
  - Ordem na sidebar: `Todas`, `Sem coleção`, separador discreto (`Border` com `Visibility`), seguidos pelas coleções reais em ordem alfabética.
- **Contadores e invariante:**
  - Contagem direta em memória calculada em uma única passada sobre `_all` em `UpdateSidebarCounts()`.
  - Atualização automática em memória ao salvar organização, lote, duplicatas, remoção ou importação.
  - Invariante validada: `Todas.Count + SemColecao.Count == Total de fotos no catálogo` (incluindo arquivos ausentes).
- **Correção de defeito do ComboBox de coleções:**
  - `CollectionChoices` é inicializado com `AnyCollectionChoice = "Qualquer"`.
  - `RefillCollectionChoices` filtra apenas coleções reais (`!e.IsVirtual`), impedindo que a chave interna `__sem_colecao__` vaze para a lista de opções do combo.
- **Testes automatizados (8 novos testes em `CollectionsEtapaBTests.cs`):**
  - `Todas_ReturnsPhotosInAtLeastOneCollection_WithoutDuplicates`
  - `Todas_IsNotTheWholeLibrary`
  - `SemColecao_ReturnsPhotosWithoutAnyAssociation_AndCreatesNoPhysicalCollection`
  - `Counters_AreDirectOnly_AndTodasPlusSemColecaoEqualsTotal`
  - `Counters_UpdateAfterSaveBatchAndDuplicateMerge`
  - `CollectionCombo_DoesNotExposeInternalKeys`
  - `VirtualEntries_AreFlaggedVirtual_AndMutuallyExclusiveWithRealCollections`
  - `RealWindow_CollectionsSidebar_ShowsVirtualItemsAndFiltersGrid` (teste real STA WPF com `[Collection("WpfUi")]`)
- **Validação de build e testes:**
  - `dotnet build -c Debug`: 0 erros, 0 avisos.
  - `dotnet build -c Release`: 0 erros, 0 avisos.
  - `dotnet test`: 5 execuções consecutivas com 127/127 testes verdes (100% aprovados).
- **Próximo passo:** Etapa C1 concluída. A Etapa C2 (Árvore e CRUD na UI) aguarda autorização explícita (`CONTINUE`).

---

## Coleções e subcoleções — Etapa C1 concluída (Modelo, Migração e Serviço — sem UI nova)

- **Modelo e Entidade:**
  - `Collection` criada em `PhotoManager.Domain.Collections` com `Id`, `Name`, `ParentCollectionId?`, `SortOrder`, `CreatedAt`.
  - `Photo` estendida com `CollectionIds : List<long>`; `Photo.Collections` mantida como lista de nomes derivados com desambiguação automática `"Pai / Filho"` para homônimos em ramos diferentes.
- **Migração Idempotente e Confiável (D3):**
  - Implementada em `SqliteCatalogRepository.InitializeAsync`: detecta banco legado flat (sem `ParentCollectionId`).
  - Criação automática de cópia de segurança única `photomanager.db.pre-colecoes.bak` antes de qualquer alteração estrutural.
  - Conexão dedicada com `PRAGMA foreign_keys = OFF` durante recriação da tabela para impedir exclusão acidental em cascata de `PhotoCollections`.
  - Recriação via `Collections_new`, cópia de dados preservando os IDs originais e ordenação alfabética, substituição atômica de tabela e índices `UX_Collections_Parent_Name` e `IX_Collections_ParentCollectionId`.
  - Validações pós-migração: conferência de contagens, zero órfãos e `PRAGMA foreign_key_check`.
  - Idempotência rigorosa comprovada em testes automatizados.
- **Serviço de Aplicação `ICollectionService` e `CollectionService`:**
  - Implementados todos os 10 métodos do contrato oficial: `GetTreeAsync`, `CreateAsync`, `RenameAsync`, `MoveAsync`, `DeleteAsync`, `AddPhotosAsync`, `RemovePhotosAsync`, `MovePhotosAsync`, `GetAncestorsAsync`, `IsDescendantAsync`.
  - Validação de nomes (não vazio, `Trim`, até 100 caracteres, único entre irmãos sem diferenciar maiúsculas/minúsculas).
  - Prevenção abrangente de ciclos em grafo: A→A, A→B→A, A→...→descendente indireto.
  - Limite inegociável de profundidade máxima de 8 níveis respeitado na criação e em movimentações de subárvores.
  - Exclusão em dois modos (`DeleteMode.PromoteChildren` com resolução de conflitos gerando sufixo ` (2)` e `DeleteMode.WithDescendants`).
  - Associação de fotos idempotente com retorno de contagem e movimentação atômica em transação.
- **Independência de Coleções em Relação a `SaveAsync` de Organização (D12):**
  - `IOrganizationRepository.SaveAsync` e `SqliteCatalogRepository.SaveAsync` não tocam em coleções. A alteração de categoria, tags, nota, rating ou favoritos nunca apaga associações a coleções.
- **Ajustes de ViewModel e Pontes Temporárias:**
  - `LibraryViewModel` recebe `ICollectionService?`, filtra por `CollectionId` e mantém `entry.Key` como nome para 100% de compatibilidade com a UI existente.
  - Pontes temporárias criadas em `SavePhotoAsync`, `SaveSelectedAsync`, `ApplyOrganizationBatchAsync` e `ApplyBatchAsync` sincronizando nomes raiz com `ICollectionService`.
- **Testes Automatizados (13 novos testes em `CollectionsEtapaC1Tests.cs`):**
  - Total de testes do projeto elevado de 127 para 140.
  - Cobertura completa: adicionar sem duplicar, mover só da origem, idempotência, criação de subcoleção, mover coleção, impedir ciclos em todos os cenários, mover para a raiz, exclusão com promoção e sufixo de conflito, exclusão com descendentes, garantia de que fotos físicas e registros de Photos nunca são apagados, contadores diretos/Todas/Sem coleção e invariante, homônimos formatados como "Pai / Filho", SaveAsync não tocando em coleções, migração de banco legado flat com `.bak` e idempotência.
- **Validação de build e testes:**
  - `dotnet build -c Debug`: 0 erros, 0 avisos.
  - `dotnet build -c Release`: 0 erros, 0 avisos.
  - `dotnet test`: 5 execuções consecutivas com 140/140 testes verdes (100% aprovados).
- **Próximo passo:** Etapa C2 concluída com sucesso.

### Coleções e Subcoleções — Etapa C2 (Árvore e CRUD na UI) — Concluída em 02/10/2026
- **Árvore Hierárquica na Barra Lateral (`CollectionNode` e `TreeView`):**
  - Implementado `CollectionNode` herdando de `ViewModelBase` com `Id`, `Name`, `ParentId`, `Path`, `DirectCount`, `SubtreeCount`, `ToolTipText`, `Children`, `IsExpanded`, `IsSelected` e método estático `Build()` com ordenação alfabética e cálculo recursivo.
  - Regra inegociável de contadores cumprida: badge do nó exibe `DirectCount` (fotos diretamente associadas); tooltip detalha fotos diretas e em subcoleções.
  - Na barra lateral do `LibraryView.xaml`:
    - Botão `[ + ]` no cabeçalho COLEÇÕES para criar coleção raiz diretamente.
    - Entradas virtuais ("Todas" e "Sem coleção") fixadas no topo com contadores globais.
    - Linha divisória (`Separator`) sutil separando os virtuais da árvore.
    - `TreeView` real estilizado com os tokens de tema existentes (`TreeItemHeaderTemplate`, chevron de expansão, `ContextMenu`).
    - Memorização do estado de expansão de nós entre refiltragens e reconstruções da barra lateral.
- **Operações de CRUD via Menus de Contexto e Teclado:**
  - Context menu no nó da árvore:
    - *Nova subcoleção...*: abre diálogo e ao criar expande automaticamente o nó pai.
    - *Renomear...* (atalho `F2`): valida unicidade entre irmãos e atualiza nó e fotos associadas.
    - *Mover para...*: abre diálogo com seletor hierárquico, desabilitando o próprio nó, descendentes (prevenção de ciclo) e o pai atual.
    - *Excluir* (atalho `Del`): abre diálogo modal informativo com escolha entre promover subcoleções ou excluir subárvore, com aviso explícito de segurança de que arquivos físicos nunca são excluídos.
  - Delegates de diálogo testáveis (`ShowNameDialog`, `ShowDeleteDialog`, `ShowMoveDialog`, `ShowMessage`) desacoplados do code-behind, permitindo automação de testes sem abertura de janelas interativas.
- **Diálogos Modais WPF Criados:**
  - `CollectionNameDialog` com foco automático no TextBox, `OkButton.IsDefault = true`, validação em tempo real e feedback de erro.
  - `DeleteCollectionDialog` com contadores de impacto, botões de opção claros e mensagem explícita: "Nenhum arquivo físico será excluído."
  - `MoveCollectionDialog` com árvore identada e indicação visual de destinos desabilitados e motivos (própria coleção, subcoleção, pai atual).
- **Substituição de Campos de Texto por Seletores Estruturados (Painel de Organização):**
  - Painel de foto individual: campo de texto livre substituído por chips visuais com botão `(×)` para remoção imediata e ComboBox seletor "Adicionar à coleção ▾" que lista apenas as coleções às quais a foto ainda não pertence.
  - `SaveSelectedAsync` não sobrescreve nem remove coleções da foto.
  - Painel de organização em lote: campo de texto substituído por chips de coleções a adicionar e ComboBox seletor; `OrganizationBatch` migrado para operar por IDs (`CollectionsToAdd : IReadOnlyList<long>`).
  - Sincronização automática do catálogo em `ReloadAsync` chamando `SyncPhotoCollectionsAsync(_all)` para garantir consistência total de `CollectionIds` e `Collections` mesmo após recargas ou refiltragens.
- **Testes Automatizados (15 novos testes em `CollectionsEtapaC2Tests.cs`):**
  - Total de testes do projeto elevado de 140 para 155.
  - Cobertura de construção e ordenação alfabética da árvore, contagens diretas e de subárvore, persistência de nós expandidos, seleção de nó com filtragem estrita de fotos diretas, CRUD completo (criar raiz, criar subcoleção, renomear, mover com prevenção de ciclos, exclusão nos dois modos garantindo que nenhum arquivo físico seja apagado), chips e remoção de foto de coleção, organização em lote por IDs, validação dos diálogos e instanciação STA WPF de todas as janelas/templates.
- **Validação de build e testes:**
  - `dotnet build -c Debug`: 0 erros, 0 avisos.
  - `dotnet build -c Release`: 0 erros, 0 avisos.
  - `dotnet test`: 5 execuções consecutivas com 155/155 testes verdes (100% aprovados).
- **Próximo passo:** Etapa D concluída com sucesso.

### Coleções e Subcoleções — Etapa D (Arrastar fotos para coleções: adicionar e mover) — Concluída em 02/10/2026
- **Arquitetura Pura de Decisão (`DropPlanner` e `PhotoSelectionDragHelper`):**
  - Implementado `DropPlanner` em `PhotoManager.Application.Collections` como módulo funcional desacoplado de UI.
  - Regra D6 integralmente implementada:
    - Arrastar normal (sem Shift) = **Adicionar** à coleção de destino. Se fotos já pertencerem, evita duplicatas e emite feedback claro ("2 fotos adicionadas a «X», 1 já pertencia").
    - **Shift + arrastar** = **Mover**. Remove da coleção de origem ativa e adiciona ao destino, mantendo todas as demais coleções intactas.
    - Origem do mover: coleção real selecionada na sidebar (`SelectedCollectionNode.Id`).
    - **Origem ambígua (Shift pressionado sem coleção real na sidebar):** calcula a interseção de coleções comuns a todas as fotos arrastadas e emite plano `AskMenu` contendo *Adicionar à coleção*, *Mover de «X» para «Destino»* (uma opção por coleção comum) e *Cancelar*.
    - Destinos virtuais ("Todas", "Sem coleção"), área vazia ou cabeçalho = bloqueados (`DropAction.Blocked`, `DragDropEffects.None`).
    - Soltar na própria coleção de origem ao mover = `DropAction.NoOp` informativo.
  - `PhotoSelectionDragHelper`: se a foto clicada estiver dentro de `SelectedCards`, arrasta todas as fotos marcadas na seleção múltipla; se não estiver, arrasta exclusivamente a foto clicada sem desmarcar a seleção atual.
  - Payload leve via `DataObject` no formato `"PhotoManager.PhotoIds"` carregando array de IDs (`long[]`), respeitando a virtualização (`VirtualizingWrapPanel`).
- **Integração com `LibraryViewModel`:**
  - `PlanDrop(...)` monta requisição e delega para `DropPlanner.Plan`.
  - `ExecuteDropPlanAsync(plan)` executa mutações pontuais via `_collectionService.AddPhotosAsync` e `MovePhotosAsync` (transacional).
  - Atualização dos objetos `Photo` e cards em memória (`CollectionIds` e `Collections`), reconstrução e recálculo imediato de contadores da sidebar (`UpdateSidebarCounts`).
  - Atualização automática da grade (`ApplyView`): ao mover fotos para fora da coleção atualmente filtrada na barra lateral, as fotos saem imediatamente da visualização sem perder a seleção das demais.
  - Invariante inegociável respeitada: nenhuma foto física em disco ou linha de `Photos` é removida. Salvar organização posterior (`SaveSelectedAsync`) preserva associações intactas.
- **Feedback Visual e Interação na Interface:**
  - Início de arraste com verificação de limiar de movimento (`MinimumHorizontalDragDistance` / `VerticalDragDistance`), impedindo conflito com clique simples, duplo clique (revisão), seleção estendida e controles interativos (`ButtonBase`, checkbox de seleção e coração de favoritos).
  - Feedback flutuante em tempo real via `DragFeedbackPopup` acompanhando o cursor do mouse com texto dinâmico atualizado ao pressionar/soltar Shift.
  - Destaque visual do nó alvo sob o cursor (`CollectionNode.IsDragOver`).
  - Auto-expansão de nós recolhidos após 700 ms sobre o nó na árvore.
  - Auto-rolagem vertical suave da barra lateral perto das bordas superior e inferior do `ScrollViewer`.
  - Menu de contexto suspenso ao soltar com Shift em caso de origem ambígua (`ShowAskMenu`).
- **Testes Automatizados (16 novos testes em `CollectionsEtapaDTests.cs`):**
  - Total de testes do projeto elevado de 155 para 171.
  - Cobertura completa: adicionar foto única, múltiplas fotos com estados mistos (já pertencentes vs novas), detecção de todas já pertencentes (`NoOp`), mover com origem ativa, mover para mesma coleção (`NoOp`), menu de desambiguação com coleções comuns e sem coleções comuns, bloqueio de nós virtuais e área vazia, seleção múltipla vs foto única, atualização no ViewModel e no banco real SQLite, persistência após salvar organização e instanciação STA WPF com temas.
- **Validação de build e testes:**
  - `dotnet build -c Debug`: 0 erros, 0 avisos.
  - `dotnet build -c Release`: 0 erros, 0 avisos.
  - `dotnet test`: 5 execuções consecutivas com 171/171 testes verdes (100% aprovados).
- **Próximo passo:** Etapa E (Drag-and-Drop de Coleções, Prevenção de Ciclos e Fechamento) aguarda autorização explícita (`CONTINUE`).




### Coleções e Subcoleções — Revisão pós-implementação (02/10/2026)
Revisão independente do código das Etapas A–E. Build/testes originais estavam verdes (188), mas a leitura do código e testes de regressão novos (`CollectionsReviewFixesTests.cs`) encontraram e corrigiram:
- **Excluir coleção promovendo filhos falhava** (`UNIQUE constraint failed: UX_Collections_Parent_Name`) quando um filho tinha o mesmo nome do pai excluído (ex.: Natal › Natal): a coleção excluída ainda ocupava o nome no nível dela enquanto os filhos subiam. Agora ela é renomeada para um nome temporário, dentro da mesma transação, antes da promoção.
- **Nomes com `|` ou `=` corrompiam a leitura das coleções/tags da foto** (a consulta usava `group_concat` com esses separadores): a foto perdia/embaralhava associações na grade. Separadores trocados por caracteres de controle (RS/US); o serviço agora rejeita nomes de coleção com caracteres de controle.
- **Falhas de banco no soltar (drop) derrubavam o aplicativo**: `ExecuteDropPlanAsync`/`ExecuteCollectionDropPlanAsync` rodam a partir de handlers `async void` sem tratamento. Agora capturam a exceção e mostram a mensagem na barra de status.
- **Ordem da árvore:** ordenava por `SortOrder` antes do nome, então coleções novas ou movidas iam para o fim; a decisão D5 é alfabética. Corrigido na árvore da UI e no `GetTreeAsync`.
- Soltar uma coleção em área vazia da árvore mostrava a mensagem de "itens virtuais"; agora mostra "Destino inválido".
- Removida a propriedade morta `BatchCollectionsText` (resquício da edição por nome).
- **Não verificado:** arraste com mouse real (auto-expandir, Shift, cursor, popup) — só por testes e leitura de código; migração testada apenas com bancos fabricados, não com o banco real do usuário.

### Coleções — Correção do menu de contexto (02/10/2026)
Relato: clicar num item do menu de contexto fechava o aplicativo; criar subcoleção dava erro. O Visualizador de Eventos do Windows mostrou `0xc00000fd` (**estouro de pilha**) em `PhotoManager.exe`.
- **Causa 1 — recursão infinita na seleção (travava o app):** `SelectCollectionNode` chamava `ClearFilters`, que desmarcava o nó; `ApplyView`/`UpdateSidebarSelection` o marcava de novo; o `TreeView` disparava `SelectedItemChanged`, que voltava a `SelectCollectionNode`, indefinidamente. Ocorria sempre que havia uma coleção selecionada e a árvore era reconstruída (todo item do menu). Corrigido com trava de reentrada (`_selectingCollectionNode`).
- **Causa 2 — menu sem comando:** os itens usavam `RelativeSource AncestorType=UserControl`, que não resolve dentro de um `ContextMenu` (fora da árvore visual): `Command` ficava nulo e os itens não faziam nada. Agora o `DockPanel` do nó guarda o ViewModel em `Tag` e os itens leem `PlacementTarget.Tag`.
- Diálogos de coleção agora têm `Owner` (janela principal), para abrirem centralizados e à frente.
- Testes novos em `CollectionsContextMenuTests.cs` (abrem o menu de verdade, acionam os itens via automação, com coleção selecionada e fotos). Antes da correção o teste derrubava o processo de testes com *Stack overflow*. 200 testes, 5 execuções seguidas verdes, 0/0 Debug e Release.
- **Por que passou despercebido:** os testes anteriores chamavam os métodos do ViewModel direto, sem passar pelo menu nem por uma coleção selecionada na árvore real.
- **Não verificado:** clique com mouse real no app; arraste com mouse real.

### Seleção — arrastar várias fotos e modo "Multi-seleção" (02/10/2026)
- **Bug:** com várias fotos marcadas, apertar o botão sobre uma delas para arrastar reduzia a seleção a uma foto (o `ListBox` em modo `Extended` colapsa a seleção já no *mouse down*), e só ela era arrastada. Agora a decisão é adiada para o soltar do botão (`PhotoClickPolicy`, função pura em `Application/Collections`): se houve arraste, a seleção fica intacta e todas as fotos vão; se foi só um clique, a seleção se reduz àquela foto (como no Explorer). Ctrl/Shift continuam com o `ListBox`.
- **Novo botão "Multi-seleção"** na barra da Biblioteca (`LibraryViewModel.IsMultiSelectMode`). Desligado: as caixas de marcação ficam ocultas e o comportamento é o normal. Ligado: as caixas aparecem e **clicar na própria foto marca/desmarca** (sem precisar acertar a caixa); Shift+clique seleciona intervalo; arrastar uma foto marcada leva todas as marcadas; arrastar uma não marcada leva só ela. Duplo clique continua abrindo a revisão (tratado no próprio handler, pois o clique é consumido).
- Testes: `MultiSelectTests.cs` (tabela do `PhotoClickPolicy`; eventos de mouse entregues pela rota da grade; visibilidade das caixas por modo).
- **Não verificado:** arraste e cliques com mouse real; só eventos sintéticos (o início do arraste, `DoDragDrop`, não é exercitado nos testes). Desligar o modo mantém a seleção atual (as fotos continuam destacadas, sem caixas).

### Coleções, pastas e seleção — ajustes pós-teste (02/10/2026)
1. **Painel de lote — coleções em comum:** quando TODAS as fotos selecionadas têm exatamente o mesmo conjunto de coleções, o painel Organização mostra esse conjunto como chips com × (remove a coleção de todas as fotos) e o botão "Remover todas". Se os conjuntos diferem, o bloco não aparece (regra pura em `CollectionSelectionHelper`). Só remove a associação; nada é apagado das fotos nem do disco. *Decisão:* segui o pedido ("exatamente as mesmas"); mostrar a interseção quando os conjuntos diferem seria uma extensão possível.
2. **Item virtual "Todas" nas coleções com subcoleções:** é sempre o primeiro filho (`CollectionNode.AggregateNode`, exibido via `DisplayChildren`), lista a coleção e todas as descendentes e conta fotos distintas. Não é um nó real: fica fora de `SelfAndDescendants()` (listas por Id nunca o veem), não tem menu de contexto, não é arrastável, não recebe fotos nem coleções e as ações Criar/Renomear/Mover/Excluir o ignoram. A coleção pai continua mostrando só as fotos diretas.
3. **Arrastar para coleções (inverteu):** arrastar = **mover** (da coleção ativa na sidebar); **Ctrl ou Shift = copiar** (adicionar sem tirar da origem). Sem coleção ativa: se as fotos têm coleções em comum, pergunta (menu ao soltar); se não têm, só adiciona (não há de onde mover). `DropPlanRequest.ShiftPressed` virou `CopyPressed`.
4. **Arrastar fotos para PASTAS (físico):** novo `FolderDropPlanner` + `FolderTree` com soltar. Arrastar **move o arquivo** de verdade; Ctrl/Shift **copia** (e cataloga a cópia). Usa `IFileOperationService` (nunca mexe em arquivo direto); mesmo `PhotoId`. Ignora fotos que já estão na pasta e fotos ausentes. Nome já existente na pasta de destino interrompe a operação com mensagem (não sobrescreve). Não há confirmação ao soltar: o movimento é explícito pelo arraste.
5. **Defeito antigo corrigido de passagem:** após selecionar um nó da árvore de coleções, `SelectedCollectionNode` ficava nulo (o `ClearFilters` o zerava); por isso F2 e Delete no teclado não faziam nada.
- Testes novos: `MoveCopyAndFolderDropTests.cs` (+1 de árvore real em `CollectionsContextMenuTests.cs`); testes antigos de D ajustados à nova semântica.
- **Não verificado:** arraste real com mouse (mover × Ctrl/Shift, pastas, cursor/efeitos, feedback), auto-expandir de pastas no arraste (não implementado para pastas), mover arquivos entre unidades/rede.

### Vídeos e metadados em sidecar .xmp (02/10/2026)
Relato: "ao selecionar mais de um item e tentar alterar, o botão de salvar não habilitava". Causa: as fotos eram PNG/CR2, que eram **somente leitura** (só JPEG gravava metadados) — o painel dizia "aplicado a 0 foto(s)". Decisões do usuário: gravar em **sidecar `.xmp` ao lado do arquivo** e suportar **vídeos** (catálogo, player embutido, metadados, coleções/organização e mover entre pastas).
- **Sidecar XMP (`SidecarXmpWriter`/`SidecarXmpReader`/`CompositeMetadataWriter`):** RAW (CR2), PNG, WebP e vídeos gravam título, descrição, palavras-chave, autor e copyright em `nome.xmp` (mesmo padrão do Lightroom/Bridge: `IMG_001.CR2` → `IMG_001.xmp`). O arquivo de mídia **nunca é aberto para escrita** (testado por hash). O sidecar é gravado em temporário, relido e validado antes de substituir o anterior; XMP pré-existente de outros programas é preservado (só `dc:*` é regravado); sidecar corrompido não é sobrescrito. JPEG continua gravando dentro do arquivo. Quando o sidecar existe, é a fonte única desses 5 campos na leitura (apagar um campo realmente o apaga).
- **Operações de arquivo levam o sidecar junto:** mover, renomear, copiar e Lixeira (`FileOperationService`). Um sidecar já existente no destino é preservado.
- **Vídeos:** `MediaFormats` (mp4, m4v, mov, avi, wmv, mkv, webm, mts, m2ts) entram na importação (a árvore de pastas, coleções, arrastar, filtros e mover funcionam como para fotos, pois são `Photo`). Largura/altura/duração/data de criação de MP4/MOV/M4V vêm de um leitor próprio das caixas do contêiner (`Mp4Info`, sem dependências); `Photos.DurationSeconds` (coluna nova, migração aditiva). Miniaturas/preview/quadro de pôster pelo Shell do Windows (`ShellThumbnail`, sem instalar codecs). Cartões mostram "▶ m:ss"; nova lista inteligente **Vídeos** na barra lateral.
- **Player embutido (`Controls/VideoPlayer`):** na revisão, vídeo abre o reprodutor (MediaElement do WPF, codecs do Windows) no lugar do zoom: reproduzir/pausar (clique ou Espaço), posição, volume, mudo. Abre pausado no primeiro quadro; **libera o arquivo** ao trocar de mídia, sair da revisão ou ocultar (testado: o arquivo fica livre). Sem codec: mensagem clara, sem exceção.
- **Editor de metadados (individual e em lote):** PNG/CR2/WebP/vídeo deixaram de ser "somente leitura"; só arquivo ausente continua bloqueando, com mensagem explicando o motivo em vez de "0 foto(s)".
- Testes: `VideoAndSidecarTests` (MP4 montado à mão), `VideoUiTests`; 4 testes antigos que afirmavam "PNG é somente leitura" foram atualizados à nova regra.
- **Não verificado (sem vídeo real nem ffmpeg na máquina de desenvolvimento):** reprodução real (codec/áudio/seek), miniatura do Shell para vídeos reais, leitura de duração/dimensões de AVI/WMV/MKV/WebM/MTS (ficam sem dimensões/duração; o vídeo é catalogado normalmente), importação de vídeos grandes. O player e a miniatura dependem dos codecs instalados no Windows (H.264/MP4 normalmente funcionam; MKV/HEVC/WebM podem não tocar).

### Ajustes pós-teste: player no preview, atualizar pasta, menus do Explorer, orientação (02/10/2026)
1. **Preview pequeno toca vídeo:** o painel da direita (seleção única) usa o `VideoPlayer` em modo compacto (sem barra de volume; mudo continua) no lugar da imagem. "Revisar" e o contador sobem para o topo no vídeo. O arquivo é **liberado** ao mudar a seleção, entrar na revisão, selecionar vários ou **antes de mover/renomear/excluir** (`SuspendVideoPlayback`), então o player nunca impede a operação.
2. **Atualizar pasta:** botão ⟳ no cabeçalho "PASTAS" (atualiza a pasta selecionada; sem seleção, todas as importadas) e item **Atualizar** no menu da pasta. Importa arquivos novos (inclui subpastas) e recalcula os ausentes: o que foi removido do disco **continua no catálogo marcado como "Arquivo ausente"** (tags, coleções e histórico são preservados); pasta que não existe mais é tratada sem erro. Mensagem: "Atualizado: N novo(s), M ausente(s)". *Decisão:* não apago itens ausentes do catálogo automaticamente (apagar a linha em `Photos` levaria junto histórico de metadados e UploadRecords pelo cascade); remover ausentes seria uma ação explícita futura.
3. **Menu da miniatura (botão direito):** **Exibir no Explorer** (seleciona o arquivo; se ele sumiu, abre a pasta) e **Excluir (Lixeira)…** com confirmação; se a miniatura clicada faz parte da seleção vale para toda a seleção, senão só para ela. A tecla Delete na grade faz o mesmo. O sidecar `.xmp` vai junto para a Lixeira.
4. **Menu da pasta:** **Atualizar** e **Exibir no Explorer** (abre a pasta; se não existe mais, avisa na barra de status).
5. **Orientação:** `Photo.Orientation` (Paisagem/Retrato/Quadrada, a partir das dimensões; "—" quando não há dimensões, ex.: alguns vídeos). Novo filtro **Orientação** na barra de filtros (entra em "Limpar filtros"), linha **Orientação** na aba Informações da Biblioteca e na revisão.
- Defeito encontrado e corrigido durante o desenvolvimento: o menu da miniatura estava declarado depois do `PhotoCardTemplate` nos recursos (referência `StaticResource` adiantada faria a Biblioteca falhar ao abrir); o teste de interface pegou.
- Testes: `ExplorerRefreshOrientationTests.cs` (11). **Não verificado:** clique/botão direito com mouse real, abertura real do Explorer, reprodução real de vídeo no painel.

### Ajustes: expandir/recolher tudo, pastas inacessíveis, remover ausentes, filtros e retrato (05/10/2026)
1. **Expandir tudo / Recolher tudo** no menu de contexto das pastas e das coleções: agem no nó e em **todos** os descendentes (o item virtual "Todas" da coleção é ignorado).
2. **Pastas inacessíveis:** a cada (re)montagem da árvore (inclui o carregamento) cada pasta é verificada em segundo plano (até 6 em paralelo, *timeout* de 4 s por pasta, para não travar com disco de rede fora). Pasta que não existe/está desconectada ganha o ícone ⚠ (âmbar), tooltip explicativo e **o conteúdo dela não é listado** (os filhos continuam no modelo e voltam quando a pasta volta). Dentro de uma pasta inacessível não se verifica mais nada; o último resultado é reaproveitado para não piscar.
3. **Atualizar lista o que não foi encontrado:** depois de importar os novos e recalcular os ausentes, abre a janela **"Itens não encontrados"** (`MissingItemsDialog`): uma linha por pasta — pasta sumida (subpastas sumidas são unidas na mais alta que não existe, ex.: disco/pasta raiz inteira) ou arquivos ausentes de uma pasta que ainda existe — com **Remover** por item e **Remover todos** (com confirmação). Remover = `DELETE` das linhas do catálogo e filhas (tags, coleções, notas, histórico de metadados, UploadRecords) e das miniaturas em cache; **nenhum arquivo é tocado**, e o serviço **reconfere no disco** (arquivo que voltou a existir nunca é removido).
4. **Filtros não são limpos ao navegar:** trocar de pasta, lista inteligente, coleção, categoria ou tag só troca o **local**; busca, categoria, tag, avaliação, orientação e favoritas definidos na barra são mantidos. "Todas as fotos" e "Limpar filtros" continuam limpando tudo. O teste antigo que esperava a limpeza foi atualizado.
5. **Remover do catálogo na miniatura:** o menu de contexto de um item com arquivo ausente mostra **Remover do catálogo…** (com confirmação) no lugar de "Excluir (Lixeira)".
6. **Retrato × paisagem visível:** (a) miniaturas de retrato e quadradas aparecem **inteiras** (como no Explorer) em vez de cortadas; (b) **correção da causa raiz para vídeos de celular:** o MP4 guarda a imagem deitada (1920×1080) com uma rotação de 90° na matriz do `tkhd`; `Mp4Info` agora aplica a matriz, então esses vídeos passam a ser *retrato* (1080×1920). Itens já catalogados são corrigidos **em segundo plano** ao abrir (coluna nova `MediaInfoRevision`; só o cabeçalho de cada vídeo é relido, em lotes de 100, e o resultado é gravado no banco — não repete).
- Defeito achado pelos testes e corrigido: após recarregar o catálogo a seleção guardada no ViewModel apontava para cartões antigos (ações de menu agiam sobre itens que já não existiam). `ApplyView` agora atualiza a seleção mesmo sem a tela escutando.
- Testes novos em `MissingAndTreeTests.cs`. **Não verificado:** a janela "Itens não encontrados" e os ícones com mouse real; comportamento com discos de rede reais lentos; a correção de orientação em vídeos reais de celular (testado com MP4 sintético com matriz de rotação). **Limitação conhecida:** fotos JPEG com orientação EXIF (celular em retrato) ainda aparecem pela orientação bruta dos pixels (miniatura e dimensões não aplicam a rotação EXIF).

### EXIF de JPEG, rotação de vídeo/drone, giro manual e integração com o Explorer (05/10/2026)
1. **Orientação EXIF dos JPEGs (`ExifOrientation`):** lê a tag 0x0112 (1–8) e aplica rotações de 90° e espelhamentos sem perda na **miniatura**, no **preview**, na **tela cheia** (zoom) e nas **dimensões** gravadas (5–8 trocam largura/altura; o filtro Retrato/Paisagem passa a acertar). Testado com JPEGs sintéticos (metade vermelha/azul) conferindo a posição das cores para cada orientação.
2. **Catálogo antigo corrigido sozinho:** `MediaInfoRevision` agora é 2. Ao abrir, itens com revisão menor (JPEG e vídeo) são relidos em segundo plano (só cabeçalhos, lotes de 100), as dimensões são corrigidas e gravadas no banco, e **as miniaturas que estavam deitadas são refeitas** (cache apagado; o Uri ganha um sufixo `?r=n` para o WPF não reaproveitar a imagem antiga).
3. **Vídeos / drone — análise dos arquivos de `docs/videos`:** a matriz do MP4 é lida na ordem correta (`a b u / c d v`); `DJI_0024`, `0030`–`0033` trazem rotação de 90° (retrato) e os demais não. O Windows **não** aplica essa rotação à miniatura (devolve 1280×720 deitada) e o MediaElement também ignora. Agora: o app gira o quadro da miniatura/preview quando o arquivo manda exibir em retrato e o quadro chegou deitado; guarda `AutoRotation` no catálogo e o player aplica a rotação. **Atenção:** nos quadros dos drones o céu está à direita; pela regra do padrão (90° horário) eles aparecem de cabeça para baixo — não deu para validar com um player de referência. Por isso existe o giro manual abaixo.
4. **Girar (botão direito → Girar à esquerda/direita):** giro manual de 90° guardado no catálogo (`Photos.UserRotation`), vale para a seleção inteira quando o item clicado faz parte dela, aplicado na miniatura (LayoutTransform), no preview, na revisão e no player; também entra no cálculo de Retrato/Paisagem. **Nunca altera o arquivo.**
5. **Explorer → app (soltar):** na área da biblioteca, soltar arquivos/pastas **cataloga no lugar** (nada é copiado nem movido; aviso azul "+ Adicionar … ao catálogo"; duplicatas ignoradas; sidecar `.xmp` acompanha; tipos não suportados são ignorados). Numa **pasta da árvore**: Ctrl = copiar, Shift = mover, sem tecla = pergunta (Sim = copiar, Não = mover, Cancelar). Nunca sobrescreve (nome existente é mantido e informado); mover um arquivo que já está no catálogo usa o serviço normal, então o `PhotoId` e os metadados acompanham. Pastas inteiras só na grade.
6. **App → Explorer (arrastar para fora):** arrastar fotos da grade para uma janela do Explorer **copia** os arquivos (formato de arquivos do Windows + "Preferred DropEffect" = copiar), levando o sidecar `.xmp`; arquivos ausentes não vão. O arraste interno (coleções e pastas da árvore) continua igual. Com Shift, o Explorer move o arquivo — o catálogo passa a mostrá-lo como ausente (use ⟳ Atualizar).
- Testes novos: `ExifAndRotationTests`, `ExplorerDragDropTests` (+ ajustes). **Não verificado:** arraste real com o mouse de e para o Explorer (efeitos/cursor, pastas grandes), reprodução e miniaturas dos seus vídeos reais, e a decisão de qual rotação "correta" os vídeos do drone devem ter (ver item 3).

### Filtros avançados, cores, localização e visualizador (05/10/2026)
- **Filtros avançados:** a barra mostra só os filtros principais; o botão "Filtros avançados (n)" abre Tipo (foto/vídeo), Extensão (lista montada a partir do catálogo), Data (de/até, pela data da foto ou de criação, fim inclusivo), Orientação, Cor e avaliação mínima. Os avançados também são mantidos ao trocar de pasta e limpos por "Limpar filtros".
- **Cores:** seis etiquetas (vermelho, laranja, amarelo, verde, azul, roxo) guardadas no catálogo (`Photos.ColorLabel`), nunca no arquivo. Faixa colorida na miniatura, submenu **Cor** no botão direito, teclas **1–6** (e **0** remove) sobre a seleção, bolinhas na barra da revisão, filtro por cor (inclui "Sem cor") e linha "Cor" nas Informações.
- **Revisão:** botões Girar à esquerda/direita também para vídeo.
- **Localização:** `ILocationService` lê o GPS do EXIF (fotos) ou do átomo `©xyz` (vídeos DJI/celular, ISO 6709) sem rede e guarda no catálogo (`Latitude/Longitude/GpsChecked`). O **nome do lugar** vem do Nominatim/OpenStreetMap (geocodificação reversa, pt-BR) **somente após autorização** do usuário (guardada em `settings.json`, chave `geocoding.consent`), com User-Agent identificável, 1 consulta/s e cache por local (~100 m). Informações → "Obter localização" / "Ver no mapa"; botão direito → "Obter localização (GPS)" para a seleção. O nome do lugar entra na busca.
- **Metadados:** miniaturas maiores (144×108 na lista, 230 px no painel), inteiras e com o giro aplicado, selo de play nos vídeos; duplo clique ou "Ampliar" abre o `MediaViewerWindow` (foto em resolução cheia com zoom, vídeo no player tocando; Esc fecha e libera o arquivo).
- Testes: `ColorAndFilterTests`, `LocationTests`, `MediaViewerTests`.

### Módulo Transferência — etapa 2: painéis e navegação (05/10/2026)
- Plano e auditoria em `docs/TRANSFER_MODULE_PLAN.md` (reutilização de cor da Biblioteca, `IFileOperationService`, planejadores de drop, miniaturas, visualizador).
- Nova área **Transferência** (entre Microstock e Ferramentas): dois painéis independentes separados por `GridSplitter` (MinWidth 320 cada). Cada painel tem pasta atual, histórico (voltar/avançar), subir, atualizar, breadcrumb clicável, caminho digitável (✎), "Selecionar pasta…" (qualquer pasta, inclusive fora do catálogo/rede), busca por nome/extensão, ordenação (nome natural, data, tamanho, tipo; crescente/decrescente; pastas primeiro opcional), modo miniaturas (tamanho ajustável, virtualizado) ou lista (cabeçalhos clicáveis ordenam), seleção com contagem e tamanho.
- Código: `Application/Transfer` (`FolderLister`, `TransferListing`, `NavigationHistory`, `Breadcrumbs`, `IFileThumbnailService`), `ThumbnailService.GetOrCreateForFileAsync` (cache por caminho+tamanho+data em `Cache/Thumbnails/Files`), `TransferViewModel`, `TransferPaneViewModel`, `TransferItemViewModel`, `TransferView`, `TransferPaneView`.
- Duplo clique: pasta abre; foto/vídeo abre o visualizador existente. Backspace volta, F5 atualiza, Enter abre.
- Pasta offline/sem permissão mostra a mensagem no painel e permite tentar de novo com Atualizar.
- **Ainda não existe:** comparação, copiar/mover, arrastar e soltar, cores, persistência do layout (etapas 3–6). Nada nesta tela altera arquivos.
- Testes: `TransferPanesTests` (17).

### Cards da Biblioteca: etiqueta de cor × seleção (05/10/2026)
- A etiqueta de cor deixou de ser uma moldura grossa: agora é **fundo suave (~12%) + borda discreta (~70%)** no próprio cartão. A **seleção** é um estado separado: borda azul (`PhotoCardSelectedBorder`, 3 px) e o fundo da etiqueta é preservado; sem etiqueta, o fundo selecionado continua azul claro. Hover = realce leve de fundo (sem trocar a borda da etiqueta).
- Só apresentação: cores centralizadas em `Themes/Colors.xaml` (`PhotoCardDefault*`, `PhotoLabel{Red,Orange,Yellow,Green,Blue,Purple}{Background,Border}`, `PhotoCardSelected*`, `PhotoCardHoverOverlay`) e gatilhos no `PhotoCardContainer` (`Themes/Cards.xaml`; ordem dos gatilhos = prioridade). Persistência, comandos e filtros intactos. A tela Transferência usa o mesmo container (sem etiqueta por enquanto).
- Imagem de conferência: `docs/Imagens/cards-cores.png`.
