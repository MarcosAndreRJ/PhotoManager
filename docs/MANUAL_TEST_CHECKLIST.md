# Checklist manual

Legenda: **[x]** verificado nesta auditoria (automático ou executando o aplicativo) · **[ ]** pendente de verificação manual.
Dica: para não usar seus dados reais, defina `PHOTOMANAGER_ROOT` antes de abrir o aplicativo.

## Fase 1 — Shell

- [x] Restaurar e compilar a solução.
- [x] Abrir o executável; a janela abre com a nova barra superior.
- [x] Largura mínima (1100×650) mantém tudo utilizável; 1366×768 e 1920×1040 conferidos.
- [x] Clicar em cada aba (Biblioteca, Metadados, Microstock, Ferramentas, Configurações) mostra o conteúdo da área (teste automatizado).
- [x] Voltar à Biblioteca preserva filtros, seleção e miniaturas (mesma instância).
- [ ] Segunda instância encerra sem abrir outra janela.
- [ ] Exceção controlada mostra mensagem e grava o stack trace no log.
- [x] Pastas `Data`, `Cache` e `Logs` são criadas; `photomanager.log` é criado.

## Fase 2 — Catálogo

- [ ] Adicionar uma pasta com JPG, JPEG, PNG e WEBP (diálogo de pasta).
- [x] Indexação recursiva de subpastas (JPG/PNG).
- [x] Arquivo corrompido não interrompe a importação.
- [x] Reabrir preserva o catálogo (`photomanager.db`).
- [x] Miniaturas criadas em `Cache\Thumbnails`; a grade aparece antes das miniaturas.
- [x] Selecionar foto mostra preview, nome, pasta, tamanho, dimensões e datas.
- [ ] Mover/renomear uma foto fora do aplicativo e reabrir: aparece "Arquivo ausente" e a foto continua no catálogo.
- [x] Importar de novo a mesma pasta não duplica.

## Fase 3 — Organização

- [x] Categoria, tags (várias), coleções (várias), nota, avaliação e favorito persistem após recarregar.
- [x] Busca por nome/nota; filtros por categoria, tag, coleção, avaliação mínima e favoritas.
- [x] Sidebar: Todas, Favoritas, Recentes, Sem categoria, Arquivos ausentes, pastas, categorias, tags, coleções com contagens; clicar filtra.
- [x] Coração no card favorita/desfavorita e persiste.
- [ ] Selecionar várias fotos com Ctrl/Shift e ver "N selecionada(s)" na barra de status.
- [x] Edição em lote de categoria/tag/coleção sem alterar os arquivos.
- [ ] Estrelas do painel: clicar altera, clicar na mesma estrela limpa, "Salvar organização" grava.

## Fase 4 — Operações de arquivo

- [x] Mover mantém `PhotoId` e organização; não sobrescreve arquivo existente no destino.
- [ ] Botão **Mover** da barra de ferramentas com diálogo de pasta.
- [x] Copiar sem catálogo / com catálogo (nova identidade) — lógica testada.
- [ ] Botão **Copiar** + caixa "Adicionar cópias ao catálogo" (aba Lote e arquivos).
- [x] Renomear (individual e por template `{date}_{name}`, `{year}-{month}-{sequence}`) mantém extensão e identidade.
- [x] Conflito de nome não sobrescreve.
- [x] Excluir envia para a Lixeira e marca a foto como ausente.
- [ ] Botão **Excluir** mostra confirmação antes de enviar para a Lixeira.
- [ ] Operação em lote com seleção múltipla pela interface.

## Visual (refatoração)

- [x] Barra superior escura com aba ativa destacada.
- [x] Sidebar com seleção destacada e contagens.
- [x] Cards com miniatura, selo de tipo, favorito, nome, estrelas e dimensões.
- [x] Painel direito: preview com anterior/próxima e posição "n de total"; abas Informações / Organização / Lote e arquivos.
- [x] Estado vazio da grade e do painel.
- [ ] Rolagem com um catálogo grande (milhares de fotos): verificar fluidez e memória.
- [ ] Windows 10 vs 11: conferir se os ícones (Segoe Fluent/MDL2) aparecem.

## Fase 5 — Leitura de metadados

- [x] Estado vazio, preview, EXIF, título/autor/copyright/palavras-chave, anterior/próxima, foto sem metadados, arquivo corrompido/ausente (testes e execução real).
- [x] 3 CR2 reais de `docs/Fotos` lidas corretamente (câmera, lente, ISO, exposição, abertura, distância focal, data, autor).
- [ ] Foto com GPS: coordenadas corretas e "Abrir no mapa".
- [ ] Foto com IPTC/XMP gravado no Lightroom/Photoshop.
- [ ] WEBP/PNG reais.

## Fase 6 — Edição segura de metadados (use CÓPIAS de JPEGs reais)

