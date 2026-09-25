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
- README: a tabela de diferenças frente ao build oficial dizia que o auto-update vinha desligado. Ele vem
  ligado, apontando só para o repositório do fork, como a seção "Updating" já dizia. Só a documentação
  mudou.

## Testes

- 20 testes novos para o casamento da resposta (formatos medidos na API real, recusas e payloads
  malformados), mais 1 para o jogo sem cache que tem de ser buscado. Total: 1680, todos passando.

## Segurança

- Nenhuma telemetria, elevação UAC ou envio de chaves reintroduzido. O auto-update continua apontando só
  para o repositório do fork.
- Formato de `settings.json` inalterado. Nenhuma dependência NuGet nova, nenhuma requisição de rede nova.
