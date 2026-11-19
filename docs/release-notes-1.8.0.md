Engine AmethystTool v1.5.3, dados do app em uma pasta própria (`LuaToolsAmethyst`), fallback de DNS pela Cloudflare para
regiões com bloqueio e as correções da auditoria.

## Novidades

- **Engine AmethystTool v1.5.3.** Instalar ou atualizar o AmethystTool pega o release mais recente, e o v1.5.3 já é aceito
  sem mudança no app: o instalador procura o arquivo `AmethystTool-*.zip` e exige o hash SHA-256 publicado no release. As
  correções do bug de inicialização e da tag de família estão na engine, não no app.
- **Dados do app em `%AppData%\LuaToolsAmethyst`.** Na primeira abertura o app **copia** (não move) o conteúdo de
  `%AppData%\LuaToolsGui` para a pasta nova. A pasta antiga fica como estava, então o LuaTools original continua funcionando
  ao lado. Se a cópia não terminar (um arquivo preso, por exemplo), nada é promovido: o app usa a pasta antiga naquela sessão,
  sem perder sua configuração, e tenta de novo na abertura seguinte. Se a pasta nova já tem dados, ela vence e a antiga é
  ignorada. Desinstalar o app apaga só a pasta nova; a antiga, que também é do LuaTools original, não é tocada.
- **Vacina `DonateKeys: false`.** O LuaTools original trata a chave `DonateKeys` ausente como ligada e, ligada, envia chaves
  de descriptografia da Steam por HTTP puro. Ao migrar, o app grava `"DonateKeys": false` no `settings.json` da pasta antiga,
  para que abrir o original não ligue isso. É a única escrita na pasta antiga, acontece uma vez, mantém as demais chaves do
  arquivo e nunca cria o arquivo. Este app em si nunca envia chaves.
- **Fallback de DNS pela Cloudflare (nova requisição de rede).** Quando o DNS do sistema não resolve um nome, o app consulta o
  DNS-sobre-HTTPS da Cloudflare, `https://1.1.1.1/dns-query`, para obter o endereço IPv4 e conecta nele. A Cloudflare vê o
  nome do site consultado e o seu IP. Isso vale só para estes hosts, sempre em HTTPS:
  `lua.tools`, `db.lua.tools`, `hubcapmanifest.com`, `applist.morrenus.xyz`, `github.com`, `api.github.com`,
  `raw.githubusercontent.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`, `ghproxy.net`,
  `ghfast.top` e `gh.ddlc.top`. Qualquer outro site, a Steam local (CDP), `localhost`, nomes locais e IPs literais nunca
  usam isso. O certificado e o nome do site continuam sendo validados como antes, e respostas com endereço privado
  (`10.x`, `172.16–31.x`, `192.168.x`), de loopback ou link-local (`169.254.x`) são descartadas.
  - **Modos (`DnsMode` no `settings.json`):** `Auto` (padrão, só depois de uma falha de resolução de nome; reset de conexão,
    recusa e timeout não contam), `Always` (consulta antes de conectar) e `Never` (desliga).
  - **O estado de falha do DNS persiste até o app fechar, afetando todos os hosts da allowlist.** Depois de uma falha, todo
    host da lista passa a ser resolvido pela Cloudflare naquela sessão, não só o que falhou.
- **Texto do botão Limpar Cache de Conteúdo (AppInfo).** A descrição agora diz que ele também é o fallback definitivo caso uma
  atualização da Steam mude a assinatura que a engine (v1.5.1 ou mais nova) intercepta.

## Correções

- **Cópia da migração para no primeiro erro.** Antes continuava copiando o resto e descartava tudo no final.
- **Promoção atômica da pasta nova.** Quando a pasta nova existia só com o log, o conteúdo era movido arquivo por arquivo e
  uma falha no meio deixava uma pasta pela metade, que o app tomava por "já migrada". Agora o log atual vai para a pasta de
  preparo, a pasta vazia é removida e tudo é promovido com uma única troca de nome.
- **DNS pela Cloudflare mais restrito.** Endereços privados e link-local nas respostas e URLs `http://` não são mais aceitos.
- **Textos que ainda citavam `%AppData%\LuaToolsGui`** (comentários e documentação interna) agora apontam para a pasta nova.
  O código já usava a nova.

## Build e testes

- Suíte de testes completa verde (2.525 na build desta versão). Nenhuma dependência nova.
- O texto novo do botão está em inglês e português (Brasil); os outros idiomas mostram em inglês por enquanto.

## Ressalvas

- **O DNS pela Cloudflare não foi testado com um bloqueio real de provedor.** Os testes simulam a falha de resolução e a
  resposta da Cloudflare; não houve teste com o app atrás de um provedor que realmente bloqueie `lua.tools` ou `github.com`.
- **`DnsMode` não tem tela.** Só se muda editando o `settings.json` (`"DnsMode": "Never"` para desligar).
- **A engine v1.5.3 não foi testada ao vivo.** Confirmei apenas que o release publica `AmethystTool-v1.5.3.zip` com hash
  SHA-256 na API do GitHub; a instalação real não foi repetida.
- **`tools\*.exe` continuam opcionais (recusa do B-02).** O app não exige `version_pin.exe` e `pattern_migrate.exe` para
  considerar a engine instalada: instalações anteriores à v1.5.1 não os têm e deixariam de contar como instaladas.
- **A pasta antiga fica com uma cópia dos seus dados**, inclusive o token de login protegido (`auth.dat`), porque ela é
  compartilhada com o LuaTools original e o app não a apaga nem na migração nem na desinstalação.