- [x] Editar título/descrição/autor/copyright, salvar e reler: valores iguais, EXIF e imagem intactos (teste automatizado).
- [x] Palavras-chave: Enter/vírgula adiciona, × remove, Limpar; contadores; vermelho ao ultrapassar.
- [x] Salvar desabilitado sem alterações; Reverter descarta; trocar de foto com pendências pergunta (ViewModel).
- [x] Falha na gravação deixa o original intacto, sem `.pm-tmp`/`.pm-bak` sobrando.
- [x] Versão (v1, v2…) e histórico atualizam; nada muda → nenhuma versão nova.
- [x] Banco antigo (sem `MetadataVersion`) é atualizado preservando dados.
- [x] PNG/WEBP: campos bloqueados com explicação.
- [ ] Salvar com o JPEG aberto em outro programa: mensagem clara, original intacto.
- [ ] Abrir o JPEG salvo no **Lightroom/Photoshop/Bridge/Explorador do Windows (Detalhes)** e conferir título, descrição, keywords, autor e copyright com acentos.
- [ ] Enviar para um banco de imagens (ex.: Adobe Stock) e conferir se lê título/keywords.
- [ ] Foto de câmera real: comparar EXIF antes/depois (ExifTool/Explorador) — deve ser idêntico.
- [ ] Copiar metadados de uma foto, Colar em outra, Salvar.
- [ ] Botões Salvar/Reverter/Copiar/Colar e a caixa de confirmação ao trocar de foto com alterações (diálogo).
- [ ] Editar uma foto e voltar à Biblioteca: tamanho/data atualizados no painel Informações.

## Fase 7 + ajustes — Edição em lote, RAW e árvore de pastas

- [x] Combos da barra de filtros planos e proporcionais ao layout.
- [x] Importar `docs/Fotos` (CR2): 3 fotos catalogadas com miniatura, preview, 5184×3456 e selo "CR2".
- [x] CR2: metadados EXIF lidos; edição bloqueada com explicação.
- [x] Importar uma pasta com subpastas: árvore com ramificação e contagens; clicar filtra pasta + subpastas.
- [x] Árvore: expandir/recolher preservado ao recarregar; mover fotos para pasta nova atualiza a árvore.
- [x] Lote: operações por campo; pré-visualização; aplicar com confirmação; resultado por foto; idempotência.
- [x] Lote: PNG/WEBP/RAW/ausentes ignorados com motivo; falha em uma foto não interrompe as outras.
- [x] Presets: salvar, atualizar por nome, carregar, excluir; persistem entre janelas.
- [ ] Importar uma pasta real grande (centenas de CR2/JPEG) e conferir tempo de importação e rolagem da árvore.
- [ ] Rotacionar: CR2 em retrato aparece em pé na miniatura e no preview.
- [ ] Lote em ~50 JPEGs reais de câmera: conferir EXIF idêntico antes/depois e abrir no Lightroom/Explorador.
- [ ] Botões reais da janela de lote (confirmação, cancelar durante a gravação, fechar bloqueado ao gravar).
- [ ] Botão **Metadados** da Biblioteca com 1, vários e nenhuma foto selecionada.
- [ ] Windows 10: ícones do chevron e das pastas na árvore.

## Fase 8 — Workflow microstock local

- [x] Central de produção aparece na aba Microstock e o smoke de UI instancia a tela.
- [x] Perfis de validação têm seed genérico, migração idempotente, seleção do perfil ativo, criação/edição/duplicação e proteção contra excluir o último.
- [x] Regras puras, precedência dos estados, migração em banco antigo, volume sintético de 2.000 fotos, progresso e cancelamento cobertos por testes.
- [ ] Executar o app real com cópias de JPEG com e sem metadata e confirmar visualmente “Pronta”, “Metadata incompleto” e “Não preparada”.
- [ ] Executar com PNG e CR2 reais e confirmar visualmente o comportamento somente leitura/ausente.
- [ ] Alterar metadata na aba Metadados, voltar à Central e confirmar atualização do status.
- [ ] Trocar perfil ativo e confirmar a atualização da tabela e dos contadores.
- [ ] Conferir a tela em 1100×650, 1366×768 e 1920×1080 com catálogo grande; a execução visual desta fase ainda não foi feita.

## Fase 9 — Agências e histórico de envio manual

- [x] Migração idempotente de banco antigo cria `Agencies`/`UploadRecords` e preserva fotos (testes automatizados).
- [x] Seed das cinco agências, CRUD seguro por renomear/ativar/desativar/reordenar e histórico append-only (testes automatizados).
- [x] Marcar enviado, erro, rejeitado, desfazer, capturar versão da metadata e filtrar estado/agência (serviços e ViewModel compilados/testados).
- [x] Smoke automatizado continua instanciando a Central de Produção e a navegação.
- [ ] Abrir o app com cópia de uma foto, selecionar duas ou mais linhas, marcar **Enviado**, confirmar e verificar as colunas/resumo.
- [ ] Registrar **Erro** e **Rejeitado** com motivo; conferir o estado e o histórico da foto.
- [ ] Usar **Desfazer** e confirmar que o estado volta a Pendente sem apagar as entradas anteriores.
- [ ] Adicionar/renomear, desativar e reordenar um banco; conferir que banco desativado não é exigido em “Enviada para todos”.
- [ ] Fechar e reabrir com `PHOTOMANAGER_ROOT` isolado; confirmar persistência do histórico e dos filtros.
- [ ] Conferir visualmente a tela em 1100×650, 1366×768 e 1920×1080; a superfície WPF nativa não ficou disponível nesta execução.

