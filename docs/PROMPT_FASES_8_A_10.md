# PHOTOMANAGER — CONTINUAÇÃO: FASES 8, 9 E 10

Você está assumindo o **PhotoManager** (WPF · .NET 10 · C# · MVVM · SQLite) em `K:\Trabalho\Projetos\PHOTOMANAGER`. As **Fases 1 a 7 já estão implementadas, compilando e testadas** (79 testes, 0 erros, 0 avisos em Debug e Release). Você tem acesso a build/teste/execução no Visual Studio e na CLI (`dotnet build`, `dotnet test`).

Objetivo desta entrega: implementar **uma fase por vez**, na ordem **8 → 9 → 10**, validando cada uma antes de passar à seguinte. **Pare ao fim de cada fase** e apresente o relatório; só avance quando eu escrever `CONTINUE`.

---

## 0. ANTES DE ESCREVER CÓDIGO

1. Leia, nesta ordem: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/KNOWN_LIMITATIONS.md`, `docs/MANUAL_TEST_CHECKLIST.md`, `docs/UI_REFACTOR_PLAN.md`.
2. Veja as imagens de referência em `docs/Imagens/` (direção visual). Há fotos reais de câmera em `docs/Fotos/*.CR2` (RAW Canon, somente leitura).
3. Rode `dotnet restore && dotnet build && dotnet test` e confirme **79 testes verdes** antes de mudar qualquer coisa. Se algo falhar, registre e corrija antes.
4. Faça uma auditoria curta do que você vai tocar (arquivos, DI, banco) e liste-a no relatório da fase.

## 1. REGRAS INEGOCIÁVEIS

**Preservar:** fotos do usuário, catálogo, dados já salvos, migrations, Fases 1–7, API pública das ViewModels existentes, testes existentes. **Nunca** apagar/recriar o banco; toda mudança de schema é **migração idempotente** (`CREATE TABLE IF NOT EXISTS` / `ALTER TABLE … ADD COLUMN` com try/catch, como já feito em `SqliteCatalogRepository.InitializeAsync`) e deve ter **teste com banco antigo**.

**Não fazer:** trocar WPF/.NET/SQLite; recomeçar arquitetura; duplicar serviços; regra de negócio em `.xaml.cs` ou XAML; mocks que fingem recurso; botões falsos; **IA** (fase 16); **upload/FTP/fila** (fases 11–13); similaridade visual (14). **Nunca excluir arquivos automaticamente.** Itens de fases futuras não entram agora.

**Qualidade obrigatória por fase:** build Debug **e** Release sem erros/avisos · testes novos cobrindo a lógica (inclua casos de erro) · testes de fumaça de UI (janela/tela carrega em STA) · repetir a suíte ≥ 5× para detectar flakiness · atualizar os 5 documentos (`IMPLEMENTATION_STATUS`, `HANDOFF`, `KNOWN_LIMITATIONS`, `MANUAL_TEST_CHECKLIST`, `ARCHITECTURE`) marcando honestamente o que foi e o que **não** foi verificado.

## 2. ARQUITETURA ATUAL (resumo)

```text
src/ Domain · Application · Infrastructure · Persistence · Wpf      tests/PhotoManager.Tests (xUnit)
```
- `Domain`: `Photo` (Id próprio, `CurrentPath`, `Extension`, `FileSize`, `ContentHash` (hoje sempre nulo), `IsMissing`, `CategoryName`, `Rating`, `IsFavorite`, `Tags`, `Collections`, **`MetadataVersion`**).
- `Application`: contratos e serviços (catálogo, organização, operações de arquivo, navegação, metadados: `IMetadataReader`, `IMetadataEditService`, `IBatchMetadataService`, `IMetadataPresetRepository`, `MetadataEdit`, `BatchMetadataPlan`; `ImageFormats` = formatos aceitos).
- `Infrastructure`: caminhos/config/log, `ThumbnailService`, `ImageLoader`/`RawPreview` (**toda decodificação de imagem deve passar por `ImageLoader`**, senão RAW quebra), `FileOperationService`, `MetadataExtractorReader`, `JpegMetadataWriter`.
- `Persistence`: `SqliteCatalogRepository` (implementa as interfaces de repositório; tabelas `Photos`, `Tags`, `Collections`, `MetadataHistory`, `MetadataPresets`…).
- `Wpf`: `App.xaml(.cs)` (DI), `MainViewModel` (abas; **uma** `LibraryViewModel` e **uma** `MetadataViewModel` por sessão), `Views/*`, `Themes/*`, `Controls/*`.
- Fluxo da Biblioteca: `ReloadAsync` lê o banco → `_all` → filtros/sidebar/árvore **em memória**. Alterações que mudam dados no banco devem terminar em recarga ou atualizar os mesmos objetos `Photo`.
- Formatos: JPG/JPEG/PNG/WEBP e **CR2 (RAW somente leitura)**. **Só JPEG é gravável** (IPTC/XMP via `JpegMetadataWriter`; EXIF e pixels nunca mudam).
- Já existem: `MetadataVersion` por foto (incrementa a cada gravação de metadados) e histórico em `MetadataHistory`. **Use isso** para detectar "alterada após envio".

## 3. LIÇÕES APRENDIDAS (leia — evitam horas de depuração)

1. **Temas:** todos os `ResourceDictionary` são mesclados em `App.xaml` (não aninhados); `StaticResource` dentro de `ControlTemplate` não enxerga dicionários irmãos aninhados. Declare estilos antes dos templates que os usam. Estilos implícitos de controles com template precisam de `BasedOn`.
2. **ComboBox tem template próprio:** use `ItemTemplate`, não `DisplayMemberPath`. Para novos `ControlTemplate`, crie teste que o instancie (`UiSmokeTests.ThemeControlTemplates_Instantiate` varre o tema).
3. `Foreground` no `TabItem` vaza para o conteúdo; `ProgressBar.Value` precisa de `Mode=OneWay` com propriedade somente leitura.
4. `UseWPF` remove `System.IO` dos usings implícitos (já tratado nos csproj).
5. Mutações de coleções ligadas à UI devem ocorrer na thread de UI; nos testes use `DispatcherSynchronizationContext` (veja `UiSmokeTests.RunSta`). Callbacks `Progress<T>` podem chegar fora de ordem: proteja mensagens finais.
6. Preview/miniatura leem arquivos por milissegundos: operações que movem/substituem arquivo precisam de **retentativa** (veja `FileOperationService.MoveFileAsync`).
7. Gravar em arquivo = **temporário → validar → substituir** (veja `JpegMetadataWriter`). Replique o padrão para qualquer operação destrutiva.
8. Em testes, **não** use `GetAwaiter().GetResult()` na thread de UI; bombeie o Dispatcher (`UiSmokeTests.Await`).
9. Para inspeção visual: `PM_SNAPSHOT_DIR=<pasta> dotnet test --filter BatchWindow_Loads` renderiza uma janela em PNG; você também pode abrir o app com `PHOTOMANAGER_ROOT=<pasta>` para usar dados isolados (nunca teste sobre `%LocalAppData%\PhotoManager` do usuário).
10. Itens `Categories`: a tabela nunca é populada (categoria é texto em `Photos.CategoryName`). Não dependa dela.

## 4. DIREÇÃO VISUAL

Mesma linguagem do app: barra superior escura com abas, superfícies claras, azul de destaque, cards discretos, ícones Segoe Fluent/MDL2, densidade boa em 1366×768 (mínimo 1100×650) e 1920×1080. Reaproveite estilos/controles (`Card`, `AccentButton`, `RatingControl`, `StatusBadge`, ComboBox/TextBox/Tabs/Tree existentes). Siga `docs/Imagens/` para a Central de Produção (cards de status no topo + tabela). Estados vazios honestos. Nada de UI de recurso inexistente.

---

# FASE 8 — WORKFLOW MICROSTOCK LOCAL (sem upload)

**Objetivo:** saber, para cada foto, o quão pronta ela está para microstock, e ter uma **Central de Produção** na aba *Microstock* (hoje um placeholder honesto).

### 8.1 Estados de preparação (calculados, não digitados)
`Não preparada` · `Metadata incompleto` · `Pronta para envio` · `Enviada parcialmente` · `Enviada para todos` · `Com erro` · `Alterada após envio`.
Os três últimos dependem de envio (fase 9): **projete o cálculo agora** (função pura, testável) recebendo "registros de envio" por foto — na fase 8 a lista é vazia, então só aparecem os 3 primeiros; **não** invente dados de envio. Defina e documente a **precedência** (sugestão: Com erro > Alterada após envio > Enviada para todos > Enviada parcialmente > Pronta > Metadata incompleto > Não preparada).
- *Não preparada*: foto sem metadados mínimos de microstock (nenhum título/descrição/keywords) ou formato não editável sem metadados.
- *Metadata incompleto*: tem algo, mas falha no **perfil de validação ativo**.
- *Pronta*: passa no perfil.

### 8.2 Perfis de validação configuráveis
**Não codifique regras de uma agência como universais.** Crie `ValidationProfile` (nome, regras) persistido no banco (migração idempotente), com perfis iniciais **genéricos e editáveis** (ex.: "Padrão genérico": título 1–200, descrição ≤ 2000, 5–50 keywords, autor opcional…). Regras possíveis: título obrigatório + min/max, descrição obrigatória + max, keywords min/max, autor/copyright obrigatórios, formato permitido (só JPEG?), dimensão mínima em MP, nota mínima. Cada regra produz uma mensagem legível ("Faltam 2 palavras-chave (mín. 5)"). Perfil ativo selecionável; criar/editar/duplicar/excluir perfis (não permitir excluir o último). Validação é função pura sobre `Photo` + `PhotoMetadata`/`MetadataEdit`.

### 8.3 Central de Produção (aba Microstock)
- **Cards** com contagem e filtro por clique: Todas · Sem metadata · Metadata incompleto · Prontas para envio · Enviadas parcialmente · Enviadas · Com erro · Alteradas após envio (os de envio mostram 0 até a fase 9 — sem dados falsos).
- **Tabela/lista:** Foto (miniatura + nome) · Metadata (resumo/ícone ✓/⚠) · colunas por banco **Adobe Stock, Shutterstock, Depositphotos, Dreamstime, 123RF** (na fase 8 aparecem como "—"/"Não enviado"; as agências reais são da fase 9 — **não crie tabela de agência aqui**, só reserve as colunas de forma que a fase 9 as alimente) · Status. Selecionar uma foto mostra as pendências do perfil e um atalho **Abrir em Metadados** (navega para a aba e seleciona a foto).
- Leitura de metadados em lote pode ser cara: use cache/lazy por foto, leitura em segundo plano com progresso e cancelamento, **sem travar a UI**; recalcule quando o perfil ativo ou os metadados mudarem (após Fase 6/7). Considere guardar um resumo (ex.: contagens) em coluna/tabela de cache invalidada por `MetadataVersion`/data de modificação — documente a decisão.
- Perfil ativo e editor de perfis acessíveis na própria tela (ou em Configurações, se for mais natural — justifique).

**Critérios de conclusão:** estados corretos para JPEG com/sem metadados, PNG/RAW (somente leitura), foto ausente; mudar o perfil muda o status; editar metadados (Fase 6/7) e voltar à Central atualiza; UI fluida com milhares de fotos (teste de volume com ≥ 2.000 fotos sintéticas); testes de validação por regra; testes do cálculo de estado (incluindo precedência com dados de envio simulados **apenas em teste**); smoke da tela; migração.

---

# FASE 9 — AGÊNCIAS E HISTÓRICO DE ENVIO (manual)

**Objetivo:** registrar por foto em quais bancos ela foi enviada, **sem upload automático**. Útil já como controle manual.

### 9.1 Modelo
- `Agency`: Id, Nome, Ativa, Ordem (iniciais: Adobe Stock, Shutterstock, Depositphotos, Dreamstime, 123RF; o usuário pode adicionar/renomear/desativar/ordenar; não excluir se houver histórico — desative).
- `UploadRecord`: `PhotoId`, `AgencyId`, `UploadedAt`, `Status` (ex.: Pendente, Enviado, Rejeitado, Erro — defina o enum e documente), `MetadataVersion` (versão dos metadados **no momento do envio**), `RemoteFileName`, `RetryCount`, `LastError`, `Notes` opcional. Histórico completo (não sobrescreva; ao remarcar, crie novo registro ou atualize com trilha — decida e documente). FK com `ON DELETE CASCADE` para `Photos`. Migração idempotente + teste com banco antigo.
- Repositório + serviço de aplicação (`IUploadHistoryService`): marcar enviado (uma ou várias fotos, um ou vários bancos) **manualmente**, registrar erro/rejeição com motivo, desfazer marcação, listar histórico por foto.

### 9.2 Integração
- Central de Produção passa a mostrar por banco: ✓ enviado (data), ✗ erro/rejeitado, — não enviado; estados **Enviada parcialmente / Enviada para todos** (considerando só agências **ativas**), **Com erro**, **Alterada após envio** (`UploadRecord.MetadataVersion < Photo.MetadataVersion` em qualquer registro Enviado) — usando a função pura da fase 8.
- Ação **"Marcar como enviado manualmente"** (seleção múltipla; escolher bancos, data/hora padrão agora, nome remoto opcional) e **"Marcar erro"**. Confirmação antes de gravar. Painel de histórico por foto (aba Histórico do painel da Biblioteca ou da Central). Tudo reversível.
- Filtros da Central por banco e por status.

**Critérios:** marcar/desmarcar persiste; versão de metadados é registrada; editar metadados depois do envio muda o status para *Alterada após envio*; desativar agência recalcula "enviada para todos"; histórico preservado; nenhum código de rede; testes de serviço/estado/migração/ViewModel/smoke.

---

# FASE 10 — DUPLICATAS EXATAS

**Objetivo:** encontrar arquivos **idênticos** no catálogo. Nunca comparar tudo com tudo.

### 10.1 Algoritmo
1. **Pré-filtro por tamanho** (`FileSize` já está no banco): só calcule hash de arquivos cujo tamanho se repete.
2. **SHA-256** em streaming (buffer, nunca carregar o arquivo inteiro; RAW tem ~30 MB), em segundo plano, com **progresso, cancelamento e retomada**. Grave em `Photos.ContentHash` (+ guarde `HashedAtSize`/`HashedAtModified` ou equivalente para invalidar quando o arquivo mudar; uma edição de metadados da Fase 6/7 muda o arquivo ⇒ hash deve ser recalculado).
3. Agrupe por hash (≥ 2 fotos). Ignore ausentes. Falhas de leitura de um arquivo não interrompem as demais.

### 10.2 Tela de duplicatas
Em **Ferramentas → Duplicatas** e smart list **Duplicadas** na sidebar da Biblioteca (com contagem; hoje não existe — a referência visual mostra o item). Lista de **grupos**, cada um com miniaturas, caminho, tamanho, data, nota/avaliação/tags de cada cópia. Ações por grupo/foto: **Manter esta** (marca as demais como candidatas), **Abrir pasta** (Explorer com seleção), **Mover** (para uma pasta escolhida, via `FileOperationService`), **Excluir** (apenas para a **Lixeira**, com confirmação explícita mostrando exatamente quais arquivos), **Ignorar grupo** (persistido: tabela de grupos ignorados por hash). **Nunca excluir automaticamente**, nunca excluir todas as cópias de um grupo (exija manter ao menos uma), e preserve organização (tags, coleções, nota) — se excluir uma cópia, ofereça **mesclar** tags/coleções/avaliação na que fica (opcional, explícito).

**Critérios:** arquivos de tamanho único nunca são hasheados (teste prova com contador/fake de hash); cópias idênticas em pastas diferentes agrupam; arquivos diferentes com mesmo tamanho não agrupam; hash invalida após edição de metadados; cancelar/retomar; grupo ignorado some e é lembrado; excluir vai para a Lixeira e atualiza catálogo e tela; milhares de fotos sem travar; testes com arquivos reais temporários + smoke da tela + migração.

---

## 5. FORMATO DO RELATÓRIO AO FIM DE CADA FASE

1. Objetivo e arquivos criados/alterados (por camada).
2. Decisões e trade-offs (incl. o que você escolheu onde este prompt deixou aberto).
3. Resultado **real**: `dotnet build` (Debug/Release), `dotnet test` (nº de testes, repetições), o que foi executado de verdade no app e o que **não** foi verificado.
4. Defeitos encontrados e corrigidos (inclusive nas fases anteriores).
5. Documentos atualizados.
6. Pergunte "CONTINUE?" e **pare**.

Nunca diga "funciona" sem validação real. Se algo falhar por ambiente, diga exatamente o quê.
