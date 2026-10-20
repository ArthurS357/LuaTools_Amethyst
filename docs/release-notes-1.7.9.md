Versão de correções: as respostas da página da loja deixam de poder ser entregues ao pedido errado, o arrastar e soltar
de manifest segue a mesma regra de nome das outras entradas, o CloudRedirect avisa com honestidade quando não pode
ser baixado, os selos da aba Modo foram corrigidos, e o login resiste a quedas do servidor de autenticação.

## Correções

- **Respostas da página da loja pareadas por id.** O injetor CEF associa cada resposta ao pedido que a originou, em vez
  de usar a ordem de chegada. Uma resposta lenta ou perdida não pode mais ser entregue a outro pedido. Timeouts e
  chamadas para métodos desconhecidos (`methodMap`) agora aparecem no log.
- **Arrastar e soltar de manifest.** Só é aceito o nome `<depot>_<manifest>.manifest`. Um `1234.manifest` solto ou uma
  duplicata do navegador como `123_456 (1).manifest` é recusado com uma mensagem legível, em vez de ser gravado no
  depotcache do Steam.

## Segurança

- A regra de nome do manifest (`ManifestNameRegex`) passa a valer também na entrada por arrastar e soltar. Nada fora
  do formato esperado é escrito no depotcache.

## UI

- **CloudRedirect:** quando o repositório de origem está indisponível, a mensagem diz isso, em vez de culpar o GitHub,
  e o painel add-on fica escondido até a origem voltar. A URL configurada não mudou.
- **Selos da aba Modo:** AmethystTool aparece como **RECOMENDADO**, BetterSteamTools como **UPSTREAM**, e o
  BetterSteamTools Nightly só como **EXPERIMENTAL** (perdeu o selo de suporte a CloudRedirect).

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

- Tradução de `Mode_Badge_Upstream` para os 28 idiomas (hoje EN e pt-BR mostram "UPSTREAM"; os outros caem no inglês).
- Popups do BetterSteamTools Nightly na instalação (`IPC spec not found`, `signature file not found`): bug do upstream
  (`OpenSteamTool` de julho/2026), fora do alcance do LuaTools.
- CloudRedirect: trocar a fonte para um fork anônimo (decisão pendente).
- Polyfill: o timeout virar falha explícita (depende de ver `success:false` no `luatools.js`).
- `Verb = "runas"` em `HttpServerService` (decisão pendente).
- F-04 (auto-update por espelhos de terceiros), F-09 (Denuvo sem teste ao vivo) e as traduções do F-10.
- Tradução das mensagens novas de login para os 28 idiomas (hoje só inglês e pt-BR).

## Testes

- Total: 1941, todos passando (1872 na 1.7.8). Testes novos para o regex do manifest, a mensagem do CloudRedirect, os
  selos da aba Modo e as defesas de login (37 casos, com teste de mutação).

## Segurança do fork

- Nenhuma telemetria, elevação UAC ou envio de chaves. A atualização automática do app continua desligada por
  padrão. Formato de `settings.json` inalterado, nenhuma dependência NuGet nova. A única requisição de rede nova é a
  checagem do servidor de login (`GET /auth/v1/health`), feita só quando você clica em entrar.