## Fase 10 — Duplicatas exatas

- [x] Pré-filtro por tamanho, SHA-256 em streaming, cache persistido, invalidação, falhas individuais, cancelamento/retomada e grupos ignorados (testes automatizados).
- [x] Smart list **Duplicadas** na Biblioteca e tela Ferramentas entram no smoke automatizado de navegação.
- [ ] Rodar **Procurar duplicatas** com duas cópias idênticas em pastas distintas e conferir grupo, miniaturas, caminho, tamanho e organização.
- [ ] Confirmar que arquivos de tamanho único não geram leitura/hash e que arquivos diferentes com o mesmo tamanho não são agrupados.
- [ ] Marcar uma cópia como **Manter esta**, conferir que mover/Lixeira ficam bloqueados para ela e executar a ação em outra cópia.
- [ ] Ativar a mesclagem explícita e confirmar tags, coleções, nota e avaliação na cópia mantida antes da Lixeira.
- [ ] Ignorar um grupo, fechar/reabrir e confirmar que ele continua fora da lista; desfazer a decisão em uma nova busca automatizada/API.
- [ ] Alterar metadata de uma cópia, buscar novamente e confirmar recalculo do hash.
- [ ] Conferir a tela com milhares de fotos e uma seleção real de pasta; a execução visual completa permanece pendente neste ambiente.


---

## Rodada de UX pós-Fase 10 — Etapa 2 (seleção e painel da Biblioteca)

- [x] Cada card tem caixa de seleção visível; marcar/desmarcar alterna a foto sem Ctrl (teste em WPF real).
- [x] Contador "N selecionadas" na barra e na barra de status; "Selecionar tudo" e "Limpar seleção" funcionam e as caixas acompanham.
- [x] Painel direito: vazio sem seleção; preview+detalhes com 1; resumo "N fotos selecionadas" com 2+.
- [x] Aba Organização com 2+ fotos: campos vazios mantêm; aplicar altera só as selecionadas; seleção permanece depois de aplicar.
- [x] Aba Arquivos: textos de alvo ("Será aplicado às N fotos"), botões desabilitados sem seleção; renomear 1 foto por nome e várias por template.
- [x] Aba Metadados: mostra X de N editáveis; botão abre o editor em lote.
- [x] 1100×700 e 1366×768 sem cortes relevantes.
- [ ] Ctrl+clique e Shift+clique reais com o mouse (faixa de seleção) e Ctrl+A.
- [ ] Clicar nas caixas com o mouse em um catálogo grande (milhares) e rolar: fluidez.
- [ ] Mover/Copiar/Excluir pelos botões da aba (diálogos de pasta e confirmação com a contagem).
- [ ] Selecionar fotos, aplicar Organização em lote e conferir persistência após fechar/abrir o app.
- [ ] Windows 10: ícones das novas barras e do check.


---

## Rodada de UX pós-Fase 10 — Etapa 3 (modo de revisão)

- [x] Entrar pela API/botão "Revisar"; Esc e "Voltar" retornam à grade com a mesma foto (testado).
- [x] Anterior/próxima (botões e setas), Home/End, índice "n de total" formatado.
- [x] Filmstrip: todas as fotos da lista visível, foto atual destacada, clique troca a foto.
- [x] Zoom: Ajustar, 100 %, +/−; Ajustar não amplia; foto nova volta a ajustar (testado no `ZoomViewer`).
- [x] Tela cheia: esconde navegação, painel e filmstrip; Esc sai (lógica testada).
- [x] Abas Informações / Metadados / Microstock / Histórico mostram dados reais e acompanham a navegação.
- [x] Favorito e estrelas gravam na hora.
- [ ] Duplo clique real no card abre a revisão; duplo clique no coração/caixa **não** abre.
- [ ] Roda do mouse (zoom ancorado no cursor) e arrastar para mover com zoom > Ajustar.
- [ ] Tela cheia real: cobre a barra de tarefas; Esc restaura tamanho/estado anteriores (normal e maximizada).
- [ ] CR2/JPEG grandes (24–40 MP): troca de foto fluida; "Resolução completa" aparece; memória estável após navegar por dezenas de fotos.
- [ ] Rotacionar: CR2 em retrato aparece em pé na revisão.
- [ ] Botões "Editar na tela Metadados" e "Abrir Central de Produção" saem da revisão e abrem a tela certa com a foto selecionada.
- [ ] Windows 10: ícones da barra superior.


---

## Rodada de UX pós-Fase 10 — Etapa 4 (editor de metadados e lote)

