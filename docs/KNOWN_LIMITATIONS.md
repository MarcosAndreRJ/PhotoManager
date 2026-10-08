# Limitações conhecidas

## Interface

- Preview sem zoom, "100 %", ajustar e tela cheia; sem filmstrip; sem alternância grade/lista nem controle de tamanho dos cards (adiados — ver `UI_REFACTOR_PLAN.md`, seção G).
- Tags e categorias continuam sendo editadas como texto separado por vírgula no painel (chips completos nas fases seguintes); coleções já foram modernizadas para chips visuais e seletores estruturados na Etapa C2.
- A avaliação e o favorito feitos no painel só são gravados ao clicar em **Salvar organização**; o coração no card grava imediatamente (e grava junto qualquer edição pendente da mesma foto).
- Janela usa a barra de título padrão do Windows e não tem ícone próprio.
- Os ícones usam *Segoe Fluent Icons* (Windows 11) com fallback para *Segoe MDL2 Assets* (Windows 10); em sistemas sem nenhuma das duas, os glifos não aparecem.
- Em capturas de tela do ambiente de desenvolvimento foi observado um artefato de pintura intermitente em rótulos da sidebar; o texto está correto na árvore de UI Automation (provável limitação do renderizador do ambiente). Confirmar em hardware real.
- A exclusão usa a Lixeira do Windows; a smart list "Duplicadas" mostra somente grupos já identificados pelo detector.

## Catálogo

- Formatos: `.jpg`, `.jpeg`, `.png`, `.webp`. Dimensões/thumbnail dependem dos codecs WPF; WEBP pode ser catalogado sem gerar preview.
- Não há relink automático de arquivo movido fora do aplicativo (aparece como "Arquivo ausente").
- Filtros operam em memória sobre o catálogo carregado; migrar para consultas SQLite se o acervo passar de dezenas de milhares de fotos.
- A tabela `Categories` nunca é populada: a categoria é texto em `Photos.CategoryName` (a UI deriva a lista das fotos).
- A barra lateral limita Pastas a 40 e Tags a 40 entradas (as mais frequentes).
- "Recentes" = importadas nos últimos 30 dias.

## Operações de arquivo

- Operações em lote interrompem no primeiro erro e informam o problema (arquivos já processados permanecem processados e o catálogo é recarregado).
- Renomeação em lote sem pré-visualização nem rollback; o template usa a sequência relativa à seleção.
- A Lixeira não tem restauração pelo PhotoManager.
- Mover/renomear tentam novamente por ~1,5 s se o arquivo estiver em uso; depois disso falham com a mensagem do sistema.

## Ambiente / testes

- Os testes automatizados usam pastas temporárias e serviços reais; a Lixeira real do Windows é usada no teste de exclusão (arquivo de teste vai para a Lixeira).
- Diálogos de pasta e a caixa de confirmação da Lixeira não têm teste automatizado (checklist manual).
- Segunda instância encerra silenciosamente (não traz a primeira janela para frente).
- Não há tela para editar configurações (`settings.json` é lido/gravado, sem UI).
- Metadados: leitura (fase 5) e edição de JPEG (fase 6, seção abaixo). Leitura por MetadataExtractor; títulos/palavras-chave XMP em idiomas diferentes de x-default usam o primeiro valor. GPS/lente/abertura/exposição sem teste automático (ver checklist).
- A tela de Metadados não tem zoom, filmstrip nem tela cheia (adiados).

## Fase 6 — edição de metadados

