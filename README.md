# PhotoManager

PhotoManager é uma aplicação desktop para Windows desenvolvida em **C# / .NET / WPF** para gerenciamento avançado de acervos de fotos e vídeos.

O projeto nasceu com a proposta de unir, em uma única aplicação:

- gerenciamento de biblioteca;
- organização por pastas, categorias, tags e coleções;
- edição de metadados;
- workflow para bancos de imagens / microstock;
- controle de envios;
- detecção de duplicatas;
- transferência de arquivos;
- preview e navegação visual.

A ideia é oferecer um fluxo mais organizado para quem trabalha com grandes volumes de fotos e vídeos e precisa saber rapidamente:

- onde cada arquivo está;
- como ele está categorizado;
- quais metadados foram preenchidos;
- quais arquivos já foram enviados para cada banco;
- quais ainda precisam ser preparados;
- quais são duplicados;
- quais arquivos já foram transferidos para outro disco ou pasta.

---

## Status

O projeto está em desenvolvimento ativo.

Atualmente já existem módulos para:

- biblioteca de fotos;
- organização do acervo;
- categorias;
- tags;
- coleções;
- favoritos;
- avaliações;
- notas;
- operações de arquivo;
- leitura e edição de metadados;
- edição em lote;
- workflow microstock;
- controle de agências;
- histórico de envio;
- detecção de duplicatas;
- marcações por cor;
- seleção múltipla.

Outros módulos estão sendo adicionados gradualmente.

---

## Principais funcionalidades

### Biblioteca

- Indexação de pastas
- Visualização em miniaturas
- Preview das imagens
- Busca
- Filtros
- Favoritos
- Avaliação por estrelas
- Marcação por cores
- Arquivos ausentes
- Navegação por pastas

---

### Organização

- Categorias
- Tags
- Coleções
- Subcoleções
- Notas pessoais
- Organização virtual sem mover o arquivo físico
- Seleção múltipla
- Operações em lote

---

### Operações de arquivo

- Mover
- Copiar
- Renomear
- Excluir
- Renomeação em lote
- Gerenciamento de pastas
- Drag-and-drop
- Integração com Windows Explorer

---

### Metadados

Leitura e edição de metadados como:

- Title
- Description
- Keywords
- Author
- Copyright
- EXIF
- IPTC
- XMP

Também há suporte para edição em lote.

Exemplo:

- adicionar keywords a várias imagens;
- remover keywords;
- substituir Title;
- aplicar Author e Copyright;
- utilizar presets.

---

### Workflow Microstock

O PhotoManager também foi projetado para ajudar no controle de imagens destinadas a bancos de imagens.

Estados como:

- Não preparada
- Metadados incompletos
- Pronta para envio
- Enviada parcialmente
- Enviada
- Com erro
- Alterada após envio

O objetivo é facilitar o acompanhamento de cada foto e evitar dúvidas como:

> “Essa imagem já foi enviada para o Shutterstock?”

ou:

> “Eu alterei os metadados depois do último envio?”

---

### Controle por agência

Cada foto pode possuir histórico individual por agência.

Exemplo:

```text
Adobe Stock
✓ Enviada

Shutterstock
✓ Enviada

Depositphotos
○ Não enviada

Dreamstime
⚠ Erro
```

O histórico pode armazenar:

- data do envio;
- versão dos metadados;
- status;
- nome remoto;
- erros;
- tentativas.

---

## Duplicatas

O projeto possui suporte para identificação de arquivos duplicados.

A estratégia pode considerar:

- tamanho;
- nome;
- hash SHA-256.

Recursos futuros poderão incluir detecção de imagens visualmente semelhantes.

---

## Transferência de arquivos

Está sendo desenvolvido um módulo de transferência com dois painéis, semelhante a um gerenciador de arquivos.

Objetivos:

- visualizar duas pastas lado a lado;
- comparar arquivos;
- identificar o que já foi transferido;
- copiar;
- mover;
- drag-and-drop;
- busca independente;
- ordenação;
- modo lista;
- modo miniaturas;
- marcações por cor;
- painéis redimensionáveis.

Exemplo conceitual:

```text
Origem                         Destino

D:\Cartão                      E:\Fotos

IMG001.JPG    ✓                IMG001.JPG
IMG002.JPG    →
IMG003.JPG    ✓                IMG003.JPG
IMG004.JPG    ⚠                IMG004.JPG
```

---

## Portabilidade da biblioteca

Uma preocupação importante do projeto é evitar que o catálogo dependa apenas de caminhos absolutos.

Exemplo:

```text
D:\Fotos\Viagens\IMG001.JPG
```

Um HD externo pode receber outra letra depois de uma formatação:

```text
F:\Fotos\Viagens\IMG001.JPG
```

Por isso, a arquitetura está sendo preparada para trabalhar com:

```text
StorageRoot
+
RelativePath
```

Exemplo:

```text
StorageRoot:
D:\Fotos

RelativePath:
Viagens\IMG001.JPG
```

Isso permitirá futuramente:

- trocar a letra da unidade;
- migrar para outro computador;
- relocalizar bibliotecas;
- restaurar backups;
- trabalhar com múltiplos discos;
- trabalhar com NAS.

---

## Backup do catálogo

O PhotoManager também deverá permitir backup do catálogo separadamente das fotos originais.

O catálogo poderá incluir:

- banco SQLite;
- categorias;
- tags;
- coleções;
- notas;
- avaliações;
- favoritos;
- metadados;
- histórico de envios;
- configurações;
- StorageRoots.

As fotos originais não precisam necessariamente estar dentro do backup do catálogo.

---

## Arquitetura

Estrutura geral planejada:

```text
PhotoManager.sln

src/
├── PhotoManager.Domain
├── PhotoManager.Application
├── PhotoManager.Infrastructure
├── PhotoManager.Persistence
└── PhotoManager.Wpf

tests/
└── PhotoManager.Tests
```

A aplicação segue uma separação entre:

- domínio;
- lógica de aplicação;
- infraestrutura;
- persistência;
- interface WPF.

---

## Tecnologias

- C#
- .NET
- WPF
- MVVM
- SQLite
- Dependency Injection
- async / await
- xUnit

Outras bibliotecas podem ser utilizadas para:

- leitura de metadados;
- geração de thumbnails;
- hashing;
- operações de mídia.

---

## Filosofia do projeto

O PhotoManager não pretende ser apenas um visualizador de imagens.

O objetivo é funcionar como um **gerenciador de acervo digital**, ajudando em todo o ciclo de organização:

```text
Importar
↓
Organizar
↓
Classificar
↓
Editar metadados
↓
Validar
↓
Preparar
↓
Enviar
↓
Acompanhar
↓
Arquivar
```

---

## Segurança

O projeto prioriza operações seguras.

Alguns princípios:

- evitar sobrescrever arquivos silenciosamente;
- preservar identificadores internos;
- não apagar arquivos automaticamente;
- tratar arquivos ausentes;
- manter histórico;
- preservar dados do catálogo;
- evitar corrupção de metadados;
- separar organização virtual da estrutura física.

---

## Roadmap

### Em desenvolvimento

- [x] Biblioteca
- [x] Categorias
- [x] Tags
- [x] Coleções
- [x] Avaliações
- [x] Favoritos
- [x] Operações de arquivo
- [x] Leitura de metadados
- [x] Edição de metadados
- [x] Edição em lote
- [x] Workflow Microstock
- [x] Controle de agências
- [x] Histórico de envio
- [x] Duplicatas
- [x] Marcação por cores
- [ ] Subcoleções avançadas
- [ ] Módulo de transferência
- [ ] StorageRoot / caminhos relativos
- [ ] Backup e restauração
- [ ] Upload automático FTP / FTPS / SFTP
- [ ] Fotos visualmente semelhantes
- [ ] Preview avançado de vídeos
- [ ] Inteligência artificial para geração de metadados
- [ ] Editor básico de vídeo

---

## Recursos futuros

Algumas ideias para fases posteriores:

### IA

- geração de Title;
- geração de Description;
- geração de Keywords;
- sugestão de categoria;
- análise visual;
- apoio ao workflow microstock.

### Vídeo

Está sendo estudado um módulo básico de pré-edição com:

- timeline;
- corte;
- junção de clipes;
- áudio;
- alteração de velocidade;
- detecção de silêncio;
- legendas automáticas.

O objetivo não é substituir editores profissionais, mas agilizar a preparação inicial de vídeos.

---

## Interface

A aplicação utiliza uma interface WPF moderna com foco em:

- thumbnails;
- navegação rápida;
- seleção múltipla;
- drag-and-drop;
- painéis redimensionáveis;
- preview;
- edição em lote;
- marcações visuais.

---

## Desenvolvimento

Para restaurar as dependências:

```bash
dotnet restore
```

Para compilar:

```bash
dotnet build
```

Para executar os testes:

```bash
dotnet test
```

> Dependendo da configuração do projeto WPF, é necessário utilizar Windows e uma versão compatível do .NET SDK.

---

## Contribuições

O projeto ainda está em desenvolvimento e sua arquitetura pode sofrer alterações.

Issues e sugestões são bem-vindas.

Antes de enviar alterações maiores, é recomendado abrir uma issue descrevendo a proposta.

---

## Licença

A licença do projeto ainda deve ser definida.

Se o repositório for público, recomenda-se adicionar um arquivo `LICENSE` antes de aceitar contribuições externas.

---

## Aviso

PhotoManager é um projeto independente.

Referências a outros softwares, serviços ou bancos de imagens são utilizadas apenas para explicar fluxos e compatibilidades conceituais.

Não há afiliação oficial com Adobe, Shutterstock, Xpiks ou outras plataformas mencionadas.
```