- [x] Marcar 2+ fotos na Biblioteca e abrir o editor: uma linha por foto, com os mesmos arquivos (teste em WPF real).
- [x] Edição por linha: título, descrição, palavras-chave (chips), autor, copyright; contadores; campos alterados em destaque; reverter por linha.
- [x] Operações globais por campo aplicadas aos rascunhos; nada gravado até "Salvar"; cada foto mantém seus valores nos campos "Manter".
- [x] Só as fotos marcadas recebem a operação; Marcar/Desmarcar todas.
- [x] Adicionar/remover/limpar/substituir palavras-chave sem duplicadas; descrição acrescentar; copiar da foto em foco; eliminar repetidas.
- [x] Salvar N alteradas: grava só as alteradas, versão e histórico por foto, EXIF intacto, relê o arquivo; falha em uma não impede as demais.
- [x] PNG/WEBP/RAW/ausentes desabilitados com o motivo.
- [x] Presets no editor (salvar, carregar, excluir, persistem).
- [x] Voltar à Biblioteca restaura a seleção múltipla.
- [ ] Digitar de verdade em várias linhas e salvar; conferir o resultado no Lightroom/Explorador (acentos).
- [ ] 50–200 JPEGs reais de câmera: fluidez da lista, tempo de leitura, tempo de "Salvar", cancelar no meio.
- [ ] "Reverter tudo" e a confirmação (diálogo) com mouse.
- [ ] Fechar o aplicativo com rascunhos pendentes (não há aviso): confirmar que nada é gravado sem "Salvar".
- [ ] Fluxos vindos da revisão ("Editar na tela Metadados") e da Central de Produção ("Abrir em Metadados") com uma e várias fotos.
- [ ] Windows 10: ícones do editor.

---

## Nova etapa de organização — ETAPA 2 (Sem coleção e ícone)

- [x] Entrada virtual **Sem coleção** aparece na barra lateral sem criar uma coleção persistida.
- [x] Selecionar **Sem coleção** filtra somente fotos sem itens em `Photo.Collections`.
- [x] A contagem da entrada diminui quando uma foto recebe coleção e é salva; a foto deixa de aparecer no filtro.
- [x] Ícone próprio configurado no título da janela e no executável WPF.
- [ ] Em uma sessão WPF nativa, conferir visualmente o ícone no título, barra de tarefas e Alt+Tab.
- [ ] Repetir com fotos reais: uma foto sem coleção, uma com coleção e uma que recebe a primeira coleção durante a sessão.
- [ ] ETAPA 3, drag-and-drop, CRUD de pastas, Explorer e subcoleções: **não testar nesta entrega; ainda não implementados**.

---

## Coleções e subcoleções — Etapa B ("Todas", "Sem coleção", contadores e combo)

- [x] Item virtual **Todas** no topo da seção COLEÇÕES com contagem de fotos com ≥ 1 coleção (teste automatizado e STA).
- [x] Tooltip de **Todas** exibe *"Fotos que pertencem a pelo menos uma coleção"*.
- [x] Clicar em **Todas** filtra apenas fotos que têm pelo menos uma coleção (não a biblioteca inteira).
- [x] Item virtual **Sem coleção** exibe apenas fotos sem nenhuma coleção, sem criar registro físico na tabela `Collections`.
- [x] Separador visual discreto abaixo de **Sem coleção**, antecedendo as coleções reais em ordem alfabética.
- [x] Invariante conferida: `Todas + Sem coleção == Total de fotos do catálogo` (incluindo ausentes).
- [x] ComboBox de Coleções na barra de ferramentas exibe opção padrão neutra **"Qualquer"** e não expõe chaves internas (`__sem_colecao__` / `__todas_colecoes__`).
- [x] Mútua exclusão entre **Todas**, **Sem coleção** e coleções reais; `HasActiveFilters` reflete seleção de "Todas".
- [x] Contadores recalculam imediatamente em memória após salvar organização individual, lote ou mesclagem de duplicatas.
- [ ] Em sessão interativa com mouse: passar o ponteiro sobre "Todas" e conferir o surgimento do tooltip do Windows.
- [ ] Em sessão interativa com mouse: clicar alternadamente entre "Todas", "Sem coleção" e uma coleção real, verificando a troca suave de destaque e o filtro da grade.

---

## Coleções e subcoleções — Etapa C1 (Modelo, Migração e Serviço — sem UI nova)

- [x] Migração automática de banco legado flat para hierarquia com coluna `ParentCollectionId` e criação de backup único `photomanager.db.pre-colecoes.bak`.
- [x] Migração roda com `PRAGMA foreign_keys = OFF` impedindo remoção acidental em cascata de `PhotoCollections`, seguida de `PRAGMA foreign_key_check` e verificação de zero órfãos.
- [x] Idempotência de inicialização do repositório (rodar N vezes não altera contagens nem sobrescreve o `.bak`).
- [x] Entidade `Collection` com `Id`, `Name`, `ParentCollectionId?`, `SortOrder` e `CreatedAt`.
- [x] `Photo.CollectionIds` armazena a verdade persistida dos IDs; `Photo.Collections` projeta nomes com desambiguação `"Pai / Filho"` para homônimos em ramos diferentes.
- [x] `ICollectionService.CreateAsync` valida nome (`Trim`, não vazio, até 100 caracteres) e impede duplicação entre irmãos (case-insensitive).
- [x] Criação de coleções homônimas permitida em pais diferentes ou raízes diferentes.
- [x] Prevenção rigorosa de ciclos: mover para si mesmo (A→A), mover para filho direto (A→B→A) e mover para descendente indireto (A→...→D).
- [x] Limite de profundidade máxima de 8 níveis respeitado ao criar e ao mover subárvores.
- [x] `DeleteAsync(PromoteChildren)` promove filhos para o pai da coleção excluída e renomeia conflitos com sufixo ` (2)` mantendo fotos nas subcoleções.
- [x] `DeleteAsync(WithDescendants)` remove a coleção e toda a subárvore e suas relações em `PhotoCollections`.
- [x] Arquivos físicos em disco e registros da tabela `Photos` nunca são excluídos em nenhuma operação de coleções.
- [x] `ICollectionRepository.SaveAsync` e `SqliteCatalogRepository.SaveAsync` de organização nunca tocam nem removem associações a coleções.
- [x] Contadores diretos por coleção calculados sem somar fotos de descendentes.
---

