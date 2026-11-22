Catálogo de correções com espelho próprio no GitHub, correções que as listas oficiais não trazem e uma rodada de endurecimento
de segurança nos downloads, links e logs.

## Novidades

- **Catálogo independente.** A aba Correção agora lê primeiro um espelho próprio
  (`ArthurS357/LuaTools-Catalog`, no GitHub), depois o `lua.tools` e, por último, a cópia salva no disco. Se um dos dois sites
  cair, a aba continua funcionando. O aviso de modo offline da 1.8.1 continua valendo para a cópia salva.
- **Correções órfãs.** O que o espelho lista e o `lua.tools` não lista aparece na lista de correções do jogo, depois das
  oficiais (se o mesmo id existir nas duas, vale a oficial).
- **SHA-256 conferido, sem exceção.** Uma correção órfã só é aplicada se o SHA-256 do arquivo baixado bater com o do
  catálogo. Se não bater, o arquivo é descartado e nada é instalado. O arquivo fica travado desde a conferência até ser
  aplicado, para ninguém trocá-lo no meio.

## Segurança

- **Downloads:** o digest do Steamless é sempre o que vem dentro do app (um espelho da API não consegue mais impor o seu);
  os redirecionamentos do GitHub são validados salto a salto (https, nome de site público, limite de saltos); espelhos com IP,
  `localhost`, `*.local` ou nome de um rótulo só são ignorados; as capas aceitam só https público, até 2 MB e com prazo de leitura.
- **Ponte com a loja da Steam:** só é injetada em `store.steampowered.com`, conferindo o site de verdade e não um pedaço do
  endereço.
- **Arquivos de trabalho:** os downloads usam nomes locais fixos. Um nome vindo do servidor não vira mais caminho no disco.
- **Links e execução:** links das descrições de correção só abrem como `http(s)`; os executáveis de lançamento do Steamless
  só valem dentro da pasta do jogo; o `installdir` do `appmanifest` precisa ficar em `steamapps\common`.
- **Cookies e logs:** as requisições do fallback de DNS não guardam mais cookies; o texto de entrada do usuário é higienizado
  antes de ir para o log; o `auth.dat` registra só o tipo do erro; a pasta do seu perfil do Windows fica oculta nos avisos
  na tela.

## Build e testes

- Suíte de testes completa verde. Nenhuma dependência nova.
- 162 testes novos nesta versão (2.540 na 1.8.1, 2.702 nesta) cobrem o catálogo, os downloads, o Steamless, as capas e a higiene de logs.
- A versão 1.8.2 foi conferida ao vivo antes do release: catálogo pelo espelho com `source: "merged"` em um jogo real
  (Moon Sweepers, 5335510), download com SHA-256 e redirecionamento do GitHub, aviso offline e requisições sem cookies.

## Ressalvas

- **Os textos novos continuam em inglês e português (Brasil);** os outros idiomas mostram em inglês por enquanto.
- **A troca de DNS por rebinding continua um risco residual documentado:** a checagem de links olha o nome do site, não o IP
  para onde ele aponta na hora da conexão.
