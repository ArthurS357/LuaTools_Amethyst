Versão de correções: as respostas da página da loja deixam de poder ser entregues ao pedido errado, o arrastar e soltar
de manifest segue a mesma regra de nome das outras entradas, o CloudRedirect avisa com honestidade quando não pode
ser baixado, os selos da aba Modo foram corrigidos, o login resiste a quedas do servidor de autenticação, e as
Configurações ganham uma tela para limpar os backups acumulados na pasta da Steam.

## Correções

- **Respostas da página da loja pareadas por id.** O injetor CEF associa cada resposta ao pedido que a originou, em vez
  de usar a ordem de chegada. Uma resposta lenta ou perdida não pode mais ser entregue a outro pedido. Timeouts e
  chamadas para métodos desconhecidos (`methodMap`) agora aparecem no log.
- **Arrastar e soltar de manifest.** Só é aceito o nome `<depot>_<manifest>.manifest`. Um `1234.manifest` solto ou uma
  duplicata do navegador como `123_456 (1).manifest` é recusado com uma mensagem legível, em vez de ser gravado no
  depotcache do Steam.

- **Aviso de erro que lançava exceção.** `ToastService.Show` agora não faz nada enquanto a janela principal não está
  pronta, como o `ShowAction` já fazia; antes, com a aplicação em pé e sem a janela, o Wpf.Ui lançava "SnackbarPresenter
  was never set".

## Segurança

- A regra de nome do manifest (`ManifestNameRegex`) passa a valer também na entrada por arrastar e soltar. Nada fora
  do formato esperado é escrito no depotcache.
- **Exceção de elevação documentada.** O único `runas` do app (`netsh http add urlacl`, só quando o servidor local não
  sobe) está registrado como exceção no código, e um teste novo falha se aparecer outro `Verb = "runas"` em qualquer
  arquivo do `src/`. O comportamento não mudou.

## Manutenção (novo)

Em **Configurações > Manutenção** há uma lista dos backups que ficam na pasta da Steam, com data, tamanho e
quantidade de arquivos:

- pastas `AmethystTool-backup-*` (instalação do AmethystTool) e `Removal-backup-*` (remoção de plugin ou modo);
- arquivos soltos `AmethystTool-v*.dll.bak`, sobras do updater do AmethystTool. Qualquer outro `.bak` na raiz aparece
  com o aviso "Backup desconhecido, verifique antes" e só é apagado se você marcá-lo.

Dá para **apagar selecionados** ou **apagar tudo com mais de 30 dias**, sempre com confirmação. A limpeza só olha a raiz
da Steam (nunca subpastas, então os `.bak` do Revert de fix Denuvo ficam intactos), não segue junctions nem symlinks, e
recusa o lote inteiro se algum item não for o que parece. Apagar é definitivo: não dá para reverter instalações
anteriores do AmethystTool nem remoções de plugin.

## UI

- **CloudRedirect:** quando o repositório de origem está indisponível, a mensagem diz isso, em vez de culpar o GitHub,
  e o painel add-on fica escondido até a origem voltar. A URL configurada não mudou.
- **Selos da aba Modo:** AmethystTool aparece como **RECOMENDADO**, BetterSteamTools como **UPSTREAM**, e o
  BetterSteamTools Nightly só como **EXPERIMENTAL** (perdeu o selo de suporte a CloudRedirect).
- **Depots:** um jogo que só continua na lista por causa de builds guardadas, sem lua ativa, mostra "Builds guardadas
  (sem lua ativa)". "Remover jogo" continua sendo o caminho para tirá-lo da lista.
- **Onboarding:** o botão **Usar código manual** também aparece abaixo do erro de login na tela de boas-vindas.
- **Traduções:** `Mode_Badge_Upstream` e as 30 mensagens de erro do F-10 agora existem nos 29 idiomas.

## Login e autenticação

Em 20/10/2026 o servidor de autenticação (Supabase) caiu por excesso de acessos. Esta versão adiciona três defesas:

- **Aviso antes de abrir o navegador.** O app confere se o servidor de login está respondendo. Se não estiver, um
  diálogo explica e oferece **Tentar mesmo assim**, **Usar código manual** ou **Cancelar**, em vez de esperar 5
  minutos em silêncio.
- **Código manual à vista.** Quando o login pelo navegador falha ou expira, o botão **Usar código manual** leva direto
  ao campo do código `/login` do Discord em Configurações.
- **Sessões ativas sobrevivem a falhas transientes do provider** (5xx, timeout, queda de rede). Só tokens
  comprovadamente revogados (`refresh_token_not_found`, `session_expired` etc.) encerram a sessão. O health check
  pré-login e o fallback por código manual evitam o timeout de 5 min durante indisponibilidade.

## Ressalva

- O download de fix Denuvo (checagem de link da 1.7.7) **continua sem ter sido exercitado ao vivo**: não há jogo
  Denuvo instalado no ambiente de teste. Se um fix falhar com "The server sent a download link this app won't
  follow", o endereço recusado está no `plugin-backend.log` (`%AppData%\LuaToolsGui`).

## Dívida registrada para a 1.7.10

- Popups do BetterSteamTools Nightly na instalação (`IPC spec not found`, `signature file not found`): bug do upstream
  (`OpenSteamTool` de julho/2026), fora do alcance do LuaTools.
- CloudRedirect: trocar a fonte para um fork anônimo (decisão pendente).
- Polyfill: o timeout virar falha explícita (depende de ver `success:false` no `luatools.js`).
- Modo Custom (o app não coloca nada na Steam), F-04 (auto-update por espelhos de terceiros) e F-09 (Denuvo sem teste
  ao vivo).
- Traduzir para os 28 idiomas as mensagens novas de login (`Auth_UseManualCode` e afins) e as da tela Manutenção
  (`Settings_Backups_*`); hoje caem no inglês fora do pt-BR.
- Manutenção: um backup de instalação ou remoção ainda em andamento pode ser apagado; o card do AmethystTool continua
  citando a última pasta de backup depois de ela ser apagada; um lote recusado mostra uma mensagem genérica; e a troca
  de uma pasta por junction entre a validação e o delete tem uma janela residual de microssegundos (a pasta é relida
  logo antes, e o link nunca é percorrido).
- Onboarding: "Usar código manual" fecha a tela de boas-vindas sem marcá-la como concluída, então ela volta no próximo
  início.

## Testes

- Total: 2038, todos passando (1872 na 1.7.8). Testes novos para o regex do manifest, a mensagem do CloudRedirect, os
  selos da aba Modo, as defesas de login, a guarda de elevação e a tela Manutenção, com teste de mutação nos controles
  de segurança.

## Segurança do fork

- Nenhuma telemetria ou envio de chaves, e nenhuma elevação UAC nova (a única existente é a exceção do `netsh`
  descrita acima). A atualização automática do app continua desligada por padrão. Formato de `settings.json`
  inalterado, nenhuma dependência NuGet nova. A única requisição de rede nova é a checagem do servidor de login
  (`GET /auth/v1/health`), feita só quando você clica em entrar.
