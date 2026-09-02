## Correções

- **O accent agora chega aos controles do centro da janela.** Botões primários, toggles, anéis de foco e
  texto de destaque mantinham a cor que estava ativa quando a página foi carregada — escolher Vermelho
  deixava a janela e a barra lateral em wine com botões violeta ainda dentro.
- A causa: a biblioteca de UI **substitui** suas brushes de accent a cada troca em vez de recolori-las, e
  qualquer controle já na tela continuava usando a brush que pegou no carregamento. O app agora é dono
  dessas brushes e as recolore no lugar, como já fazia com todas as superfícies.
- Continua valendo na hora, **sem reiniciar**.

1.7.1 corrigiu o texto corrido; esta versão fecha a outra metade da mesma emenda.

## Testes

- +28 testes (total: 1629), verificando identidade da brush através de uma troca — não só a cor.

## Segurança

- Nenhuma telemetria, auto-update, elevação UAC ou envio de chaves reintroduzido.
- Formato de `settings.json` inalterado. Nenhuma dependência NuGet nova.
