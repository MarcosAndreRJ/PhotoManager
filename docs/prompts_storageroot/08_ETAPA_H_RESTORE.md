# PHOTOMANAGER — PORTABILIDADE · ETAPA H (restore com assistente de raízes, PreRestoreBackup e rollback)

## CONTEXTO COMUM

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Etapas A–G desta frente concluídas (`.pmb`, validador/relocalizador de raízes). Releia: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/STORAGE_ROOT_AND_BACKUP_PLAN.md` e **`docs/prompts_storageroot/00_ANALISE_E_DECISOES.md`** (S1–S14, em especial **S4, S9, S10, S12, S13**).

**Regras inegociáveis:** restore **nunca** altera, move, copia ou apaga fotografias; **nunca** sobrescrever o catálogo atual sem `PreRestoreBackup` e caminho de rollback; **`PhotoId` é preservado** (nada é recriado como novo); não mexer em subcoleções/drag de coleções, IA, upload, similaridade; tratar o `.pmb` como **entrada não confiável**; sem mocks; **sem regra de negócio em `.xaml.cs`**; build Debug e Release **0/0**; `dotnet test` verde **≥ 5× seguidas, uma por vez** (feche `PhotoManager.exe`); `[Collection("WpfUi")]`; `PM_SNAPSHOT_DIR`; `PHOTOMANAGER_ROOT`; **nunca testar sobre o banco real do usuário**; docs registram o que **não** foi verificado. **Pare ao fim e relate.** Esta é a última etapa da frente: não avance para outra.

---

## ESTA ETAPA — H

### 1. Fluxo (S12) — `RestoreService` + assistente em passos
1. **Selecionar** `.pmb`.
2. **Validar por completo, em pasta temporária** (sem tocar no catálogo): é ZIP válido; `manifest.json` presente e legível; `BackupVersion` conhecida; **`DatabaseVersion` ≤ versão do app** (maior = recusa com mensagem "gerado por versão mais nova"); versão menor/zero é aceita e migrada depois; todas as entradas existem e **SHA-256/tamanho** conferem com o manifest (corrupção → recusa); limites de segurança (nº de entradas, tamanho descompactado, **zip slip**: nenhuma entrada sai da pasta temporária; só caminhos esperados); `PRAGMA integrity_check` e `foreign_key_check` no `.db` extraído; contagens (`PhotoCount`, raízes) iguais às do manifest. Todo `RelativePath` do banco passa por `RelativePathNormalizer` (S9): qualquer um com `..`/absoluto = recusa. Resumo mostrado ao usuário (data, versão, nº de fotos, raízes, miniaturas sim/não).
3. **`PreRestoreBackup` obrigatório:** gerar um `.pmb` do estado atual com o `BackupService` da G (em `Backups\PreRestore\`, nome com data/hora); falhou = **não prossegue**. Nunca é apagado automaticamente.
4. **Aplicar:** `SqliteConnection.ClearAllPools()`, fechar/descartar repositórios e caches em memória, gravar o banco restaurado (arquivo temporário → *replace* atômico, mantendo o antigo como `.restore-old` até o fim), restaurar `settings.json`, miniaturas (se vieram), rodar `InitializeAsync` (migrações de banco mais antigo, inclusive a D se o backup for legado: nesse caso, o fluxo da D/"Organizar bibliotecas" se aplica depois do restore), recarregar o snapshot de raízes e os ViewModels da biblioteca (S12).
5. **Assistente de raízes:** para cada raiz do backup: verificar disponibilidade em `CurrentPath`; se ausente, rodar o `RootLocator` da F (serial/rótulo/estrutura/amostra) e mostrar **status por raiz, de forma independente**: `✓ localizada` (e como), `⚠ não encontrada — [Localizar…]`, `⚠ offline`. "Localizar…" reutiliza o diálogo e o `RootValidator` da F; aplicar só atualiza `StorageRoot.CurrentPath` (+ volume). **S13** vale: automático só com certeza. Cada raiz é tratada separadamente (HD Fotos, SSD Trabalho, NAS, Fotos locais).
6. **Concluir** é permitido com raízes não localizadas: as fotos ficam **offline** (E) e podem ser relocalizadas depois em "Bibliotecas e armazenamento". Miniaturas: se o backup as trouxe, usar; se não, regenerar quando o original estiver disponível; raiz offline sem miniatura → placeholder.
7. **Rollback:** qualquer falha entre os passos 3 e 6, ou "Cancelar" no assistente antes de concluir, restaura o `PreRestoreBackup` (banco, settings, miniaturas se alteradas), recarrega a biblioteca e informa. Depois de **Concluir**, oferecer "Desfazer restauração" enquanto a sessão estiver aberta (usa o mesmo `PreRestoreBackup`) e informar onde o arquivo fica. Se o próprio rollback falhar, mostrar instruções exatas com o caminho dos arquivos (`.restore-old`, `PreRestoreBackup`).

### 2. Preservação (verificar com teste)
`PhotoId`, tags, categorias, coleções, notas, avaliações, favoritos, metadados e versões, workflow, `UploadRecords`, histórico de metadados, presets, perfis, `Agencies`, `DuplicateIgnores`: idênticos aos do backup. Nenhuma foto recriada. Fotos do catálogo **anterior** que não estão no backup deixam de aparecer (é restauração, não mesclagem) — **texto de aviso explícito na confirmação** ("O catálogo atual será substituído; um backup dele será criado antes").

### 3. Restore em outro PC
Backup de um PC com `D:\Fotos` aberto em PC sem essa unidade: restaura, mostra raízes não localizadas, permite apontar `F:\Fotos` (validado) ou concluir offline. Usuário/máquina diferentes não importam (`MachineName` é só informativo). Simule com `PHOTOMANAGER_ROOT` distintos e pastas temporárias.

### 4. Testes (os 18 do pedido ficam completos aqui; mapeie no plano)
**11** restore ok com `PhotoId`/tabelas idênticas; **12** backup corrompido (byte alterado, entrada faltando, ZIP truncado, SHA divergente) recusado **sem tocar** no catálogo; **13** manifest incompatível (versão maior, campos ausentes, JSON inválido); zip slip e `RelativePath` malicioso recusados; **14** restore com raiz ausente conclui e deixa fotos offline; **15** restore "em outro PC" (raízes em caminhos inexistentes → relocalizar para pasta temporária equivalente → validação → `Online`); **16** várias raízes independentes (uma localizada, uma ausente, uma offline); **17** rollback: falha injetada em cada passo (3, 4, 5) devolve o estado exato anterior (comparar banco, settings, miniaturas); cancelar no assistente = rollback; **PreRestoreBackup** criado antes e válido; banco legado (sem raízes) é restaurado e migrado; banco com `user_version` maior é recusado; app com *pool* aberto (conexão em uso antes do restore) funciona após `ClearAllPools`; arquivos de fotos **inalterados** (hash antes/depois); restore repetido (idempotente). UI (STA): assistente carrega com raízes em cada estado, captura.

### 5. Fechamento da frente
1. **Regressão completa:** os **18 testes do pedido** mapeados com status; `grep` provando que não resta uso de `CurrentPath` de foto fora da Persistence (se alguma coisa sobrou, justifique).
2. **Integridade:** utilitário/teste que varre o banco (FK check, `RelativePath` válido e único, raízes sem sobreposição incoerente) e roda após migração, relocalização e restore.
3. **Desempenho:** volume (≥ 20.000 fotos, 3 raízes): abertura da biblioteca, verificação de raízes, backup, restore — tempos registrados.
4. **Documentação consolidada:** `docs/STORAGE_ROOT_AND_BACKUP_PLAN.md` com status final por item; `IMPLEMENTATION_STATUS.md`, `HANDOFF.md` (como estender: backup automático agendado, incluir originais **só** como opção explícita futura, criptografia — **não** implementar), `ARCHITECTURE.md`, `KNOWN_LIMITATIONS.md`, `MANUAL_TEST_CHECKLIST.md` (checklist **manual** completo: HD externo trocando de letra, NAS, formatar PC → restaurar, backup corrompido, rollback, cancelar no meio).
5. Remover código morto da transição (fallback legado, métodos antigos por caminho) e confirmar build sem avisos.

### Verificação
Build Debug+Release 0/0; `dotnet test` ≥ 5×; fluxo ponta a ponta com dados isolados e **dois** `PHOTOMANAGER_ROOT` (backup num, restore no outro, com a pasta de fotos em local diferente) e captura (sem mouse real).

**Pare e relate.** Não inicie coleções hierárquicas, upload, IA ou similaridade.