## Coleções e subcoleções — Etapa C2 (Árvore visual, CRUD, diálogos e chips)

- [x] Árvore `TreeView` de coleções na barra lateral com chevrons expansíveis/recolhíveis, ícones e badges com contagens diretas.
- [x] Tooltip dos nós da árvore exibe contagem direta e contagem na subárvore (`X fotos diretas · Y em subcoleções`).
- [x] Expansão e recolhimento de ramos persistidos em memória durante a navegação e reconstrução.
- [x] Seleção de nós na árvore filtra a grade de fotos estritamente pela coleção selecionada (respeitando desambiguação por `Path`).
- [x] Botão `[ + ]` no cabeçalho da seção COLEÇÕES abre diálogo modal para criar coleção raiz.
- [x] Menu de contexto nos nós da árvore com opções: **Nova subcoleção**, **Renomear (F2)**, **Mover para...**, **Excluir (Del)**.
- [x] Diálogo modal **Criar / Renomear**: foco automático com seleção de texto, validação reativa (nome obrigatório, máx 100 caracteres, bloqueio de irmãos duplicados case-insensitive) e botão Salvar como padrão (Enter).
- [x] Diálogo modal **Mover**: seletor em árvore identada com bloqueio do próprio nó, de seus descendentes (prevenção de ciclo) e do pai atual.
- [x] Diálogo modal **Excluir**: opções claras "Promover subcoleções" vs "Excluir com subcoleções", exibição de contagens de impacto e aviso explícito "Nenhum arquivo físico será excluído."
- [x] Painel de Organização individual: chips visuais para cada coleção associada à foto com botão `(×)` para remoção imediata e ComboBox "Adicionar à coleção ▾" para vincular novas coleções.
- [x] Painel de Organização em lote: chips visuais para coleções a adicionar com botão `(×)` e ComboBox seletor alimentando `OrganizationBatch` por IDs (`CollectionsToAdd`).
- [x] Invariante absoluta: nenhuma operação de coleção (criar, renomear, mover, excluir) exclui arquivos físicos em disco ou registros da tabela `Photos`.
- [ ] Em sessão interativa com mouse: expandir e recolher ramos com clique no chevron e conferir persistência do estado.
- [ ] Em sessão interativa com mouse: usar atalhos de teclado `F2` (renomear) e `Del` (excluir) com um nó selecionado na árvore.

---

## Coleções e subcoleções — Etapa D (Drag-and-drop de fotos para coleções)

- [x] Lógica pura de planejamento `DropPlanner` cobrindo adição, movimentação, menu de origem ambígua, nós virtuais, área vazia e no-op (testes automatizados).
- [x] Regra de seleção múltipla: arrastar card selecionado move/adiciona todas as fotos marcadas; arrastar card não selecionado move/adiciona apenas ele sem alterar a seleção atual.
- [x] Limiar de movimento configurado com `MinimumHorizontalDragDistance` / `MinimumVerticalDragDistance` impedindo falsos inícios de arraste.
- [x] Convivência preservada com clique simples de seleção, Ctrl+clique, Shift+clique, duplo clique (abrir revisão), checkbox de seleção no card e botão de favorito (coração).
- [x] Adicionar fotos via drop atualiza `CollectionIds` em memória, contadores da sidebar e banco SQLite sem duplicar relações existentes.
- [x] Mover fotos via Shift+drop remove exclusivamente da coleção de origem ativa na sidebar, adiciona ao destino, atualiza contadores e remove as fotos da visão da grade se a origem for o filtro ativo.
- [x] Invariante absoluta: nenhuma operação de drag-and-drop altera, move ou apaga arquivos físicos no disco nem registros da tabela `Photos`.
- [x] Concorrência lógica: salvar organização individual ou em lote após um drag-and-drop preserva as associações recém-criadas.
- [ ] Em sessão interativa com mouse: clicar e arrastar uma foto única para um nó de coleção real na barra lateral e conferir o feedback flutuante `+ Adicionar 1 foto a “Nome”`.
- [ ] Em sessão interativa com mouse: selecionar 3 fotos com Ctrl/Shift ou checkbox e arrastar uma das selecionadas; conferir o feedback `+ Adicionar 3 fotos a “Nome”` e a atualização dos contadores ao soltar.
- [ ] Em sessão interativa com mouse: selecionar 3 fotos, mas clicar e arrastar uma quarta foto não selecionada; conferir que apenas a foto clicada é arrastada (`+ Adicionar 1 foto...`) e que a seleção anterior não é perdida.
- [ ] Em sessão interativa com mouse: filtrar a grade por uma coleção real, segurar `Shift` e arrastar fotos para outra coleção; conferir feedback `→ Mover N fotos de “Origem” para “Destino”`, conferir remoção das fotos da grade e atualização dos contadores.
- [ ] Em sessão interativa com mouse: com o filtro da grade em "Todas" ou em uma pasta (sem coleção ativa na sidebar), segurar `Shift` e soltar sobre uma coleção; conferir abertura do menu de contexto suspenso com opções de Mover das coleções comuns, Adicionar ou Cancelar.
- [ ] Em sessão interativa com mouse: arrastar fotos para uma coleção onde elas já pertencem; conferir feedback `A foto já pertence à coleção “Nome”` e ausência de duplicação.
- [ ] Em sessão interativa com mouse: arrastar sobre "Todas", "Sem coleção", pastas, tags ou área vazia; conferir cursor de bloqueio e feedback indicando destino inválido.
- [ ] Em sessão interativa com mouse: pausar o cursor durante o arraste sobre um nó recolhido com filhos por ~700 ms e conferir sua auto-expansão.
- [ ] Em sessão interativa com mouse: aproximar o cursor do topo ou da base da barra lateral durante o arraste e conferir a auto-rolagem suave do `ScrollViewer`.

