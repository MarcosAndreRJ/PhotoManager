# PHOTOMANAGER — PORTABILIDADE · ETAPA F (validação, detecção automática e relocalização de raiz e de pasta)

## CONTEXTO COMUM

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Etapas A–E desta frente concluídas (raízes, resolver, migração, offline, tela "Bibliotecas e armazenamento" com "Localizar novamente" desabilitado). Releia: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/STORAGE_ROOT_AND_BACKUP_PLAN.md` e **`docs/prompts_storageroot/00_ANALISE_E_DECISOES.md`** (S1–S14, em especial **S2, S8, S9, S13**).

**Regras inegociáveis:** não mexer em subcoleções/drag de coleções, IA, upload, similaridade; **relocalizar nunca move, copia, apaga nem altera arquivos**; preservar `PhotoId` e `RelativePath` (mudança de letra só altera `StorageRoot.CurrentPath`); sem mocks; **sem regra de negócio em `.xaml.cs`**; build Debug e Release **0/0**; `dotnet test` verde **≥ 5× seguidas, uma por vez** (feche `PhotoManager.exe`); `[Collection("WpfUi")]`; `PM_SNAPSHOT_DIR`; `PHOTOMANAGER_ROOT`; nunca simular mouse/teclado reais; docs registram o que **não** foi verificado. **Pare ao fim e relate** e espere `CONTINUE`.

---

## ESTA ETAPA — F

### 1. Validador (`RootValidator`, Application; I/O atrás de interface para testes)
`Validate(root | subconjunto de fotos, candidatePath, options, progress, cancellation)` → `RootValidationResult`:
- Pasta existe e é legível.
- **Amostra** dos caminhos relativos: todos se ≤ 50 fotos; senão até 200 escolhidos de forma **determinística** (semente fixa, distribuídos entre subpastas). Para cada: existe? **tamanho** igual a `Photo.FileSize`? Para até ~10 fotos com `ContentHash` **válido** (`HashedAtSize == FileSize`): **hash** igual (calcular com `IFileHashService`, cancelável, com progresso). Fotos sem hash válido só entram por existência+tamanho (registre quantas).
- Saída: `Found/Total`, `SizeMatches/Total`, `HashMatches/HashChecked`, lista dos ausentes (amostra) e uma classificação: **Forte** (≥ 95% existem, ≥ 95% tamanho, 0 hash divergente), **Parcial** (≥ 50%), **Fraca/Nenhuma**. Hash divergente ou muitos tamanhos diferentes **rebaixa** (arquivo existe mas é outro). Textos como no pedido: "18 de 18 arquivos encontrados / 18 de 18 tamanhos compatíveis / 5 de 5 hashes compatíveis → Correspondência forte".
- Nunca grava nada; nunca modifica arquivos; leitura de hash limitada e cancelável (NAS).

### 2. Detecção automática (`RootLocator`)
Antes de pedir ao usuário, para raiz offline: candidatos por (1) **`VolumeSerial`** entre as unidades montadas (`DriveInfo` + `VolumeInfoReader`) → caminho = unidade + `OriginalPath` sem a letra; (2) `VolumeLabel`; (3) cada unidade montada com a mesma estrutura (`OriginalPath` sem unidade e também só o nome da pasta raiz); (4) arquivos conhecidos (amostra pequena); (5) hashes de amostra. Cada candidato é validado (item 1). **S13:** aplicar sozinho **apenas** se serial confere **e** validação Forte **e** candidato único; caso contrário, mostrar como **sugestão** com o resultado e deixar o usuário decidir ("Usar esta localização"). Dúvida nunca remapeia.

### 3. Relocalizar raiz ("Localizar novamente")
Ative o botão da Etapa E. Fluxo (ViewModel + diálogo): sugestões automáticas (se houver) → "Escolher pasta…" → validação com progresso → resultado legível → **[Usar esta localização]** (habilitado só se Forte; Parcial exige confirmação explícita com aviso do que ficará offline/ausente; Fraca bloqueia com explicação). Aplicar = **somente** `StorageRoot.CurrentPath` (+ `VolumeLabel/Serial/DriveType/LastSeenAt` novos), em transação, **sem tocar** em `Photos`; atualizar snapshot do resolver; reverificar disponibilidade; recarregar a biblioteca sem perder seleção/filtros quando possível. Bloquear se o novo caminho coincide com/está dentro de outra raiz de forma incoerente (S8) — mensagem clara. Registrar histórico leve (`OriginalPath` preservado; guardar `PreviousPath` em log/`Notes`).

### 4. Relocalizar pasta (subconjunto)
No menu de contexto da pasta na árvore e/ou na tela de armazenamento: **"Relocalizar pasta…"**. Entrada: pasta antiga (raiz + prefixo relativo, ex.: `Viagens\2026`) e pasta nova absoluta (ex.: `F:\Arquivo\Viagens\2026`). Estratégia (documente no plano):
1. Nova pasta **dentro da mesma raiz** → trocar o prefixo de `RelativePath` das fotos afetadas (UPDATE em massa, transação, mesma raiz, sem recriar fotos).
2. Nova pasta **dentro de outra raiz existente** (mais profunda, S8) → trocar `StorageRootId` e `RelativePath`.
3. Nova pasta **fora de qualquer raiz** → exige **criar nova raiz** (pasta escolhida vira raiz; as fotos ficam com `RelativePath` = resto); pedir confirmação do nome da raiz.
Valide o subconjunto com o mesmo `RootValidator` (apenas as fotos afetadas) antes; mostrar quantas fotos mudarão; conflito de `(raiz, relativo)` único = bloquear e listar; tudo em transação com rollback; `PhotoId` e demais dados intactos.

### 5. Testes
Validador: tudo ok/forte; parcial; tamanhos diferentes; hash divergente; pasta inexistente; amostragem determinística; cancelamento; fotos sem hash. Detector: serial confere + forte + único → aplica; serial confere mas validação parcial → só sugere; dois candidatos → só sugere; nenhum. **Pedido 4 (D→F):** só `CurrentPath` da raiz muda; **pedidos 5 e 6:** `PhotoId` e `RelativePath` iguais, **nenhuma linha de `Photos` alterada** (comparar antes/depois); múltiplas raízes independentes (**16**); NAS/UNC (**9**, com fake); relocalizar pasta nos 3 casos, conflito de unicidade, rollback; raiz relocalizada volta a `Online` e miniaturas/preview funcionam; arquivos do disco intactos (comparar hashes antes/depois). UI (STA): diálogo carrega, resultado forte/parcial/fraca renderizados (captura).

### Verificação e documentação
Build Debug+Release 0/0; `dotnet test` ≥ 5×; fluxo real com dados isolados: importar `docs/Fotos` em pasta temporária, renomear a pasta (simula troca de letra), relocalizar, conferir. Atualize `IMPLEMENTATION_STATUS.md`, `HANDOFF.md`, `ARCHITECTURE.md`, `KNOWN_LIMITATIONS.md` (ex.: validação por amostra não prova 100%), `MANUAL_TEST_CHECKLIST.md` (HD externo com outra letra, NAS, pasta movida) e o plano.

**Pare e relate.** Não inicie a Etapa G.
