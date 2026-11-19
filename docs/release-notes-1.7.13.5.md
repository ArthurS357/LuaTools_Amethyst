Suporte à engine AmethystTool v1.5.1: botão de socorro para o cache de conteúdo da Steam, as ferramentas de linha de
comando da engine guardadas no disco e downloads de fix mais seguros.

## Novidades

- **Limpar Cache de Conteúdo (AppInfo).** Em Configurações > Manutenção. Use se um jogo instalado estiver dando erro de
  "Configuração de Conteúdo Inválida". O app pede confirmação, fecha a Steam, move `appcache\appinfo.vdf` para a pasta
  `appcache\luatools-backup` e reabre a Steam, que reconstrói o arquivo. Se a Steam não fechar, nada é alterado. A
  primeira abertura depois disso demora mais.
- **Ferramentas da engine no disco.** Instalar ou atualizar o AmethystTool v1.5.1 agora guarda `version_pin.exe` e
  `pattern_migrate.exe` (pasta `tools` do release) em `%AppData%\LuaToolsGui\amethysttool\tools`. Elas nunca vão para a
  pasta da Steam e ainda não têm botão no app. Releases antigos, sem essa pasta, instalam normalmente.
- **Novas opções da engine respeitadas.** `[manifest] failover` (failover de provedores, ligado por padrão) e
  `[shadow] record` (desligado por padrão) são lidas com os padrões da engine, e ligar ou desligar o Modo Denuvo não mexe
  nelas.

## Correções

- **Downloads de fix revalidam cada redirecionamento.** Antes só o primeiro endereço era checado. Agora cada salto precisa
  ser https com nome de domínio; IP, `localhost` e variantes são recusados em qualquer salto, e uma cadeia para em 5.
- **Backup do appinfo.vdf não apaga o mais novo.** A rotação dos backups passou a usar a data do nome do arquivo.

## Build e testes

- Suíte de testes completa verde (2.453 na build desta versão). Nenhuma dependência nova.
- O texto do botão novo está em inglês e português (Brasil); os outros idiomas mostram em inglês por enquanto.

## Ressalva

- **Não validado ao vivo.** O botão de limpar cache e a instalação da v1.5.1 com `tools` foram testados só contra pastas
  temporárias, com a Steam simulada. O zip real da v1.5.1 foi inspecionado (traz `tools\version_pin.exe` e
  `tools\pattern_migrate.exe`), e os testes reproduzem o mesmo formato de nomes.
- **A correção do `AppIdVec` (Family Share) é da engine C++**; o app não reimplementa nada disso.
- **Atualizar o AmethystTool reseta `amethysttool.toml`** para o do release (com backup), como antes. Só o Modo Denuvo é
  levado adiante; quem mudou `failover` ou `record` à mão precisa refazer depois de atualizar.
- **`dotnet format` tem os mesmos erros de espaço em branco que já existiam** em 5 arquivos não tocados aqui.