- **Só JPEG** é gravável. PNG e WEBP aparecem somente leitura (com explicação na tela).
- **RAW:** ver a seção "Fase 7 / RAW / árvore de pastas" abaixo (CR2 é indexado em modo somente leitura; sidecar `.xmp` segue pendente).
- O EXIF nunca é alterado. Apagar na tela um campo que também existe no EXIF (Artist, Copyright, ImageDescription, XP*) faz o valor voltar na próxima leitura.
- IPTC com campos de tamanho estendido, IPTC/XMP corrompido ou XMP não interpretável: a gravação é recusada (nada é alterado) com mensagem.
- XMP e IPTC precisam caber em 64 KB cada (segmento JPEG); textos gigantes são recusados. ExtendedXMP não é mesclado.
- IPTC clássico tem limites por campo (título 64, autor 32…); o PhotoManager grava o texto completo, como a maioria das ferramentas. Os limites da tela (200/2000/50) são referência, não bloqueio.
- O histórico guarda valores, não cópias do arquivo; "Reverter" descarta alterações **não salvas**. Restaurar uma versão antiga ainda não existe (os valores já estão guardados).
- Backup de arquivo: apenas temporário durante a substituição (removido após o sucesso).
- Copiar/Colar metadados vale para a sessão.
- Botões "Sugerir keywords"/"Gerar com IA" das referências não existem (IA é a fase 16).

## Fase 7 / RAW / árvore de pastas

- **RAW (CR2) é somente leitura:** indexado, com miniatura/preview do JPEG embutido e metadados lidos; **não** grava IPTC/XMP nem sidecar `.xmp` (decisão pendente). Apenas `.cr2` está habilitado; outros RAW exigem validação com arquivos reais. Se o RAW não tiver JPEG embutido utilizável, a foto é catalogada sem miniatura.
- A orientação do RAW (retrato/paisagem) é aplicada na miniatura e no preview; fotos JPEG comuns continuam sem aplicar a orientação EXIF (limitação anterior).
- **Árvore de pastas:** só pastas que contêm fotos aparecem (não há pastas vazias); o rótulo das raízes é abreviado (`…\pai\pasta`) e o caminho completo está na dica. Não há "remover pasta do catálogo" nem "reimportar pasta" pelo menu da árvore. A seleção de pasta não é lembrada entre sessões.
- **Lote:** só JPEG é gravado (PNG/WEBP/RAW/ausentes são ignorados com o motivo). "Acrescentar" na descrição junta com um espaço. "Remover" palavras-chave compara sem diferenciar maiúsculas. Cancelar não desfaz fotos já gravadas (cada foto tem sua versão no histórico, mas não há "restaurar versão" ainda). Valores de campos mantidos vêm do arquivo (com o fallback EXIF descrito na Fase 6).
- Presets guardam o plano inteiro (operações + valores); não há importar/exportar presets nem presets por banco de imagens (fase 8+).
- A janela de lote é modal e não permite fechar durante a gravação.

## Fase 8/9 — workflow microstock local e histórico manual

- A Central calcula preparação localmente; não há login, rede, upload, fila ou integração com bancos.
- A Fase 9 registra ações manuais, mas não envia arquivos: não há login, rede, upload, fila, retry automático ou integração com bancos.
- O histórico é append-only e o estado atual usa a última marcação por foto/agência; não há exclusão física de registros.
- Agências podem ser adicionadas, renomeadas, desativadas e reordenadas, mas não são apagadas para preservar referências do histórico.
- A grade mantém colunas visuais para as cinco agências seed; bancos personalizados aparecem no resumo de envios e nos filtros, não como coluna dinâmica.
- O perfil genérico é configurável, mas a edição de regras na tela cobre os campos principais; extensões permitidas e alguns limites avançados ficam disponíveis no modelo persistido para a próxima evolução da UI.
- O cache de metadata é apenas de sessão. A invalidação usa versão de metadata, tamanho e data de modificação; fechar o app força nova leitura.
- A leitura em lote é limitada a quatro arquivos simultâneos e pode ser cancelada. A tela foi testada por unidade com 2.000 fotos sintéticas; rolagem visual com milhares de fotos reais continua pendente.
- Não há ainda edição de metadata dentro da Central: o botão navega para a aba Metadados, preservando a mesma seleção da Biblioteca.
- A seleção/confirmação e a tela completa da Fase 9 ainda precisam de conferência manual em uma sessão com superfície WPF nativa disponível.

