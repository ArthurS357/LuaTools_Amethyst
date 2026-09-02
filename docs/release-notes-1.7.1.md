## Correções

- **O accent agora alcança o texto das páginas**, não só a barra de navegação em volta delas. A chave de
  texto padrão do container central vinha do WPF-UI como branco puro fora do tema — era o único token que
  uma troca de accent não conseguia mover.
- **Unlocks de DLC entram na fila de Downloads.** Era o último caminho que ainda baixava e instalava por
  fora, invisível na aba Downloads e capaz de rodar junto com uma instalação de manifesto no mesmo arquivo.
  Os dois agora se excluem.
- **A barra de progresso redundante da aba Add saiu.** A linha da fonte agora mostra "Queued" com um link
  para a aba Downloads, onde estão tamanho, velocidade, pause e cancel de verdade.
- **Código morto removido** junto com o caminho inline que ele servia.

## Testes

- +6 testes (total: 1601).

## Segurança

- Nenhuma telemetria, auto-update, elevação UAC ou envio de chaves reintroduzido.
- Formato de `settings.json` inalterado. Nenhuma dependência NuGet nova.