---

## Coleções e subcoleções — Etapa E (Arrastar coleções, reparentar, raiz, ciclos e fechamento)

- [x] Lógica pura de planejamento `CollectionDropPlanner` testada por unidade para todos os cenários (mover para coleção, mover para raiz, si mesma, ciclos diretos e indiretos, profundidade máxima de 8 níveis, irmãos homônimos, nós virtuais, no-op).
- [x] Prevenção rigorosa de ciclos na UI em tempo real sem I/O síncrono e defesa defensiva transacional no `ICollectionService.MoveAsync`.
- [x] Arrastar subcoleção para o cabeçalho "COLEÇÕES" move a subcoleção para a raiz (`ParentCollectionId = null`), atualiza a árvore, expande o nó e exibe status correspondente.
- [x] Cabeçalho "COLEÇÕES" só é destino válido para coleções (rejeita fotos com feedback informativo).
- [x] Arrastar nós virtuais ("Todas", "Sem coleção") é impedido; soltar coleções sobre nós virtuais exibe bloqueio imediato com motivo.
- [x] Conflito de nomes entre irmãos: destino com irmão homônimo bloqueia o drop e exibe mensagem identificando o nome conflitante.
- [x] Estouro de profundidade: calcular a soma da profundidade do destino com a altura da subárvore arrastada; bloqueia se exceder 8 níveis.
- [x] Convivência de formatos no mesmo `TreeView`: nós recebem payloads `PhotoManager.PhotoIds` ou `PhotoManager.CollectionId` decidindo a ação de acordo com o tipo presente.
- [x] Auditoria de integridade do catálogo: zero relações órfãs em `PhotoCollections`, zero ciclos no grafo de coleções, fotos e arquivos em disco 100% preservados.
- [x] Desempenho em volume sintético: 5.000 fotos × 200 coleções em 4 níveis com cálculo de contadores diretos e árvore em memória em tempo inferior a 1 segundo.
- [ ] Em sessão interativa com mouse: clicar e arrastar uma coleção real sobre outra coleção real; conferir feedback flutuante `Mover “Filha” para “Pai”`, o realce do nó de destino e a mudança de hierarquia ao soltar.
- [ ] Em sessão interativa com mouse: clicar e arrastar uma subcoleção sobre o cabeçalho "COLEÇÕES"; conferir feedback `Mover “Nome” para a raiz`, o realce azul suave na faixa do cabeçalho e a promoção para a raiz ao soltar.
- [ ] Em sessão interativa com mouse: arrastar uma coleção pai sobre um de seus filhos ou netos; conferir o cursor de bloqueio e a mensagem `Ciclo detectado: não é permitido mover uma coleção para dentro de seus próprios descendentes.`
- [ ] Em sessão interativa com mouse: arrastar uma coleção sobre si mesma ou sobre seu pai atual; conferir feedback indicando que já está no local (no-op).
- [ ] Em sessão interativa com mouse: arrastar fotos da grade para um nó e em seguida arrastar uma coleção para o mesmo nó; conferir que ambos os fluxos convivem perfeitamente sem conflito.





## Coleções — menu de contexto (regressão do estouro de pilha)
- [ ] Selecionar uma coleção na árvore (clique) e, com ela selecionada, clicar com o botão direito → **Nova subcoleção…** → digitar nome → Criar. O app não fecha e a subcoleção aparece (pai expandido).
- [ ] Repetir com **Renomear…**, **Mover para…** e **Excluir…** (os dois modos de exclusão). O app não fecha em nenhum.
- [ ] F2 e Delete com a coleção selecionada.
- [ ] Menu de contexto em uma coleção diferente da selecionada.
- [ ] Os diálogos abrem centralizados sobre a janela principal.

