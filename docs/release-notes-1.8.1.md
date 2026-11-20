Seletor do modo de DNS em Configurações > Rede, para o fallback pela Cloudflare que chegou na 1.8.0.

## Novidades

- **Configurações > Rede > Fallback de DNS (Cloudflare DoH).** Uma lista com três opções, que antes só podiam ser mudadas
  editando o `settings.json`:
  - **Auto (padrão):** usa o DNS do sistema e só recorre à Cloudflare depois de uma falha de resolução de nome.
  - **Sempre:** consulta a Cloudflare antes de conectar. A Cloudflare (`1.1.1.1`) vê o nome dos sites da lista fixa e o seu IP.
  - **Nunca:** nunca usa DNS externo.
- **Vale na hora, sem reiniciar.** A escolha é gravada no `settings.json` e o app passa a usá-la na próxima requisição. Quem já
  tinha `DnsMode` no arquivo vê a opção correta marcada ao abrir a tela.
- **Nada mudou no que o fallback faz:** os mesmos hosts, só HTTPS, certificado e nome do site validados como antes.

## Build e testes

- Suíte de testes completa verde (2.529 na build desta versão). Nenhuma dependência nova e nenhuma requisição de rede nova.
- Os textos novos estão em inglês e português (Brasil); os outros idiomas mostram em inglês por enquanto.

## Ressalvas

- **Não abri a tela com o app rodando.** O vínculo (lista, valor salvo, gravação) é coberto por teste, mas o visual da seção
  nova não foi conferido a olho.
- **O DNS pela Cloudflare continua sem teste com um bloqueio real de provedor** (ver as notas da 1.8.0).
- **O estado de falha do DNS no modo Auto ainda dura até fechar o app.** Trocar para Nunca desliga o fallback na hora; voltar
  para Auto não apaga uma falha já registrada na sessão.
