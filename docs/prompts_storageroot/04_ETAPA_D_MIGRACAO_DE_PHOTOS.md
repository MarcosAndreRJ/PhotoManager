# PHOTOMANAGER — PORTABILIDADE · ETAPA D (migração das fotos para StorageRootId + RelativePath) — CRÍTICA

## CONTEXTO COMUM

Projeto **PhotoManager** (WPF · .NET 10 · MVVM · SQLite), `K:\Trabalho\Projetos\PHOTOMANAGER`. Etapas A, B e C desta frente concluídas (raízes, resolver, consumidores migrados; `Photos` ainda com `CurrentPath` e colunas novas nulas). Releia: `docs/HANDOFF.md`, `docs/ARCHITECTURE.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/STORAGE_ROOT_AND_BACKUP_PLAN.md` e **`docs/prompts_storageroot/00_ANALISE_E_DECISOES.md`** (S1–S14, em especial **S5, S6, S8**).

**Regras inegociáveis:** não mexer em subcoleções/drag de coleções, IA, upload, similaridade; nunca alterar arquivos de fotos; **preservar `PhotoId` e todas as tabelas filhas**; migração idempotente com backup prévio e teste com banco antigo; **a FK está LIGADA**; sem mocks; sem regra de negócio em `.xaml.cs`; build Debug e Release **0/0**; `dotnet test` verde **≥ 5× seguidas, uma por vez** (feche `PhotoManager.exe`); `[Collection("WpfUi")]`; `PHOTOMANAGER_ROOT` isola dados; **teste a migração só em CÓPIAS do banco, nunca no do usuário**; docs registram o que **não** foi verificado. **Pare ao fim e relate** e espere `CONTINUE`.

---

## ESTA ETAPA — D: inferir raízes, confirmar com o usuário e converter o catálogo

Se a frente de coleções (`docs/prompt_colecoes`) já tiver rodado, parta do schema dela e incremente `user_version` (S14). Se houver **qualquer** dúvida de coordenação, pare e pergunte.