## Seleção múltipla e arraste
- [ ] Modo normal: marcar 3 fotos (Ctrl+clique), apertar e arrastar uma delas até uma coleção → as 3 vão e continuam marcadas; clique simples numa foto marcada (sem arrastar) reduz a seleção a ela.
- [ ] Botão **Multi-seleção** desligado: sem caixas nas fotos. Ligado: caixas visíveis; clicar na foto marca/desmarca; Shift+clique seleciona intervalo.
- [ ] Multi-seleção ligada: arrastar uma foto marcada leva todas as marcadas; arrastar uma não marcada leva só ela e não altera a seleção.
- [ ] Duplo clique abre a revisão nos dois modos; clique no coração e na caixa continuam funcionando.

## Arrastar fotos: coleções e pastas (regra: arrastar = mover; Ctrl ou Shift = copiar)
- [ ] Vendo a coleção A, arrastar 2 fotos para a coleção B: saem de A e entram em B. Com Ctrl (ou Shift) pressionado: entram em B e continuam em A. O texto flutuante muda entre "Mover" e "Copiar".
- [ ] Vendo "Todas as fotos" (sem coleção ativa), arrastar fotos que estão numa coleção em comum para outra coleção: aparece o menu (adicionar / mover de «X»). Fotos sem coleção: só adiciona.
- [ ] Coleção com subcoleções: o primeiro item é "Todas" (itálico); clicar mostra a coleção e as subcoleções; sem menu de contexto; não aceita soltar fotos.
- [ ] Arrastar fotos para uma PASTA da árvore "Pastas": o arquivo é movido de verdade (conferir no Explorer). Com Ctrl/Shift: copia. Soltar na pasta onde a foto já está: bloqueado com mensagem.
- [ ] Selecionar 2+ fotos com a mesma(s) coleção(ões): no painel Organização aparecem os chips das coleções em comum com ×; "Remover todas" tira todas. Com coleções diferentes entre as fotos o bloco não aparece.
- [ ] Selecionar uma coleção na árvore e apertar F2 / Delete: abre renomear / excluir.

## Vídeos e sidecar .xmp
- [ ] Importar uma pasta com MP4/MOV: aparecem na grade com miniatura e "▶ m:ss"; lista **Vídeos** na barra lateral.
- [ ] Selecionar um vídeo e abrir a revisão: o player aparece pausado no primeiro quadro; clique ou Espaço reproduz/pausa; barra de posição, volume e mudo funcionam; setas ←/→ trocam de mídia e o vídeo anterior para.
- [ ] Depois de ver um vídeo, mover/renomear o arquivo pela Biblioteca: não pode dar erro de "arquivo em uso".
- [ ] Editor de metadados: selecionar 2+ itens (PNG, CR2, vídeo, JPEG misturados), preencher um campo → "Aplicar às marcadas" habilita → "Salvar". Conferir no Explorer: aparecem `nome.xmp` ao lado de PNG/CR2/vídeo; o JPEG é gravado dentro.
- [ ] Mover/renomear/copiar um PNG com metadados: o `.xmp` vai junto.
- [ ] Arrastar vídeos para coleções e pastas funciona como para fotos.
- [ ] Vídeo em formato sem codec (ex.: MKV/HEVC): mensagem de erro no player, sem travar; miniatura pode faltar.

## Preview de vídeo, atualizar pasta, menus e orientação
- [ ] Selecionar um vídeo (seleção única): o painel da direita mostra o player (pausado no 1º quadro); clique/Espaço toca; "Revisar" continua funcionando. Mover/excluir esse vídeo pela Biblioteca não pode dar "arquivo em uso".
- [ ] Botão ⟳ ao lado de "PASTAS": copiar um arquivo novo para uma pasta importada e apagar outro; clicar ⟳ com a pasta selecionada → o novo aparece e o apagado fica como "Arquivo ausente".
- [ ] Botão direito numa pasta: Atualizar / Exibir no Explorer (abre a pasta).
- [ ] Botão direito numa miniatura: Exibir no Explorer (arquivo selecionado) e Excluir (Lixeira) com confirmação; com 3 fotos marcadas, botão direito numa delas → "3 arquivo(s)". Tecla Delete idem.
- [ ] Filtro **Orientação** (Paisagem/Retrato/Quadrada) e a linha "Orientação" em Informações.

