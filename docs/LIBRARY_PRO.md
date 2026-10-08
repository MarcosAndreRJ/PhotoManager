# Biblioteca "pró" — recursos, atalhos e limitações

Status: implementado (todos os itens sugeridos, exceto a importação automática de cartão/celular, que virá de outro software).

## Interface
- **Grade justificada** (`Controls/VirtualizingJustifiedPanel`): cada item na proporção real, virtualizada por linha, cabeçalhos por dia desenhados pelo painel. Alterna com a grade uniforme (botão na barra ou Configurações). Tamanho pelo controle deslizante.
- **Título humano**: "27 set · 05:59" (o nome do arquivo fica na dica; Configurações › Mostrar o nome do arquivo).
- **Barra contextual da seleção**: com itens selecionados a barra superior vira ações (estrelas, cor, P/X/U, uso, comparar, exportar, cortes, mover, Lixeira).
- **Busca com sintaxe + fichas**: `tipo:video duração:<60 orientação:vertical cor:verde nota:>=3 -uso:usado tag:"pôr do sol"` (`Application/Library/LibraryQuery`). Sugestões ao digitar; cada filtro ativo vira ficha com ✕.
- **Prévia de vídeo ao passar o mouse** (`Controls/HoverVideoPreview`): toca sem som; mover na horizontal percorre o vídeo.
- **Painel direito**: some/aparece; sem seleção mostra o resumo da visão (itens, espaço, horas de vídeo, triagem, duplicadas).
- **Status** some sozinho após 10 s; contagens iguais ao total ficam ocultas.
- **Linha do tempo** (ordenação por data, ≥ 30 itens): anos/meses à direita da grade.
- **Paleta de comandos** (Ctrl+K): ações, listas, coleções, pastas e áreas.
- **Tema escuro** (Configurações › Aparência): troca em tempo real (`ThemeService`, `Themes/ColorsDark.xaml`).

## Triagem e organização
- **P** escolhe, **X** rejeita, **U** tira a bandeira (grade e revisão; avança sozinho se ligado). Ctrl+0…5 estrelas.
- **Comparar** 2–4 itens lado a lado ("Ficar com este" escolhe e rejeita os outros).
- Listas: Escolhidas, Rejeitadas (com "Enviar para a Lixeira"), Shorts/Reels (vertical 9:16 até 3 min), Vídeos não usados, Desfocadas, Com cortes marcados.
- **Coleções inteligentes**: salve a busca atual (botão + na barra lateral); atualiza sozinha.
- **Pilhas**: RAW+JPEG e rajadas (≤ 1 s) mostram só a melhor; clique no selo para expandir.
- **Uso**: Usado em… / Publicado / Sem uso, com selo no cartão.

## Vídeo
- **Cortes**: aba "Cortes" (ou I/O/M na revisão) marca entrada/saída na posição do player; **Exportar cortes** gera FCPXML (DaVinci/Final Cut) ou EDL (Premiere e outros).

## Ferramentas
- **Parecidas e desfocadas**: hash perceptual + nitidez calculados em segundo plano a partir das miniaturas; sugere a melhor de cada grupo (vira escolhida/rejeitada — nada é apagado).
- **Armazenamento**: espaço por ano, pasta e tipo; vídeos ≥ 2 GB; quanto dá para liberar.
- **Backup**: pares origem → backup; conferência por caminho relativo + tamanho; "Copiar o que falta".

## Exportar
Predefinições (lado maior, JPG/PNG/original, qualidade, marca d'água de texto, modelo de nome `{nome} {data} {seq}`…). Vídeos são copiados sem conversão. Nunca sobrescreve.

## Mapa
Tiles do OpenStreetMap com cache (só depois de permitir). Shift+arrastar filtra uma área; clique numa bolha aproxima/filtra.

## IA local (infraestrutura pronta, motores pendentes)
`Application/Ai`: catálogo de modelos, download sob demanda com hash, indexação incremental, busca por significado (`ia:"…"`), pessoas (agrupamento de rostos, renomear na barra lateral), etiquetas automáticas (`etiqueta:`), transcrição (`fala:`). **Nenhum motor (ONNX Runtime / Whisper) está instalado**: os recursos aparecem como indisponíveis e o botão "Baixar" fica bloqueado até um motor implementar `IImageTextEmbedder`, `IFaceAnalyzer` ou `ITranscriber` e ser registrado no `AiEngineRegistry` (App.xaml.cs).

## Limitações conhecidas
- A barra de rolagem e os menus de contexto usam o estilo do Windows (claros também no tema escuro).
- Com muitos meses, a linha do tempo mostra só os anos.
- Análise de nitidez vale para fotos; em vídeos só o hash (quadro da miniatura).
- Shift+arrastar para o Explorer move pelo próprio Explorer (o catálogo não acompanha esse caso).