### 1. Inferência (função pura, sem filesystem) — `RootInferenceService`
Entrada: lista de pastas das fotos (já em texto). Saída: **proposta** (`ProposedRoot { Name, Path, PhotoCount, IsAmbiguous, Reason, DriveType (do texto: unidade/UNC) }`) conforme **S5**: agrupar por unidade/UNC; raiz candidata = maior ancestral comum que não seja a raiz da unidade; ramos independentes viram raízes separadas; fotos soltas na raiz da unidade → ambíguo; nunca assumir `D:\`. Determinística e testável com listas de caminhos (incluir: um único ramo; dois ramos na mesma unidade; duas unidades; UNC; foto direto em `D:\`; pastas com acentos/caixa diferente; um caminho prefixo do outro; 1 foto só). Se a unidade/pasta estiver acessível, leia `VolumeInfoReader` (rótulo/serial/tipo) **apenas para preencher** a proposta; inacessível = campos nulos.

### 2. Confirmação (diálogo, antes de qualquer gravação)
Na primeira abertura após a atualização com fotos legadas (`StorageRootId IS NULL`), mostre a janela **"Organizar bibliotecas"**: lista de raízes propostas (nome editável, caminho, nº de fotos, aviso de ambiguidade com o motivo), botão "Alterar pasta…" (só aceita um **ancestral** das pastas daquelas fotos), "Dividir/Mesclar" apenas se a complexidade for baixa (senão registre como limitação), **Confirmar** e **Decidir depois** (app abre em modo legado funcional: resolver com fallback; nada é gravado). Nada é migrado sem confirmação. Regra de negócio no ViewModel; code-behind só diálogo. **Ambiguidade não resolvida = pare e documente; não adivinhe.**

### 3. Migração (S6) — serviço `StorageRootMigration`
1. Backup `photomanager.db.pre-storageroot.bak` (cópia usando a **API de backup do SQLite**, não cópia de arquivo aberto; uma vez; não sobrescreve existente).
2. Criar as raízes confirmadas (`OriginalPath = CurrentPath` = pasta confirmada).
3. Conexão dedicada: `PRAGMA foreign_keys=OFF` **antes** do `BEGIN`; criar `Photos_new` **sem** `CurrentPath`, com `StorageRootId INTEGER NOT NULL REFERENCES StorageRoots(Id)`, `RelativePath TEXT NOT NULL`, `UNIQUE(StorageRootId, RelativePath COLLATE NOCASE)` e **todas** as demais colunas atuais; copiar mantendo `Id`; calcular `(raiz, relativo)` pela raiz **mais profunda** (S8) usando `RelativePathNormalizer`; foto sem raiz correspondente = **aborta** (não perde nada) e relata.
4. `DROP TABLE Photos`; `RENAME`; recriar índices (`IsMissing`, `(StorageRootId, RelativePath)`); recriar o que dependia.
5. Verificações antes do `COMMIT`: `PRAGMA foreign_key_check` **vazio**; contagem de `Photos` e de **cada** tabela filha (`PhotoTags`, `PhotoCollections`, `MetadataHistory`, `UploadRecords`, `DuplicateIgnores` etc.) **igual** à anterior; todo `(raiz, relativo)` resolve de volta ao mesmo `CurrentPath` antigo (comparação sem caixa). Qualquer falha = `ROLLBACK`, banco intacto, mensagem clara, `foreign_keys=ON` religado.
6. `PRAGMA user_version` incrementado. Idempotente: segunda execução não faz nada.
7. Remover o fallback legado do resolver e `CurrentPath` do `Photo`/repositório; `FindByPathAsync` passa a `(raiz, relativo)`; `ImportFolderAsync` aplica **S8** (raiz existente que contém a pasta, senão cria raiz **na pasta importada**; mostra no relatório de importação qual raiz foi usada/criada); `FileOperationService` mover/copiar/renomear grava `(StorageRootId, RelativePath)` pela função `ApplyLocation` da C; mover para pasta fora de qualquer raiz = pedir para registrar nova raiz (nunca gravar caminho absoluto).

### 4. Testes (todos com bancos temporários; banco "antigo" fabricado com o schema legado e dados reais nas filhas)
Inferência (casos acima); migração preserva `Id`, tags, coleções, histórico de metadados, `UploadRecords`, favoritos, notas, hash; **nenhuma linha filha se perde** (o teste que pegaria o cascade); `foreign_key_check` vazio; `.bak` criado e restaurável; idempotência; rollback em falha simulada (ex.: foto fora de qualquer raiz) deixa o banco **idêntico**; `RelativePath` único sem caixa; import novo cria raiz na pasta escolhida; import dentro de raiz existente reusa; ancestral de raízes existentes (S8); mover dentro da raiz atualiza `RelativePath` e preserva `PhotoId` (**pedido 7**); mover entre raízes troca `StorageRootId` (**pedido 8**); `PhotoId` e `RelativePath` estáveis (**5, 6**); "Decidir depois" não grava nada. Teste de **volume** (≥ 20.000 fotos, 3 raízes): tempo da migração registrado. Teste manual em **cópia** de um banco real de exemplo (documente o resultado e como reproduzir).

### Verificação e documentação
Build Debug+Release 0/0; `dotnet test` ≥ 5×; app com dados isolados: abrir banco legado de exemplo → janela de confirmação → conferir contagens e que a biblioteca é idêntica (captura). Atualize `IMPLEMENTATION_STATUS.md`, `HANDOFF.md` (novo schema, como reverter com o `.bak`), `ARCHITECTURE.md`, `KNOWN_LIMITATIONS.md`, `MANUAL_TEST_CHECKLIST.md` (inclua restaurar o `.bak` à mão) e o plano.

**Pare e relate**, incluindo a lista de raízes que o seu banco real geraria (rodando só em cópia) e qualquer ambiguidade que exija decisão. Não inicie a Etapa E.
