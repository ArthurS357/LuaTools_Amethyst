Versão de correções: o Sair da bandeja deixou de travar, o tema acompanha as trocas de cor, o arrastar e soltar
instala um arquivo por vez, e o log e o login local ficaram mais difíceis de enganar. As mensagens de erro dos
serviços agora vêm dos arquivos de idioma (com tradução em português do Brasil).

## Correções

- **Sair pela bandeja não deixa mais o processo vivo.** E o Steam só é fechado no Sair quando foi o LuaTools
  que o abriu.
- **Cancelar também para um jogo que está sendo adicionado pela loja** (`/add`).
- **Tema:** a cor de destaque e a rampa neutra passam a valer no conteúdo central a cada troca, inclusive em
  trocas seguidas. Corrigido o erro que derrubava a página Depots depois de trocar o tema.
- **Uma rajada de erros vira um único aviso**, em vez de uma fila de toasts.
- **Arrastar e soltar:** as instalações passam uma de cada vez; soltar arquivos durante um lote em andamento
  os coloca no lote. O `.lua` escolhido dentro de um zip é o mesmo na prévia e na instalação.
- **O login não congela mais a janela** ao gravar a sessão.

## Segurança

- **Callback do login (Discord):** só aceita o redirecionamento real do navegador. Um código forjado não encerra
  o login, e um erro de login nos primeiros 30 segundos é ignorado (uma negação legítima nesse intervalo também
  é descartada; o botão espera o limite de 5 minutos).
- **O log da ponte local não aceita mais texto que forje linhas**, as linhas do log agora têm a data, e os
  detalhes de um erro não voltam mais para quem fez a requisição: ficam, sem credenciais, no `plugin-backend.log`.
- O erro `HttpListenerException 995` que aparecia no `crash.log` ao fechar o app não é mais registrado.

## Idiomas

- 30 mensagens de erro que estavam em inglês dentro do código (instalador, unlocker, login, download e
  inicialização) passaram para os arquivos de idioma e têm tradução em **português do Brasil**. Os outros 28
  idiomas mostram o texto em inglês nessas mensagens até a tradução chegar.

## Ressalva

- O download de fix Denuvo (checagem de link da 1.7.7) **continua sem ter sido exercitado ao vivo**: não há jogo
  Denuvo instalado no ambiente de teste. Se um fix falhar com "The server sent a download link this app won't
  follow", o endereço recusado está no `plugin-backend.log` (`%AppData%\LuaToolsGui`).

## Dívida registrada para a 1.7.9

- F-04: update automático por espelhos de terceiros sem fixar a origem (opt-in, desligado por padrão).
- Tradução das 30 mensagens para os outros 28 idiomas.
- 4 textos da página de resultado do login (HTML) ainda em inglês.

## Testes

- Total: 1872, todos passando. A suíte não escreve mais no `plugin-backend.log` real.

## Segurança do fork

- Nenhuma telemetria, elevação UAC ou envio de chaves. A atualização automática do app continua desligada por
  padrão. Formato de `settings.json` inalterado, nenhuma dependência NuGet nova, nenhuma requisição de rede nova.
