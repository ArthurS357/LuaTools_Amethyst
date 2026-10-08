## Correções de segurança

- **O plugin da loja não se atualiza mais sozinho sem você pedir.** A cada abertura da Steam, o app trocava o
  plugin e reiniciava a Steam quando havia versão nova, mesmo com `PluginAutoUpdate` desligado. Agora o
  update automático do plugin só roda com essa opção ligada.
- **A ponte local (porta 6767) exige o token do app em toda requisição** e o método HTTP certo em cada rota.
  Antes, uma página qualquer aberta no navegador podia adicionar ou apagar um jogo às cegas. O plugin da loja
  continua funcionando normalmente: ele já mandava o token.
- **Um zip de manifests só grava em `depotcache` arquivos com nome `<depot>_<manifest>.manifest`.** Qualquer
  outro nome no zip é pulado e aparece como erro, em vez de ser gravado onde o nome apontasse.
- **Luas, manifests, detalhes de jogo em cache e o arquivo de login são gravados de forma atômica.** Uma
  queda de energia ou travamento no meio da gravação não deixa mais um arquivo truncado que o app passa a
  tratar como válido.

## Correções

- **Download que falha, é cancelado ou para de receber dados não deixa mais lixo em `%TEMP%`.** Um servidor
  que fica 60 segundos sem mandar nada agora encerra o download com erro, em vez de travá-lo até você
  cancelar. Conexão lenta que continua recebendo dados não é afetada.
- **O download de fix Denuvo só segue links `https` para um endereço com nome.** Qualquer outro link é
  recusado, e o endereço recusado fica no `plugin-backend.log` para você reportar.
- **O índice de builds salvas (aba Builds) se recupera do backup quando está danificado**, e o backup não é
  mais sobrescrito pela cópia danificada.

## Ressalva

- O download de fix Denuvo com a checagem nova de link **não foi testado contra um fix real** antes desta
  publicação. Se um fix falhar com "The server sent a download link this app won't follow", o endereço
  recusado está no `plugin-backend.log` (pasta `%AppData%\LuaToolsGui`). Reporte-o numa issue.

## Testes

- 98 testes novos desde a 1.7.6, incluindo os primeiros testes diretos da instalação de zip e das rotas da
  ponte local. Cada correção foi conferida removendo-a de propósito e vendo os testes falharem. Total: 1780,
  todos passando.

## Segurança

- Nenhuma telemetria, elevação UAC ou envio de chaves reintroduzido. A atualização automática do app
  continua desligada por padrão.
- Formato de `settings.json` inalterado. Nenhuma configuração nova, nenhuma dependência NuGet nova, nenhuma
  requisição de rede nova.
