Versão de correções: as respostas da página da loja deixam de poder ser entregues ao pedido errado, o arrastar e soltar
de manifest segue a mesma regra de nome das outras entradas, o CloudRedirect avisa com honestidade quando não pode
ser baixado, e os selos da aba Modo foram corrigidos.

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

## Testes

- Total: 1904, todos passando (1872 na 1.7.8). Testes novos para o regex do manifest, a mensagem do CloudRedirect e os
  selos da aba Modo.

## Segurança do fork

- Nenhuma telemetria, elevação UAC ou envio de chaves. A atualização automática do app continua desligada por
  padrão. Formato de `settings.json` inalterado, nenhuma dependência NuGet nova, nenhuma requisição de rede nova.
