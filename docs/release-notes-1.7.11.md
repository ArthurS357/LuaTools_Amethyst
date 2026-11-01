Versão com três novidades: o app pergunta antes de mexer numa Steam que já está aberta, faz backup dos saves de cada
jogo e avisa quando uma fonte entrega o lua sem manifestos.

## Novidades

- **Startup Guard.** Se a Steam já estiver aberta quando o LuaTools inicia, um diálogo oferece **Reiniciar Steam**,
  **Sair** ou **Continuar mesmo assim** (não recomendado), em vez de fechar a Steam sem avisar. Continuar deixa a Steam
  como está e avisa que o hook pode não se conectar. Fechar o diálogo (Esc ou X) equivale a **Sair**. A escolha fica
  registrada no log.
- **Backup de saves.** A aba Gerenciar ganhou a ação **Backup de saves…**, que compacta a pasta
  `userdata\<conta>\<appid>` da Steam em `%AppData%\LuaToolsGui\SaveBackups`, com **Restaurar** e **Excluir** para cada
  backup. A ação fica desabilitada para um jogo sem pasta de saves e sem backup.
- **Restauração segura.** Antes de extrair, o app valida o arquivo inteiro (zip-slip, links, outro jogo, tamanho),
  confere se há espaço em disco para tudo e compacta os saves atuais antes de sobrescrever. Se algo falhar, nada é
  extraído pela metade. Só a pasta de saves da própria Steam é coberta; jogos que salvam em Documentos ou AppData são
  informados como "sem saves".
- **Aviso de manifestos.** Quando uma fonte instala o lua sem manifestos, um aviso amarelo diz que o download na Steam
  pode falhar com um falso "Sem conexão com a internet" e recomenda a fonte Sadie (Hubcap). O aviso não aparece para
  DLCs nem quando a fonte já é a Sadie, e sai uma única vez por download.

## Mudanças

- O card da aba Modo passa a usar o nome real do projeto, **OpenSteamTool** (e **OpenSteamTool Nightly**), no lugar de
  BetterSteamTools, que é outro fork.
- Logs de diagnóstico quando a consulta de fontes volta vazia, com o motivo.

## Nada mais mudou

Sem dependência nova e sem requisição de rede nova.

## Traduções

As mensagens novas existem em inglês e português do Brasil; os demais idiomas usam o inglês até a próxima rodada de
tradução.
