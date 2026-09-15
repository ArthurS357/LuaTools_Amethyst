## v1.7.4

- Reempacota a 1.7.3, cujo pacote continha o binário 1.7.2 (build lock durante empacotamento). **Correção (2026-09-14):** o `LuaTools.exe` publicado nesta release se identifica como `1.7.3.1`, não `1.7.4` — o `<Version>` do projeto não foi atualizado junto. O código é o mesmo da 1.7.3.
- Adiciona aviso visual nas abas Add e Builds sobre a necessidade de usar HubCap/Ryuu e desativar "Auto Update Apps (Don't Lock Manifests)" após a mudança da Steam.
- Porta os três fixes upstream v1.3.1: migração de pasta de manifest, filtro "My games" na aba Fixes, e revert limpo de fixes Denuvo.

> A **v1.7.5** corrige a versão reportada pelo binário e remove o aviso acima, que ficou desatualizado.
