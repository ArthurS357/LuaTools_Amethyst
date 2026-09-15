## Mudanças

- **O aviso sobre fontes de manifests nas abas Add e Builds foi removido.** Ele orientava usar Sadie
  (Hubcap) ou Ryuu e desligar "Auto Update Apps (Don't Lock Manifests)", e ficou desatualizado: o modo
  BetterSteamTools do upstream resolveu o problema na versão mais recente.
- Nenhum aviso novo foi adicionado no lugar, e o app não faz nenhuma requisição nova.

## Correções

- **Build limpo voltou a compilar.** O arquivo de projeto tinha sido salvo em Windows-1252 na 1.7.4, e o
  build parava com "Invalid character in the given encoding". Ele voltou a ser UTF-8.
- **Versão correta no rodapé e na aba About.** O executável da 1.7.4 se identificava como `1.7.3.1`.
- O CI agora verifica o encoding de todos os arquivos de texto antes de compilar.

## Testes

- 6 testes a menos (os do aviso removido). Total: 1659, todos passando.
- 27 testes novos para a verificação de encoding (UTF-16/32, BOM duplo ou fora do lugar, controles,
  texto duplamente codificado).

## Segurança

- Nenhuma telemetria, auto-update, elevação UAC ou envio de chaves reintroduzido.
- Formato de `settings.json` inalterado. Nenhuma dependência NuGet nova.
