# PHOTOMANAGER — PORTABILIDADE · ETAPA E (disponibilidade, raízes offline e tela "Bibliotecas e armazenamento")

## CONTEXTO COMUM

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Etapas A–D desta frente concluídas (`Photos` = `StorageRootId`+`RelativePath`, resolver central, migração feita). Releia: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/STORAGE_ROOT_AND_BACKUP_PLAN.md` e **`docs/prompts_storageroot/00_ANALISE_E_DECISOES.md`** (S1–S14, em especial **S7**).

**Regras inegociáveis:** não mexer em subcoleções/drag de coleções, IA, upload, similaridade; nunca alterar arquivos; preservar `PhotoId`; sem mocks nem botões falsos; **sem regra de negócio em `.xaml.cs`**; `ComboBox` com `ItemTemplate`; temas em `App.xaml`; build Debug e Release **0/0**; `dotnet test` verde **≥ 5× seguidas, uma por vez** (feche `PhotoManager.exe`); `[Collection("WpfUi")]`; `PM_SNAPSHOT_DIR` para captura; `PHOTOMANAGER_ROOT` isola dados; nunca simular mouse/teclado reais na tela do usuário; docs registram o que **não** foi verificado. **Pare ao fim e relate** e espere `CONTINUE`.

---

## ESTA ETAPA — E: disponibilidade e offline (sem relocalizar ainda)

### 1. Disponibilidade da raiz (`StorageRootAvailability`)
- Estados: `Online`, `Offline` (e `Unknown` enquanto verifica). Critério: `Directory.Exists(CurrentPath)` **com timeout** (ex.: 2 s, configurável) executado fora da thread de UI; **uma** verificação por raiz por ciclo, nunca por foto (S7). NAS lento não pode travar a abertura da biblioteca.
- Verificação na inicialização, ao abrir a Biblioteca, ao clicar em "Verificar novamente" e ao receber evento de mudança de unidade (`DeviceChange`/`DriveInfo` polling leve opcional; se não implementar, registre). Ao ficar online: `LastSeenAt` atualizado, atualizar também rótulo/serial se mudaram.
- O snapshot do resolver (C) expõe `RootAvailability` por foto sem consultar nada por foto.

### 2. Comportamento com raiz offline (S7)
- **Corrigir `UpdateMissingStatesAsync`:** recalcular `IsMissing` **só** para fotos de raízes online; para raízes offline **não tocar** no valor persistido e **não** chamar `File.Exists`. Teste: desconectar a raiz não altera nenhum `IsMissing`; reconectar com arquivo removido marca ausente; reconectar com tudo ok mantém tudo ok.
- `PhotoCardViewModel.IsMissing` deixa de chamar `File.Exists` por cartão: usa o estado de disponibilidade + `IsMissing` persistido; novo estado de exibição **"Raiz indisponível"** (distinto de "Arquivo ausente"), com selo/tooltip na grade, na revisão e nos painéis.
- **Miniaturas:** `ThumbnailService.GetOrCreateAsync` olha o cache **antes** de exigir o original; raiz offline continua mostrando miniatura em cache; sem cache e offline → placeholder "indisponível" (sem gerar erro). Preview grande/zoom/metadados/edição/escrita/mover/copiar/renomear/lixeira: **desabilitados com motivo claro** ("A raiz «HD Fotos» está offline"), nunca exceção. Validação Microstock/duplicatas: ignora fotos offline e informa quantas foram ignoradas (não as trata como erro).
- Filtro "Ausentes" não inclui fotos de raiz offline; criar filtro/indicador "Raiz offline" na sidebar com contagem.
- Árvore de pastas: nós de topo = **raízes** (nome, selo ● disponível / ⚠ offline, contagem) com subpastas por caminho relativo; continua funcionando offline (a estrutura vem do catálogo). Filtro de pasta passa a ser `(raiz, subpasta relativa)`.

### 3. Tela Configurações → "Bibliotecas e armazenamento"
Hoje `SettingsViewModel` é uma classe vazia; implemente de verdade (sem mocks). Lista de raízes: nome (renomear), caminho atual, tipo (HD/removível/rede), rótulo/serial, nº de fotos, última vez visto, status (`● Disponível` / `⚠ Offline`), botão **Verificar novamente** (ativo) e **Localizar novamente** (**visível mas desabilitado com dica "Disponível na próxima etapa"** — não implementar F aqui; não esconder nem fingir). Botão "Adicionar raiz…" **somente** se simples e correto (valida pasta, bloqueia raiz duplicada e raiz aninhada incoerente); caso contrário, deixe para a F e documente.

### 4. Testes
Disponibilidade: online/offline/timeout (fake de sistema de arquivos com atraso); **pedidos 2 e 3** (raiz online/offline); `IsMissing` intocado offline; reconexão; miniatura em cache com original offline; ações de escrita bloqueadas offline com mensagem; filtros e contagens (ausentes × raiz offline); árvore por raiz com selo; tela Configurações carrega e lista raízes reais (STA, `PM_SNAPSHOT_DIR`); nenhum `File.Exists` por foto (teste espiando o acesso: ≥ 5.000 fotos, 1 verificação por raiz); `ThemeControlTemplates_Instantiate` continua verde. Banco legado "Decidir depois" (D) segue funcional.

### Verificação e documentação
Build Debug+Release 0/0; `dotnet test` ≥ 5×; execução real com dados isolados simulando raiz offline (renomear temporariamente a pasta de teste, **nunca** a pasta do usuário) e captura. Atualize `IMPLEMENTATION_STATUS.md`, `HANDOFF.md`, `ARCHITECTURE.md`, `KNOWN_LIMITATIONS.md`, `MANUAL_TEST_CHECKLIST.md` (desconectar HD externo, NAS) e `UI_REFACTOR_PLAN.md` se pertinente.

**Pare e relate.** Não inicie a Etapa F.
