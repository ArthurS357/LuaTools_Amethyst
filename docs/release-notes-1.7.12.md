Versão de ajustes no login e no backup de saves: cancelar o login no Discord é percebido na hora, a tela de boas-vindas
para de voltar depois que você entra, e o backup de saves confere o espaço em disco e a Steam aberta.

## Novidades

- **Cancelar no Discord é imediato.** Se você clicar em **Cancelar** na página do Discord, o app percebe na hora, em vez
  de ficar parado até os 5 minutos de espera, e pergunta se você quer **Tentar novamente** ou **Usar código manual**.
  Fechar a janela do navegador sem responder continua esperando o tempo limite e só então faz a mesma pergunta.
- **Usar código manual leva direto ao campo.** Fecha a tela de boas-vindas, se estiver aberta, abre as Configurações e
  coloca o cursor na caixa do código (rode `/login` no Discord para receber o código).

## Correções

- **Tela de boas-vindas.** Depois de "Usar código manual", ela ainda volta na próxima abertura até que um login dê certo
  (pelo navegador ou pelo código). Quando você entra, ela não volta mais. Desistir no meio mantém a tela.
- **Backup de saves confere o espaço.** Criar um backup agora exige o tamanho dos saves mais 10% mais 50 MB livres na
  unidade onde ficam os backups. Sem espaço, o app diz quanto precisa e não grava nada.
- **Restaurar com a Steam aberta.** O app avisa que a Steam precisa estar fechada (para o Steam Cloud não sobrescrever
  os arquivos) e oferece **Fechar Steam e continuar** ou **Cancelar**. A Steam é pedida para fechar normalmente, sem
  encerrar o processo à força. Se ela não fechar, nada é restaurado.

## Segurança

Os erros que o servidor de login informa continuam sendo ignorados nos primeiros 30 segundos, para que uma página
qualquer não consiga cancelar um login que você acabou de iniciar. A única exceção é o cancelamento explícito
(`access_denied`), que passa pelas mesmas verificações de origem local e de navegação antes de valer. Uma página local
que insista nesse endereço ainda pode cancelar um login recém-iniciado; o custo é um clique em "Tentar novamente".

## Nada mais mudou

Sem configuração nova, sem dependência nova e sem requisição de rede nova.

## Traduções

As mensagens novas existem em inglês e português do Brasil; os demais idiomas usam o inglês até a próxima rodada de
tradução.