## Fase 10 — duplicatas exatas

- A smart list **Duplicadas** depende de uma busca SHA-256 já executada em Ferramentas; abrir a Biblioteca não inicia hashing automaticamente.
- A grade da Ferramentas mostra os grupos e miniaturas, mas não oferece restauração da Lixeira nem desfazer de mover/excluir.
- Ignorar é persistido por hash: duas cópias com o mesmo conteúdo compartilham a decisão de ignorar, por desenho.
- O detector não compara imagens visualmente semelhantes, versões redimensionadas ou arquivos com conteúdo diferente; são duplicatas exatas por SHA-256.
- A conferência manual de milhares de fotos, seleção da cópia mantida e confirmação da Lixeira ainda precisa ser feita em uma sessão WPF nativa disponível.

## Editor de metadados (Etapa 4)

- Rascunhos ficam só na memória: fechar o aplicativo com alterações não salvas as descarta, sem aviso.
- A janela modal de lote foi removida; todo lote passa pelo editor (rascunho → Salvar). `BatchMetadataService` não é mais usado pela interface.
- Em lote, "Substituir" exige valor (não há "limpar título/autor/copyright" em lote); palavras-chave têm "Limpar".
- Salvar é sequencial (uma foto por vez) e cancelar não desfaz as já gravadas.
- Não há "restaurar versão" a partir do histórico; "Reverter" só descarta rascunhos.
- Sem edição de metadados para PNG/WEBP/RAW (linhas desabilitadas).

## Nova etapa de organização — Coleções e Subcoleções (Etapas A a E — Concluídas)

- **Contador do nó pai ≠ soma dos filhos:** Por decisão explícita de arquitetura (D11), o badge numérico visível em cada nó exibe apenas fotos vinculadas diretamente a ele (`DirectCount`). Isso evita contagens infladas ou falsas somas de conjuntos com interseção (já que uma foto pode estar em múltiplas subcoleções simultaneamente). A contagem consolidada da subárvore está acessível no tooltip do nó (`SubtreeCount`).
- **Persistência de Expansão:** O estado de expansão/recolhimento dos nós da árvore (`IsExpanded`) é gerenciado de forma reativa e preservado durante as operações de criação, renomeação, movimentação e arraste da sessão atual, mas não é persistido no banco SQLite entre diferentes sessões do aplicativo (ao reabrir o app, a árvore inicializa recolhida com raízes visíveis).
- **Sem Reordenação Manual Arbitrária entre Irmãs:** A exibição dos nós da árvore obedece à ordenação alfabética case-insensitive (`COLLATE NOCASE`). A coluna `SortOrder INTEGER NOT NULL DEFAULT 0` já foi introduzida no schema SQLite e na entidade para viabilizar reordenação personalizada no futuro, mas o arraste atual atua como reparentamento (mover para novo pai ou raiz), não como posicionamento relativo entre nós irmãos.
- **Natureza 100% Virtual:** Mover coleções, criar subcoleções, reparentar via arraste ou excluir coleções (mesmo em modo de exclusão com subcoleções) nunca altera, move ou apaga arquivos físicos no disco nem remove fotos do catálogo `Photos`.
- **Criação/renomeação/exclusão de pastas do sistema de arquivos** e operações diretas com o Windows Explorer permanecem fora do escopo de coleções virtuais.

## Vídeos e sidecar .xmp
- Metadados de RAW/PNG/WebP/vídeo ficam em `nome.xmp` ao lado do arquivo: agências de microstock e outros programas que só leem metadados **embutidos** não os enxergam. JPEG continua embutido. O perfil Microstock que exige "formato editável (JPEG)" continua refletindo isso.
- RAW + PNG com o mesmo nome base na mesma pasta compartilhariam o mesmo `.xmp` (convenção Adobe). JPEG não usa sidecar, então o caso comum RAW+JPEG não conflita.
- Duração/dimensões só são lidas para MP4/MOV/M4V; outros contêineres de vídeo são catalogados sem esses dados. Miniatura e reprodução dependem dos codecs do Windows.
- Não há edição/corte de vídeo, legendas, nem leitura de dados de câmera de vídeos (o GPS do átomo `©xyz` é lido; ver "Cores, filtros avançados e localização").
- A validação Microstock trata vídeo como foto (megapixels do quadro, palavras-chave, etc.); regras específicas de vídeo não existem.