## Expandir/recolher, inacessíveis, remover ausentes, filtros, retrato
- [ ] Botão direito numa pasta com subpastas: **Expandir tudo** abre todas as subpastas; **Recolher tudo** fecha. Mesmo nas coleções.
- [ ] Renomear temporariamente uma pasta raiz no Explorer e clicar ⟳: a pasta ganha ⚠ e o conteúdo some da árvore; ao voltar o nome e clicar ⟳, tudo reaparece.
- [ ] ⟳ com arquivos/pastas apagados: abre "Itens não encontrados"; **Remover** num item o tira do catálogo; **Remover todos** pede confirmação. Conferir que nenhum arquivo foi afetado.
- [ ] Botão direito num item "Arquivo ausente": **Remover do catálogo…** (e não "Excluir").
- [ ] Definir Orientação = Retrato (e/ou avaliação, tag) e trocar de pasta: os filtros continuam. "Todas as fotos" e "Limpar filtros" limpam.
- [ ] Vídeos de celular em retrato: depois de abrir o app (correção em segundo plano), aparecem como retrato (miniatura inteira, "Retrato" nas Informações e no filtro).

## EXIF, giro, drone e Explorer
- [ ] JPEG de celular em retrato (orientação EXIF): miniatura, preview e revisão aparecem em pé; filtro **Retrato** o inclui; as dimensões mostram largura < altura.
- [ ] Abrir o app com o catálogo antigo: em alguns segundos fotos/vídeos em retrato mudam de forma (correção em segundo plano) e as miniaturas deitadas são refeitas.
- [ ] Vídeos do drone em `docs/videos` (0024, 0030–0033): aparecem em retrato; se estiverem de cabeça para baixo, selecione todos → botão direito → **Girar à direita** duas vezes. O player (painel e revisão) acompanha o giro.
- [ ] Botão direito numa miniatura → **Girar à esquerda/direita**: miniatura, preview e player giram; o arquivo e a data de modificação não mudam; o giro sobrevive a fechar/abrir o app.
- [ ] Arrastar arquivos do Explorer para a área da biblioteca: aparece o aviso azul e eles entram no catálogo sem sair do lugar. Arrastar uma pasta inteira também.
- [ ] Arrastar arquivos do Explorer para uma pasta da árvore: com Ctrl copia, com Shift move, sem tecla pergunta. Arquivo com o mesmo nome no destino é mantido.
- [ ] Arrastar fotos da grade para uma pasta aberta no Explorer: os arquivos (e `.xmp`) são copiados; os originais continuam.

## Filtros avançados, cores, localização e visualizador
- [ ] Biblioteca: só aparecem os filtros principais; "Filtros avançados" abre/fecha o painel e mostra o número de filtros ativos.
- [ ] Tipo = Vídeos mostra só vídeos; Extensão lista só o que existe no catálogo; Data de/até inclui os dois dias; "Limpar filtros" zera tudo; trocar de pasta mantém os avançados.
- [ ] Botão direito → Cor → Vermelho numa seleção de vários itens: todos ganham a faixa vermelha. Teclas 1–6 colorem a seleção e 0 remove. Filtro por cor e "Sem cor" funcionam. A cor sobrevive a fechar/abrir o app.
- [ ] Revisão: botões de girar atuam em foto e em vídeo; bolinhas de cor aplicam à foto/seleção atual.
- [ ] Informações de uma foto com GPS (de celular/drone): "Obter localização" → pergunta de autorização na 1ª vez → mostra coordenadas e nome do lugar; "Ver no mapa" abre o OpenStreetMap. Recusar a autorização lê o GPS mas não consulta a rede.
- [ ] Vídeo do drone (`docs/videos`): "Obter localização" lê o GPS do `©xyz`. Selecionar vários e usar o menu de contexto mostra o andamento no rodapé.
- [ ] Buscar parte do nome do lugar (ex.: "Cabo") encontra as fotos.
- [ ] Metadados: miniaturas maiores, vídeos com selo de play; duplo clique na miniatura (ou "Ampliar") abre a visualização grande: foto com zoom, vídeo tocando; Esc fecha. Depois de fechar é possível mover/excluir o vídeo.

## Transferência (etapa 2)
- [ ] A aba **Transferência** aparece entre Microstock e Ferramentas; o painel esquerdo abre em Imagens e o direito pede para escolher uma pasta.
- [ ] Arrastar o divisor central muda a proporção; nenhum painel fica menor que o mínimo.
- [ ] "Selecionar pasta…" abre qualquer pasta (disco externo, rede); o breadcrumb leva a pastas superiores; ✎ permite digitar `D:\Fotos` e Enter navega (Esc cancela).
- [ ] Voltar/Avançar/Subir/Atualizar funcionam por painel; mudar um lado não altera o outro (pasta, busca, ordem, modo).
- [ ] Busca "viagem mp4" mostra só itens com os dois termos; ordenar por Nome/Data/Tamanho/Tipo e inverter; "Pastas primeiro" mantém pastas no topo; IMG2 vem antes de IMG10.
- [ ] Alternar Miniaturas/Lista por painel; slider muda o tamanho; clique no cabeçalho da lista ordena.
- [ ] Miniaturas de fotos e vídeos aparecem sozinhas; outros arquivos ficam com ícone. Duplo clique em pasta entra; em foto/vídeo abre o visualizador.
- [ ] Desconectar um disco/pasta: o painel mostra a mensagem e "Atualizar" recupera quando voltar.
- [ ] Selecionar vários (Ctrl/Shift/Ctrl+A): rodapé mostra "N selecionado(s) · tamanho".
