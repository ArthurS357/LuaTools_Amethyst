A aba Fixes agora mostra o catálogo inteiro do LuaTools, com o badge "Instalado" nos jogos que você tem e esses jogos
primeiro.

## Novidades

- **Catálogo completo com badge "Instalado".** Todos os jogos com fix no LuaTools aparecem. Os que a Steam tem inteiros no
  disco ganham o mesmo badge verde da aba Gerenciar e vão para o topo; dentro de cada grupo a ordem é A–Z.
- **Aviso de instalados sem fix.** Se algum jogo instalado não está no catálogo, um aviso recolhido mostra quantos são;
  abra para ver os nomes.
- **Falhas separadas.** Se a biblioteca da Steam não puder ser lida, a lista continua, sem badges, com uma mensagem
  dizendo isso. Se o catálogo não carregar, só a mensagem de erro aparece.
- **Link `luatools://fix/…` de jogo não instalado** continua abrindo o painel de fixes do jogo.

## Correções

- **Carregamento da aba sem corrida.** Abrir um link de fix enquanto a aba carrega não cancela nem embaralha mais o
  carregamento; um espera o outro. Sair da aba interrompe o que estava em andamento.
- **Jogo instalado depois aparece sem reiniciar.** A biblioteca é lida de novo a cada visita à aba.

## Build e testes

- Suíte de testes completa verde (2.407 na build desta versão). Nenhuma dependência nova e nenhuma requisição de rede nova.
- As duas mensagens novas estão só em inglês por enquanto (pendentes de tradução nos 29 idiomas).

## Ressalva

- **Validado ao vivo:** o usuário passou o roteiro de seis passos no executável publicado (informado ao agente, não
  repetido na hora do commit): (1) catálogo inteiro com
  instalados no topo e badge só nos instalados; (2) aviso de instalados sem fix, com contagem e nomes; (3) catálogo
  indisponível mostra só o erro, sem lista nem aviso; (4) `libraryfolders.vdf` ausente mostra a lista sem badge e com a
  mensagem da biblioteca; (5) `luatools://fix/<appid>` de jogo não instalado abre o painel; (6) o filtro "Meus jogos"
  restringe aos jogos com lua em `stplug-in` e, desligado, mostra o catálogo todo.
- **O aviso aparece para quase toda biblioteca.** O catálogo cobre só jogos com fix, então a maioria dos jogos instalados
  fica fora dele. O aviso vem recolhido; se virar ruído, a UX será revista.
- **A exclusão do carregamento é testada só pelo código-fonte.** O teste confere o `SemaphoreSlim` e o `Release` no
  `finally` do `FixesViewModel`, não o comportamento sob concorrência, porque o cliente da API é fixo em `lua.tools` e não dá
  para simulá-lo.
- **`dotnet format` tem 21 erros de espaço em branco que já existiam** em 5 arquivos (`SaveBackupService`,
  `AuthProviderDefenseTests`, `CefInjectorPairingTests`, `DepotPickerTests`, `OnboardingCompletionTests`). Não foram
  introduzidos nesta versão e nenhum arquivo tocado aqui tem erro.
- **O catálogo continua dependendo do `lua.tools`:** lista pública, sem pin nem digest. O host R2 dos downloads de fix
  continua "a confirmar".