## Orientação, ausentes e pastas inacessíveis
- (Corrigido em 05/10/2026) A orientação EXIF de JPEG agora é aplicada às miniaturas, ao preview, à tela cheia e às dimensões. Outros formatos com EXIF (PNG/WebP/TIFF) ainda não têm a orientação lida; RAW CR2 já era tratado.
- Vídeos de **drone (DJI)** em modo vertical gravam o quadro deitado com uma matriz de rotação de 90° no MP4. O app segue o padrão do arquivo (retrato), mas o conteúdo desses quadros tem o céu à direita, então a imagem pode aparecer de cabeça para baixo; use **Girar** (botão direito) — vale para a seleção inteira e é guardado no catálogo sem alterar o arquivo. Não foi possível confirmar com um player de referência qual seria a exibição "correta" desses arquivos.
- O Windows não aplica a rotação do contêiner nas miniaturas desses vídeos; o app gira o quadro quando o arquivo manda exibir em retrato e o quadro chegou deitado. Rotação de 180° do contêiner não é aplicada às miniaturas (não dá para saber se o Windows já aplicou).
- DNG (RAW do drone) e outros RAW além de CR2 não são catalogados.
- "Remover do catálogo" apaga também tags, coleções, notas, histórico de metadados e registros de upload do item; não há desfazer (um backup do catálogo, previsto no roadmap, protegeria isso).
- A verificação de pasta inacessível usa `Directory.Exists` com *timeout* de 4 s: uma pasta de rede muito lenta pode ser marcada como inacessível por engano até a próxima verificação.

## Cores, filtros avançados e localização
- A cor é só do catálogo (não vai para o arquivo nem para o `.xmp`); se o item for removido do catálogo e reimportado, volta sem cor.
- O nome do lugar depende do serviço público do OpenStreetMap: precisa de internet, é limitado a 1 consulta por segundo (lotes grandes demoram) e pode falhar/ficar vazio em locais remotos ou se o serviço recusar. Falhas não são gravadas, então uma nova tentativa é possível.
- Só são lidas coordenadas gravadas no arquivo (EXIF GPS; `©xyz` em MP4/MOV). Vídeos sem esse átomo e formatos sem GPS aparecem como "Sem GPS no arquivo"; não há leitura de GPS do `.xmp` sidecar nem de arquivos de trilha (.srt/.gpx).
- A leitura do GPS é manual (botão/menu) e feita uma vez por arquivo; se o GPS for adicionado ao arquivo depois, o catálogo não relê sozinho.
- Ao autorizar o envio de coordenadas, vale para as próximas consultas; para revogar, apague a chave `geocoding.consent` do `settings.json` (ainda não há opção em Configurações).
- O visualizador ampliado abre vídeo no mesmo reprodutor do Windows (MediaElement), com as mesmas limitações de codec.

## Transferência (etapa 2)
- Miniaturas são geradas em segundo plano, uma por vez, em ordem de lista (não só as visíveis); em pastas com milhares de mídias levam tempo até completar, mas ficam em cache. Cartões de memória lentos podem demorar mais.
- Duração/dimensões de vídeo e foto ainda não aparecem na lista; só nome, tipo, tamanho e data.
- Pastas ocultas/de sistema não são listadas. Arquivos de tipos desconhecidos aparecem apenas com ícone genérico.
- O layout (pastas, modo, proporção dos painéis) ainda não é lembrado ao reabrir o app.
