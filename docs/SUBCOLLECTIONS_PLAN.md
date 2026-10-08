# Plano futuro — Subcoleções

Esta etapa não implementa subcoleções. O modelo atual trata coleções como nomes em `Photo.Collections`, persistidos por `Collections` e `PhotoCollections`; não existe entidade `Collection` exposta à UI nem relação pai/filha.

## Modelo proposto

Adicionar uma entidade persistida:

```text
Collections
  Id INTEGER PRIMARY KEY
  Name TEXT NOT NULL
  ParentCollectionId INTEGER NULL REFERENCES Collections(Id)
  SortOrder INTEGER NOT NULL DEFAULT 0
  UNIQUE(ParentCollectionId, Name)
```

As fotos continuariam relacionadas por `PhotoCollections(PhotoId, CollectionId)`. O nome deixaria de ser armazenado diretamente em `Photo.Collections`; a ViewModel poderia expor nomes derivados para compatibilidade temporária.

## Regras

- `ParentCollectionId = NULL` representa uma coleção raiz.
- Uma coleção pai pode mostrar somente suas fotos diretas ou incluir descendentes; a decisão deve ser uma preferência explícita do filtro, não uma ambiguidade da UI.
- Renomear preserva o `Id` e todas as relações de fotos.
- Excluir uma coleção deve oferecer remover apenas a relação, excluir a subárvore ou cancelar; nunca apagar fotos físicas.
- Mover uma coleção exige bloquear ciclos: uma coleção não pode ser movida para si mesma nem para qualquer descendente.
- A contagem da árvore deve distinguir fotos diretas de fotos agregadas dos descendentes.
- Drag-and-drop de fotos para coleção e de coleções para coleções será uma etapa própria, com confirmação para movimentos em massa.

## Migration proposta

1. Criar uma tabela `CollectionsV2` com `ParentCollectionId`, `SortOrder` e nomes normalizados.
2. Migrar cada nome atualmente encontrado em `Collections` para uma coleção raiz única.
3. Recriar `PhotoCollections` apontando para os novos IDs, preservando todas as relações.
4. Validar contagens e nomes antes de substituir a tabela antiga; manter backup/migração reversível.

Não aplicar esta migration agora: a implementação de subcoleções exige primeiro definir a semântica de filtros pai/filha e a UX da árvore.

## UX futura

`TreeView` de coleções com criar, renomear, mover, expandir/recolher, contagens direta/agregada, drop de fotos e prevenção visual de ciclos. A coleção pai poderá ser selecionada como filtro agregado ou somente como filtro direto, conforme a preferência escolhida na próxima etapa.
