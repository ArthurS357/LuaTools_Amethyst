## Mudanças

- **Atualização automática desligada por padrão.** O app não procura nem instala versão nova sozinho. Para
  atualizar, baixe a release e rode o instalador. Quem quiser o update automático de volta pode religar no
  `settings.json` (a aba About mostra a pasta e o que está valendo):
  `"AppUpdateRepos": ["https://github.com/ArthurS357/LuaTools_Amethyst"]`.
- Esta é a **última atualização automática**: quem está na 1.7.5 recebe a 1.7.6 sozinho, porque a 1.7.5 ainda
  tinha o update ligado. A partir da 1.7.6, nada é baixado sem você pedir.
- Um update que já tenha sido baixado não é mais aplicado ao abrir o app, só ao fechar. Desligar o update
  automático nunca deixa uma versão pendente para trocar o app por baixo de você.

## Correções

- **Nome, capa, gêneros e descrição voltaram a aparecer para jogos populares.** A Steam mudou a forma de
  responder aos detalhes da loja: para todo jogo que tem DLC ou trilha sonora, a resposta agora vem
  arquivada sob o id desse complemento, não sob o do jogo. O app procurava só pelo id do jogo, não achava
  nada e desistia em silêncio. Counter-Strike 2, Portal 2, Dota 2, Elden Ring, Cyberpunk 2077 e outros
  ficavam sem nome, capa, gêneros e sinopse se ainda não estivessem em cache.
- A resposta agora é casada pelo id do jogo que a Steam coloca dentro dela. Uma resposta que não dá para
  provar que é do jogo é **recusada**, nunca adivinhada, então os detalhes de um jogo não podem ser salvos
  no lugar dos de outro.
- Os jogos afetados se preenchem sozinhos conforme a atualização em segundo plano chega a eles. Não é
  preciso re-adicionar nada. O que já estava em cache antes da mudança da Steam nunca foi afetado.
- **Jogo adicionado com o app aberto ganha nome e capa na hora**, sem precisar reiniciar. Antes, um jogo
  sem detalhes em cache ficava marcado como "não encontrado" na memória e nunca era buscado de novo até o
  app ser reaberto.

## Testes

- 20 testes novos para o casamento da resposta (formatos medidos na API real, recusas e payloads
  malformados), 1 para o jogo sem cache que tem de ser buscado e 2 para o update desligado por padrão.
  Total: 1682, todos passando.

## Segurança

- Nenhuma telemetria, elevação UAC ou envio de chaves reintroduzido. O app deixou de fazer a verificação de
  update por conta própria; quando religada, continua aceitando só o repositório do fork.
- Formato de `settings.json` inalterado. Nenhuma dependência NuGet nova, nenhuma requisição de rede nova.
