Seletor do modo de DNS em Configurações > Rede, para o fallback pela Cloudflare que chegou na 1.8.0, e a aba Correção que
continua mostrando o catálogo quando o `lua.tools` está fora do ar.

## Novidades

- **Configurações > Rede > Fallback de DNS (Cloudflare DoH).** Uma lista com três opções, que antes só podiam ser mudadas
  editando o `settings.json`:
  - **Auto (padrão):** usa o DNS do sistema e só recorre à Cloudflare depois de uma falha de resolução de nome.
  - **Sempre:** consulta a Cloudflare antes de conectar. A Cloudflare (`1.1.1.1`) vê o nome dos sites da lista fixa e o seu IP.
  - **Nunca:** nunca usa DNS externo.
- **Vale na hora, sem reiniciar.** A escolha é gravada no `settings.json` e o app passa a usá-la na próxima requisição. Quem já
  tinha `DnsMode` no arquivo vê a opção correta marcada ao abrir a tela.
- **Nada mudou no que o fallback faz:** os mesmos hosts, só HTTPS, certificado e nome do site validados como antes.
- **Catálogo da aba Correção salvo no disco.** Cada vez que o `lua.tools` entrega o catálogo, o app guarda uma cópia em
  `%AppData%\LuaToolsAmethyst\cache\denuvo_listings.json`. Se o site não responder (sem internet, manutenção, erro 503), a aba
  mostra essa cópia com o aviso "Modo offline: exibindo catálogo salvo em [data]", em vez de ficar vazia ou com erro. A cópia
  vale por 24 horas; depois disso, sem conexão, a aba volta a mostrar a falha de antes. Ao reabrir a aba o app não tenta a rede
  sozinho: use **Atualizar** quando a conexão voltar, e o aviso some. Não há requisição nova, é só um arquivo.

## Build e testes

- Suíte de testes completa verde (2.540 na build desta versão). Nenhuma dependência nova e nenhuma requisição de rede nova.
- Os textos novos estão em inglês e português (Brasil); os outros idiomas mostram em inglês por enquanto.

## Ressalvas

- **Não abri o app rodando.** O vínculo da lista de DNS e a lógica do cache e do aviso offline são cobertos por teste, mas o
  visual da seção Rede e do aviso na aba Correção não foi conferido a olho, e a queda do `lua.tools` foi simulada, não real.
- **Só o catálogo da aba Correção é guardado.** A lista de correções de cada jogo (ao abrir um jogo) continua precisando de
  rede, e os downloads também.
- **O DNS pela Cloudflare continua sem teste com um bloqueio real de provedor** (ver as notas da 1.8.0).
- **O estado de falha do DNS no modo Auto ainda dura até fechar o app.** Trocar para Nunca desliga o fallback na hora; voltar
  para Auto não apaga uma falha já registrada na sessão.
