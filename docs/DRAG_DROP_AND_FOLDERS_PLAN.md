# Plano — Drag-and-drop e gerenciamento de pastas

## Auditoria inicial

- Pastas não são entidades persistidas: são derivadas de `Photo.CurrentPath` por `FolderNode.Build` e reconstruídas em `LibraryViewModel.ReloadAsync`.
- A seleção múltipla existe na `ListBox` da Biblioteca com `SelectionMode="Extended"`; o code-behind lê `PhotoList.SelectedItems` e mantém `LibraryViewModel.SelectionCount`/`SelectedCards`.
- Mover, copiar, renomear e enviar para a Lixeira já passam por `IFileOperationService`/`FileOperationService`. `MoveAsync` atualiza o mesmo objeto `Photo` e chama `ICatalogRepository.UpdateLocationAsync`, preservando `PhotoId`; `CopyAsync` só cria outra identidade quando `addCopyToCatalog=true`.
- Colisões atualmente falham com `IOException` e não sobrescrevem. Ainda não existe política de diálogo para substituir/manter/ignorar/cancelar.
- Não existe drag-and-drop WPF para fotos, pastas ou Explorer, nem CRUD físico de pastas.
- Coleções são listas de nomes em `Photo.Collections`, relacionadas pelas tabelas `Collections`/`PhotoCollections`; não há entidade de pasta ou coleção hierárquica.
- A janela não tinha `Icon` nem `ApplicationIcon` configurado; o cabeçalho usava apenas um glifo.

## Ordem de implementação

1. **ETAPA 2 (esta rodada):** filtro virtual `Sem coleção` e ícone próprio da aplicação.
2. **ETAPA 3:** serviço de pastas (criar, renomear, abrir, excluir seguro, remover somente do catálogo), com atualização transacional/reconciliação de caminhos.
3. **ETAPA 4:** drag interno de fotos para `FolderNode`, seleção múltipla, MOVE padrão, COPY com Ctrl, feedback e resolução de colisões.
4. **ETAPA 5:** drop do Explorer para Biblioteca/pasta, com diálogo explícito para copiar/mover/adicionar catálogo.
5. **ETAPA 6:** drag-out COPY para Explorer somente se o OLE/WPF puder ser encapsulado sem fragilizar a aplicação.

## Riscos e decisões

- Como pastas são derivadas do caminho, renomear uma pasta exige atualizar todos os `Photo.CurrentPath` descendentes e manter o mesmo `PhotoId`; o serviço deve fazer operação física, atualizar o banco e tentar rollback físico quando a persistência falhar.
- Remover somente do catálogo exige decidir se fotos continuam como itens ausentes ou são excluídas. A arquitetura atual não possui operação de remoção lógica; a ETAPA 3 deve escolher e documentar uma política antes de expor o comando.
- `FileOperationService` não deve ser contornado pela View. A futura API de lote precisa receber `CancellationToken`, progresso e uma política de colisão explícita.
- Drop externo sobre uma pasta não pode mover silenciosamente: o fluxo recomendado é diálogo Copiar/Mover/Cancelar, especialmente fora das raízes já importadas.
- Watch folders, relink automático, subcoleções e drag de coleções ficam fora desta etapa.
