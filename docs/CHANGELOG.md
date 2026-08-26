# Changelog

## 1.6.0 — 2026-08-26

### Um modo ativo por vez, AmethystTool no topo, SteamTools aposentado

- **AmethystTool e um Mode nao aparecem mais os dois como ATIVO.** Os cartoes de Mode liam o estado ativo
  de `settings.SelectedMode`; o cartao do AmethystTool lia da simples presenca dos seus quatro arquivos na
  pasta da Steam. Como instalar o AmethystTool nunca mexia em `SelectedMode`, instalar o BetterSteamTools e
  depois o AmethystTool acendia o selo ATIVO nos dois. O contrario tambem falhava: instalar um Mode
  sobrescreve `dwmapi.dll` e `xinput1_4.dll` mas deixa `AmethystTool.dll` e `amethysttool.toml` no lugar,
  entao o cartao continuava se dizendo ativo. Agora existe **um unico slot**: quem ocupa os proxies e um
  valor so (`ActiveBackendPolicy`), entao instalar um demove o outro sem que nada precise percorrer a lista
  apagando selo. Dois nao podem estar ligados porque um nao pode estar ligado duas vezes.
- **O selo ATIVO do AmethystTool tambem exige evidencia em disco.** Ele agora pede selecao *e* payload
  presente, entao apagar os arquivos por fora do app nao deixa um cartao afirmando algo que nao e verdade.
- **Formato do `settings.json` inalterado.** O campo `SelectedMode` sempre foi uma string livre lida como
  nome de enum, e um valor que nao e membro sempre significou "nenhum Mode ativo". O AmethystTool grava
  nesse mesmo campo, com um token que nenhum Mode usa. Um arquivo escrito por uma versao anterior mantem o
  significado, e um escrito por esta e lido por uma anterior como "nenhum Mode" — nunca como o Mode errado.
- **A deteccao automatica nao rouba mais o slot.** O AmethystTool e um fork, entao seus proxies podem
  bater com o hash do BetterSteamTools. A deteccao agora nao roda quando o AmethystTool esta ativo — do
  contrario ela readotaria o Mode e o duplo ATIVO voltaria por outro caminho.

- **O cartao do AmethystTool passou a ser o primeiro da pagina Modo.** Ele lidera a lista, acima do
  BetterSteamTools e do BST Nightly.

- **A descricao do AmethystTool agora diz o que ele e.** Antes era "um plugin nativo da Steam (um fork do
  BetterSteamTools)". Agora: fork independente do BetterSteamTools voltado a privacidade, com auto-update
  desativado e sem telemetria, injetando nativamente pela pasta da Steam. O texto ingles foi aplicado nos
  29 idiomas: a frase faz afirmacoes factuais sobre privacidade, e uma traducao velha dizendo outra coisa
  seria pior que ingles.

- **Instalar o AmethystTool absorve o registro de Mode que ficou obsoleto.** Antes desta correcao, um
  registro `mode-*` que ja tinha instalado `dwmapi.dll`/`xinput1_4.dll` continuava reivindicando os dois
  depois que o AmethystTool os sobrescrevia — a desinstalacao do AmethystTool relatava `SharedKept` para
  arquivos que, na pratica, ja eram dele, "guardados" por um registro que nao correspondia mais aos bytes
  em disco. Agora, ao terminar a instalacao, o AmethystTool remove essas duas reivindicacoes especificas do
  registro do Mode antigo (`InstallManifest.AbsorbFiles`, politica pura + `InstallManifestService` para a
  escrita). **So os nomes que o AmethystTool de fato acabou de gravar saem do registro antigo** — se aquele
  registro ainda listar um arquivo que o AmethystTool nunca toca (ex.: `OpenSteamTool.dll` do
  BetterSteamTools), ele fica exatamente como estava. Um registro que so tinha os dois proxies e removido
  por inteiro; um que tem mais alguma coisa e apenas reduzido. Idempotente: rodar a instalacao de novo nao
  muda nada na segunda vez, porque na primeira ja nao sobrou nada para absorver.

- **SteamTools saiu da pagina Modo.** O upstream parou de publicar atualizacoes, entao oferece-lo mandava
  o usuario para um backend que nao vai ser consertado. O cartao nao e mais mostrado e `InstallAsync`
  recusa o modo. **A definicao continua no app de proposito**: o membro do enum e uma chave persistida —
  nomeia o registro `mode-steamtools` que diz quais arquivos ele colocou na pasta da Steam, e e o que
  `PluginRemovalService.ClaimedByOthers` consulta para nao apagar os proxies de uma instalacao que ainda
  esta la. Apagar a definicao deixaria os dois orfaos. Quem ainda tiver SteamTools como Mode ativo continua
  vendo o cartao, porque ele e o unico caminho ate o botao Desinstalar; escolhe-lo de novo e impossivel.

### AmethystTool passou da aba Plugin para a aba Modo

- **O cartao do AmethystTool agora fica na aba Modo, junto aos outros modos.** Ele estava na aba Plugin,
  ao lado do plugin de store-page, com quem nao compartilha nada: fonte diferente, payload diferente,
  destino diferente. O que ele compartilha e com os Modes — dois dos quatro arquivos que instala,
  `dwmapi.dll` e `xinput1_4.dll`, sao exatamente os proxies que SteamTools, BetterSteamTools e BST Nightly
  colocam. Ter AmethystTool instalado e ter um Mode instalado e o mesmo slot, entao os dois passam a
  aparecer na mesma lista. A aba Plugin voltou a ser so o plugin de store-page.
- **Instalar e desinstalar passaram a usar o overlay da propria pagina.** O cartao levantava dois
  `MessageBox` proprios enquanto vivia na aba Plugin; os cartoes de Mode ja confirmavam pelo scrim in-page.
  Agora e um so, com o mesmo texto ("fechar Steam e continuar" na instalacao, "os arquivos vao para uma
  pasta de backup e a Steam fica fechada" na remocao).
- **Uma instalacao de Mode e uma do AmethystTool nao rodam mais ao mesmo tempo.** Nas duas paginas
  separadas, nada impedia iniciar as duas — e as duas param a Steam e escrevem os mesmos dois proxies. O
  botao do AmethystTool agora e regido pelo mesmo `IsBusy` da pagina que rege os cartoes de Mode, entao
  qualquer uma das operacoes desabilita a outra enquanto roda. O progresso vai para a barra unica no rodape
  da pagina, em vez de uma segunda barra dentro do cartao.
- **A terminologia da UI acompanhou.** Status, botoes, badge e o aviso de "sem registro de instalacao" do
  cartao passaram a usar a familia de recursos `Mode_*` — que ja existia, ja estava traduzida nos 30 idiomas
  e ja dizia "modo" em vez de "plugin". Nenhuma chave nova foi criada; `Removal_NoRecord_Hint`, que dizia
  "this plugin" e ficou sem uso, foi removida.
- **Nada mudou na instalacao, na remocao ou na verificacao.** `AmethystToolService`, `AmethystToolPlan`,
  `PluginRemovalService` e `InstallManifest` nao foram tocados: hash fail-closed, recusa de zip-slip, o
  handle pinado contra TOCTOU e a protecao de compartilhamento via `ClaimedByOthers` continuam iguais. O id
  de registro (`amethysttool`) tambem continua igual, entao instalacoes existentes seguem desinstalaveis.


### Desinstalacao de Modes

- **O cartao do Mode ativo ganhou botao Desinstalar.** A aba Modo so sabia sobrescrever: os arquivos de um
  Mode iam para a raiz da Steam e o unico vestigio era `settings.SelectedMode`. Agora instalar ou trocar de
  Mode grava uma entrada no `install-manifest.json` com os arquivos colocados, e desinstalar usa o MESMO
  registro, a MESMA politica de remocao e a MESMA pasta de backup que a aba Plugin.
- **Uma entrada de Mode por vez, com os restos carregados adiante.** Modes sao mutuamente exclusivos e
  sobrescrevem os nomes que compartilham. Uma entrada antiga sobrevivente continuaria reivindicando
  `dwmapi.dll` — e reivindicacao de "outra instalacao" e exatamente o que impede um arquivo de ser removido,
  o que deixaria tanto o Mode novo quanto o AmethystTool permanentemente indesinstalaveis. Ao instalar, as
  entradas de outros Modes sao dobradas na nova: os nomes que sobreviveram em disco (`OpenSteamTool.dll`,
  tipicamente) entram no registro do Mode atual, entao um desinstalar limpa a cadeia inteira em vez de
  abandonar um arquivo que ninguem assume.
- **O Mode ativo nao reivindica contra si mesmo.** `ClaimedByOthers` sempre somava os `PlaceFiles` do Mode
  ativo. Apontado para o proprio Mode ativo, isso marcaria cada arquivo dele como "ainda necessario a outra
  instalacao", removeria zero arquivos e reportaria sucesso — a falha que a regra de compartilhamento existe
  para evitar, virada para o alvo errado. A decisao virou funcao pura, `PluginRemoval.CombineClaims`.
- **Deteccao automatica nao grava registro.** Na primeira execucao sem Mode selecionado o app compara os
  hashes das DLLs com os releases publicados e adota o que casar. Isso prova o que os arquivos SAO, e nada
  sobre quem os colocou ali — entao nao ha registro, o botao fica desabilitado e o cartao explica o porque,
  em vez de remover por adivinhacao. Mesma doutrina do AmethystTool.
- **Tipo proprio para nao criar ciclo de DI.** `PluginRemovalService` ja depende de `UnlockerService` (para
  nao apagar as proxies do Mode ativo). Por isso a desinstalacao de Mode vive em `ModeRemovalService`, um
  orquestrador fino sobre os dois — o container rejeitaria o ciclo em `ValidateOnBuild`.
- **`SelectedMode` volta a "nenhum" so depois de os arquivos sairem.** Limpar antes deixaria um desinstalar
  falho reportando "sem Mode" com as DLLs ainda sendo carregadas pela `steam.exe`, e o registro que diz
  quais arquivos sao esses fora do alcance da UI. Nada muda no formato do `settings.json`: o campo sempre
  foi anulavel e o `null` sempre significou "nunca escolhido" — sem migracao.

### Escrita atomica do registro de instalacao

- **`File.WriteAllText` trocado por gravar-e-trocar.** O registro vai para um temporario IRMAO (mesmo
  volume, porque mover entre volumes e copia, e copia nao e atomica) e so entao entra no lugar via
  `File.Replace` — ou `File.Move` na primeira gravacao, quando nao ha o que substituir. `WriteAllText`
  trunca antes e preenche depois: uma queda nessa janela deixa um arquivo que le como vazio, e "nada esta
  registrado" e justamente o que faz o Desinstalar se recusar a tocar em arquivos que continuam na raiz da
  Steam. A escrita atomica impede que um crash desarme o recurso em silencio.
- **Falha nao destroi o registro anterior** e nao deixa temporario para tras; o retorno continua sendo
  `false` em vez de excecao, porque a instalacao ja aconteceu quando isso roda.
- **Nome de temporario unico por chamada** — o lock e por instancia, o arquivo nao.
- **`InstallManifestService` ganhou seam de diretorio** (ctor `internal`, padrao do `SettingsService`), para
  o caminho de escrita ser testavel sem sujar o registro de quem roda os testes.

### Correcoes e limpeza

- **Desinstalar do AmethystTool passava por fora do proprio servico.** A view model chamava
  `PluginRemovalService.RemoveAsync` direto, entao nem o back-fill de registro (instalacoes anteriores ao
  manifest) nem a limpeza do manifest de versao rodavam — o botao ficava habilitado para esses usuarios e
  respondia "sem registro". Agora vai por `AmethystToolService.UninstallAsync`.
- **`DescribeRemoval` saiu da view model para `RemovalMessage`**, compartilhado entre a aba Plugin e a aba
  Modo. Duas descricoes escritas a mao para um mesmo desfecho e como "removido" e "mantido porque outra
  coisa precisa" acabam com texto diferente dependendo do botao apertado.
- **Chave i18n `Plugin_Toast_Removed` removida** dos 30 `.resx` e do `Strings.Designer.cs`. Ficou orfa
  quando o texto foi trocado pela variante que menciona a Steam; um accessor sem chave faz o `Strings.Get`
  devolver o NOME da chave e a UI exibir "Plugin_Toast_Removed".
- **Documentado que a Steam nao e reaberta apos desinstalar** — README ganhou secao propria explicando que
  isso vale para os tres caminhos de desinstalacao e por que difere do instalar. Comportamento inalterado.

### Desinstalacao de plugins, e registro de instalacao

- **Botao Desinstalar no cartao do AmethystTool, e remocao segura para os dois plugins.** A remocao trabalha
  a partir de um REGISTRO DE INSTALACAO, nao de uma lista de nomes compilada. O registro novo fica em
  `%AppData%\LuaToolsGui\install-manifest.json` e guarda, por plugin: versao, data e quais arquivos foram
  colocados na raiz da Steam (com hash). Nao mexe em `settings.json`.
- **Arquivo que outra instalacao ainda usa NAO e removido.** Esse e o motivo de todo o mecanismo:
  `dwmapi.dll` e `xinput1_4.dll` sao colocados pelo AmethystTool E por tres dos unlockers da aba Modo. Com
  um Modo ativo, desinstalar o AmethystTool deixa esses dois no lugar e avisa; so o registro sai. Remover
  deixaria a Steam carregando um proxy cujo par sumiu. As reivindicacoes vem do manifest E do
  `SelectedMode` — Modes nao mantem manifest proprio, entao so o manifest nao bastaria.
- **Nada e apagado, e movido.** Tudo removido vai para `Removal-backup-<timestamp>\<plugin>\` dentro da
  pasta da Steam. Um desinstalar do qual o usuario se arrepende vira mover arquivo de volta.
- **Sem registro, sem remocao.** Se nao ha o que provar, o botao fica desabilitado com texto explicando —
  nunca remocao por adivinhacao. Instalacoes anteriores ao registro continuam funcionando: ha um
  back-fill estreito, permitido so quando o app tem evidencia propria de que instalou (os nomes dos slots
  do plugin de store-page, que nada mais neste app coloca; ou o manifest local do AmethystTool).
- **A Steam e parada e NAO e reaberta.** Os arquivos ficam travados com ela rodando, entao precisa cair;
  reabrir sozinha um cliente que ha um instante carregava uma DLL que agora nao existe mais nao e decisao
  do desinstalador. Muda o comportamento do desinstalar do plugin de store-page, que antes reabria — o
  toast agora diz que a Steam foi fechada.

### TOCTOU no PluginInstallerService

- **Retroportado o endurecimento que so o AmethystTool tinha.** Verificar, triar e usar eram tres aberturas
  separadas do mesmo caminho; em cada intervalo outro processo rodando como o mesmo usuario podia trocar o
  arquivo, e os bytes que a `steam.exe` carrega nao seriam os bytes cujo digest foi conferido. Agora um
  handle e mantido aberto sobre toda a sequencia — para o zip E para cada DLL de slot, que tinha a mesma
  janela entre verificar e copiar para a raiz da Steam.
- **`FileShare.Read`, e a omissao importa mais que a inclusao.** Concede outros LEITORES (AssetIntegrity,
  FixAnalyzer e ZipFile abrem por caminho) e nega escrita e — por nao ter `FileShare.Delete` — exclusao e
  rename. O arquivo nao pode ser substituido, truncado nem movido por baixo do handle.
- **Centralizado em `AssetIntegrity.OpenPinned`**, usado pelos dois instaladores, com testes de regressao
  sobre o mecanismo em si.


### Instalacao automatica do AmethystTool

- **A aba Plugin ganhou um segundo cartao: AmethystTool.** E o fork do BetterSteamTools mantido junto
  deste app, e um plugin de injecao NATIVO — `dwmapi.dll` e `xinput1_4.dll` sao proxies que a `steam.exe`
  carrega pelo nome e que encaminham para `AmethystTool.dll`. Tudo vai para a RAIZ da Steam. O botao baixa
  o release, verifica, extrai e instala; a Steam e parada para a copia e reaberta depois, porque essas DLLs
  ficam travadas enquanto ela roda.
- **Quatro arquivos, nunca mais que isso.** O zip do release tambem traz `INSTALL.txt`, `README.md`,
  `RELEASE_NOTES.md` e `TESTING.md`. A lista instalada e uma ALLOW-LIST (`AmethystToolPlan.PayloadFiles`),
  entao documentacao nao chega na pasta da Steam e um arquivo que um release futuro venha a adicionar e
  ignorado por padrao, em vez de instalado por padrao.
- **Nada e sobrescrito sem copia antes.** Se `dwmapi.dll` ou `xinput1_4.dll` ja existir — outro tool e dono
  dele, ou e reinstalacao — o arquivo atual e MOVIDO para `AmethystTool-backup-<timestamp>\` dentro da
  pasta da Steam ANTES de a substituicao ser escrita, e o cartao diz para onde foi. Vale para o
  `amethysttool.toml` tambem: reinstalar troca a config, e a anterior fica na pasta de backup. Uma DLL
  proxy sobrescrita as cegas quebra a Steam de um jeito que o usuario nao desfaz.
- **Verificacao fail-closed, sem valvula de escape.** O SHA-256 que o GitHub publica para o asset e
  obrigatorio; digest ausente, malformado ou divergente PARA a instalacao. Diferente do Steamless, aqui nao
  ha hash pinado de fallback — o release ja publica digest, e um fallback so criaria um caminho em volta da
  checagem. A URL do asset e pinada em `ArthurS357/BetterSteamTools-Amethyst` via
  `GithubProxy.IsAssetUrlForRepo`, entao um mirror hostil da API nao pode apontar para o payload de outro
  repositorio e entregar o hash correspondente.
- **A decisao ficou separada da escrita.** `AmethystToolPlan` e politica pura sobre strings — o que copiar,
  para onde, o que precisa de backup — e `AmethystToolService` so executa. E o que torna as tres garantias
  acima testaveis com uma pasta temporaria, sem Steam e sem rede.


## 1.5.4 — 2026-08-22

Duas coisas nesta versao: um botao **Jogar** na aba Gerenciar, e a base do app migrada de **.NET 8 para
.NET 10 LTS**. Nao houve release 1.5.3 — a numeracao pula de 1.5.2 para 1.5.4.

### Jogar / Instalar

- **Cada jogo na aba Gerenciar ganhou um botao de acao.** Ate aqui o app configurava manifests e lua para
  um jogo e depois deixava o usuario ir procurar esse mesmo jogo na Steam para abri-lo. O botao fecha esse
  vao: o jogo configurado e o jogo iniciado passam a ser a mesma linha da tela.
- **O rotulo diz o que vai acontecer.** Com os arquivos em disco le **Jogar** e dispara
  `steam://rungameid/<appid>`; sem os arquivos le **Instalar** e dispara `steam://install/<appid>`, que
  abre o download na Steam. Rotulo e acao saem da MESMA regra (`SteamLaunchPolicy.IntentFor`) de proposito
  — uma ViewModel que re-derivasse "diga Instalar quando nao instalado" por conta propria fica a uma
  edicao de prometer uma coisa e fazer outra.
- **Terceiro estado explicito: `Unknown`.** Quando a Steam nao e localizada, a biblioteca esta ILEGIVEL, e
  isso nao e sinonimo de "li e o jogo nao esta la". `Unknown` resolve para **Jogar**, nao para uma recusa:
  `steam://rungameid/` se autocorrige — a propria Steam responde com o prompt de instalacao se o jogo e
  possuido e ausente, e com a pagina da loja se nao e possuido. Recusar deixaria o usuario preso atras de
  um botao morto exatamente no caso em que quem esta em duvida e o app.
- **A Steam e iniciada antes, quando nao esta de pe.** Uma URL `steam://` enviada a um cliente morto e
  descartada em silencio, o que chega ao usuario como "o botao nao fez nada". O adaptador espera o processo
  aparecer (timeout de 45s, poll de 500ms) e ainda aguarda 3s de acomodacao: o processo existir nao e o
  mesmo que o cliente conseguir atender a URL, porque steam.exe registra o endpoint de IPC alguns segundos
  depois de subir. A espera so e paga quando a Steam precisou ser iniciada.
- **Politica pura separada do adaptador.** `SteamLaunchPolicy` decide (`SteamLaunchPlan`) sem tocar em
  processo, disco ou registro; `SteamGameLauncher` reune os dois fatos e executa. A decisao inteira e
  testavel sem Steam, sem WPF e sem I/O — e o que os testes novos de `SteamLaunchPolicyTests` cobrem.
- **`SteamProtocolUri` e a fronteira de seguranca do botao.** A URL chega em `Process.Start` com
  `UseShellExecute = true`, ou seja, o shell do Windows resolve o que estiver nela. Por isso o appid e
  validado como `long` numa faixa fechada (`0 < id < 2_000_000_000`) em vez de passar adiante como string:
  um `long` validado so consegue se renderizar como digitos, entao o resultado interpolado e comprovadamente
  uma URL `steam://` bem formada — nao existe string que sobreviva ao parse para numero e ainda carregue
  aspas, espaco, troca de esquema ou um segundo argumento. Appid invalido devolve `null`, e `null` significa
  **nao iniciar processo nenhum**; nunca ha fallback para string crua. O teto de 2 bilhoes casa com o guarda
  do `SteamLinkParser` e exclui os valores compostos de 64 bits que `rungameid` usa para atalhos non-Steam.
- **Falhas sao casos distintos, nao um bool.** `SteamLaunchOutcome` separa `SteamUnavailable`,
  `InvalidAppId` e `Failed`, porque "a Steam nao foi encontrada" e "esse id de jogo nao e valido" sao
  problemas diferentes com solucoes diferentes. Sete chaves novas em `Strings` cobrem os rotulos e avisos,
  presentes nos 30 arquivos de idioma.

### Migracao para .NET 10 LTS

- **TFM de `net8.0-windows` para `net10.0-windows`**, no app e no projeto de testes (o teste referencia o
  WinExe WPF, entao o TFM precisa casar). Motivo e suporte: o .NET 8 sai de suporte em **novembro de 2026**
  e para de receber correcao de seguranca; o .NET 10 e LTS ate **novembro de 2028**. Adiar a troca so
  encurta a janela em que ela pode ser feita com calma.
- **`System.Security.Cryptography.ProtectedData` saiu do csproj.** Sob `net10.0-windows` o tipo ja vem com
  o framework, e o NuGet levanta **NU1510** para a referencia redundante — que o `TreatWarningsAsErrors` do
  `Directory.Build.props` transforma em restore quebrado. Foi a unica alteracao que a migracao EXIGIU.
  O DPAPI (`AuthService`, `SettingsService`) nao muda: verificado que o tipo carrega de
  `shared\Microsoft.WindowsDesktop.App\10.0.11\` e que o round-trip funciona, e o blob continua no formato
  Win32 `CryptProtectData` padrao — ou seja, chave Hubcap e tokens gravados pelo build .NET 8 seguem
  legiveis, sem migracao de dado.
  **O detalhe que importa:** ele vem do framework do WINDOWS DESKTOP, nao do base. Um projeto
  `net10.0-windows` sem `UseWPF`/`UseWindowsForms` nem compila contra ele (CS1069, tipo encaminhado). Logo,
  tirar `UseWPF` ou `UseWindowsForms` deste projeto quebraria o armazenamento de credencial, e nao so a UI.
  Ficou registrado no comentario do csproj.
- **`Microsoft.Extensions.Hosting` 10.0.9 para 10.0.11**, alinhando com o runtime 10.0.11. Foi a UNICA
  subida de versao de pacote da migracao. `WPF-UI` continua pinado exato em `[4.3.0]` — o tema Amethyst
  depende de chaves internas de recurso dessa versao — e `Velopack 1.2.0` nao foi tocado para nao mexer no
  auto-update. `VirtualizingWrapPanel`, `xunit`, `AwesomeAssertions` e `Microsoft.NET.Test.Sdk` tem versoes
  mais novas disponiveis e foram deixados como estao: nenhum deles bloqueia o .NET 10, e um salto de major
  no meio de uma migracao de runtime junta duas causas de falha numa mudanca so.
- **`LangVersion` continua sem ser declarado, de proposito.** Nao existia antes e continua nao existindo: o
  default segue o TFM, entao a migracao ja levou o compilador de **C# 12 para C# 14** sem nenhuma linha
  nova. Declarar `14.0` explicitamente seria um pin sem beneficio hoje e uma trava a mais para remover na
  proxima migracao.
- **Zero mudanca de codigo exigida pela migracao.** Fora a remocao do `PackageReference`, nada precisou ser
  adaptado. O build passa com `TreatWarningsAsErrors=true` e **0 avisos**, e os testes existentes passam sem
  alteracao — inclusive os que pinam as invariantes de seguranca (`LocalApiAccessPolicy`, `AssetIntegrity`,
  `AuthService.StateMatches`, `GithubProxy`, `LuaManifestValidator`). `BinaryFormatter`, removido no
  .NET 9, nao era usado em lugar nenhum.
- **`RollForward=LatestMajor` mantido no projeto de testes** pelo mesmo motivo de sempre: nao custa nada e
  impede que um runtime ausente na maquina seja a razao pela qual a suite nem roda.
- **CI e script de release acompanharam.** `build.yml` instala `10.0.x`; o fallback de TFM em
  `build-release.ps1` e a mensagem de erro que aponta o download do SDK foram atualizados. O caminho de
  publicacao nao precisou de nada: o script ja lia `TargetFramework` do projeto em vez de fixar o valor.
  Nem o `packId` do Velopack nem o instalador publicado foram tocados.
- **PENDENTE, fora deste repo: o `vpk pack`.** O passo de empacotamento e manual e nao esta versionado
  aqui, entao nada nesta mudanca o atualiza. Um build Velopack framework-dependent nomeia o runtime que o
  setup provisiona numa maquina limpa, e esse argumento precisa ir do desktop runtime do .NET 8 para o do
  .NET 10 (`--framework net10.0-x64-desktop`) — caso contrario o instalador entrega um runtime em que o
  binario 1.5.4 nao sobe. O `packId` continua o mesmo: ele e a chave de toda instalacao existente.
- **`xunit` 2.9.2 aparece como preterido** (`Legacy`, alternativa `xunit.v3`) no `dotnet list package
  --deprecated`. Nao foi migrado aqui de proposito: xunit v3 muda o modelo de execucao da suite inteira e
  nao tem relacao com o .NET 10 — a suite roda nele sem alteracao. Fica registrado como trabalho proprio.

### Formatacao e assinatura de codigo

- **`.editorconfig` (novo) e `dotnet format --verify-no-changes` como portao.** O arquivo e deliberadamente
  minimo: toda regra nele ja casava com 100% da base antes de ser escrita, entao adiciona-lo nao gerou uma
  unica violacao nova. `end_of_line` NAO e definido de proposito — o repo tem uma divisao real por arquivo
  (CRLF na maioria dos `.cs`, LF nos `Resources/*.resx` e numa minoria de `.cs`), e forcar uma convencao
  marcaria como violacao todo arquivo do outro lado. O comportamento padrao do `dotnet format`, detectar e
  preservar o final de linha de cada arquivo, e o que ja deixa os dois coexistirem.
- **Quatro correcoes de formatacao** para deixar o verificador verde: espaco em `[assembly: ThemeInfo(`,
  `[JsonIgnore]` em linha propria em `ApiModels.cs`, alinhamento manual desfeito em `HomeViewModel.cs` e um
  inicializador de objeto quebrado por linha em `LaunchModStoreTests.cs`. Nenhuma delas muda comportamento.
- **Assinatura de codigo documentada e ligada.** `build-release.ps1` assina `LuaTools.exe` com `signtool`
  quando `CERTIFICATE_PATH`/`CERTIFICATE_PASSWORD` estao definidos, e avisa em vez de pular em silencio
  quando nao estao — releases continuam saindo sem assinatura por padrao, exatamente como antes. O
  certificado vem de variavel de ambiente e nao de parametro para que o valor nunca caia no historico do
  shell nem na invocacao registrada de um job de CI. Se o certificado ESTA configurado mas o `signtool` nao
  e encontrado, o script FALHA em vez de produzir um binario sem assinatura: cair para o artefato nao
  assinado entregaria em silencio exatamente aquilo que configurar o certificado existe para evitar. O
  README ganhou a secao "Code signing" com os requisitos reais — por que um certificado self-signed ou DV
  nao resolve o SmartScreen, e por que o fluxo `/f`+`/p` deste script nao cobre EV com token de hardware.

## 1.5.2 — 2026-08-19

Fechar a janela deixa de matar o app: o X manda para a bandeja e o bridge local do plugin continua
de pe. Mais os follow-ups de baixo risco que estavam registrados desde 1.5.1.

### Bandeja do sistema

- **Fechar a janela nao encerra mais o app.** O X passa a esconder a janela na bandeja e o processo
  continua vivo; a unica saida de verdade e o item **Sair** do menu da bandeja. Antes o comportamento
  existia mas atras da opcao "Minimizar para a bandeja", **desligada por padrao** — ou seja, para quase
  todo mundo o X matava o app e junto com ele o bridge HTTP local que o plugin da store consulta. A
  integracao com a pagina da Steam simplesmente parava de responder, sem nada na tela dizendo por que.
  O padrao virou LIGADO. Quem prefere o antigo desliga a opcao em Configuracoes e a escolha e persistida.
- **Icone da bandeja com identidade propria.** Tooltip e titulo do balao passam a usar
  `Strings.App_DisplayName` ("LuaTools Amethyst") em vez do literal "LuaTools", e o menu continua com
  **Abrir** e **Sair** (ja traduzidos nos 29 idiomas). Duplo clique restaura a janela.
- **A regra saiu do code-behind.** `TrayService` (novo) e dono do icone e da decisao fechar-vs-encerrar,
  contra duas interfaces (`ITrayIcon`, `ITrayWindow`); `NotifyIconTray` e o unico adaptador WinForms e nao
  guarda logica. `MainWindow` virou o `ITrayWindow` que o servico dirige. Antes eram tres condicoes
  embutidas no handler `Closing`, alcancaveis so com uma janela real — a regra com mais chance de irritar
  o usuario era a unica sem teste nenhum.
- **`Dispose` do `NotifyIcon` garantido e idempotente.** Icone nao descartado fica morto na area de
  notificacao ate o usuario passar o mouse por cima.
- **Instalacao silenciosa continua se auto-encerrando.** `luatools://install/silent/<id>` e disparavel por
  qualquer pagina web. Com o padrao invertido, a condicao antiga (`!MinimizeToTray`) nunca mais seria
  verdadeira e cada instalacao dessas deixaria um processo residente que o usuario nunca pediu. Entrou
  `SettingsService.WantsResidentTrayApp`, que pergunta o mais estreito: o usuario **escolheu** manter o app
  na bandeja? Sem escolha registrada, a instalacao silenciosa limpa a propria sujeira.

### Follow-ups executados

- **`Settings_HubcapKeyBad` mencionava `(smm_…)` nos 30 arquivos de idioma.** A validacao ja tinha sido
  afrouxada em 1.5.1 (prefixo opcional, corpo hex 16–256), mas a mensagem de recusa continuou anunciando um
  formato que o codigo nao exige mais. Mensagem mais estrita que a checagem e pior que mensagem vaga: manda
  o usuario cacar uma chave que nunca foi o problema. O parentese foi removido de todos os idiomas. O
  **placeholder** do campo (`Settings_HubcapKeyPlaceholder`) mantem o `smm_…` de proposito: e dica sobre o
  que colar num campo vazio, nao veredito sobre o que foi digitado.
- **`PluginLog` → `AppLog`.** Ha muito tempo nao era so o log do bridge: resolvedor de auto-update, tela de
  seguranca de fixes/manifestos, aviso de DPAPI indisponivel, aviso de privacidade do lookup em texto claro
  e a checagem manual de update do About escrevem ali. O nome dizia "log do plugin", que e como um mantenedor
  decide **nao** olhar nele atras de um problema de update. O **arquivo** continua `plugin-backend.log` de
  proposito — e o que o README e as respostas de suporte mandam enviar, e o que a geracao rotacionada `.1`
  ja se chama em disco.
- **DTOs de API viraram `init`-only** (`ApiModels.cs`, `FixModels.cs`, `ModeModels.cs` — 96 propriedades).
  Sao alvos de desserializacao: o `System.Text.Json` preenche uma vez e nada no app tem o que escrever
  depois. A conversao nao quebrou **nenhum** call site, que e justamente o argumento — a propriedade estava
  aberta a toa. `init` em `class`, deliberadamente **nao** `record`: `record` tambem trocaria igualdade de
  referencia por estrutural, e esses tipos vivem em caches e colecoes que nunca pediram semantica de valor.

### Primeira impressao

- **A primeira linha da primeira tela chamava o app pelo nome errado.** `Home_Welcome` dizia "Bem-vindo ao
  LuaTools" nos 30 idiomas, enquanto o titulo da janela, a aba About e o README dizem **LuaTools Amethyst**
  — e o README manda o usuario identificar o build por exatamente essas superficies ("Checking which build
  you are running"). A saudacao contradizia em silencio a propria checagem de identidade do fork. Agora
  nomeia o fork em todos os idiomas; "Amethyst" e nome de produto e nao se traduz, entao a frase traduzida
  segue intacta.
- **`Plugin` no menu lateral era literal em ingles.** Os outros oito itens do rail leem de `Strings`; esse
  estava fixo no `MainWindow.xaml`. Numa UI traduzida ficava exatamente uma palavra em ingles no primeiro
  elemento que o usuario olha. Virou `Nav_Plugin`, registrada em `PENDING_TRANSLATION` — sem traducao
  automatica, conforme a regra que vale para as outras 84 chaves.

Fora isso, a abertura foi avaliada e **nao** recebeu mudancas: a janela ja pinta o fundo da paleta antes do
primeiro frame (o `Backdrop=None` de 1.3.0), cada bloco do Home ja abre com estado provisorio proprio
("Verificando a Steam...", "Verificando...", "Nenhum modo selecionado") em vez de vazio, o strip de
"Adicionados recentemente" se esconde quando nao ha nada em vez de mostrar area vazia, e a sequencia de
startup ja narra o que faz por toast. Nao havia defeito concreto ali para justificar mexer.

### Follow-ups avaliados e nao implementados

- **`file_modified` para detectar manifesto desatualizado.** `FileModified` e `NeedsUpdate` ja sao lidos e
  guardados, e nada os consome — a pagina Manage ainda so sabe dizer "disponivel". A metade que falta e
  **local**: o app nao registra data de instalacao nem versao de origem de um manifesto, entao nao ha com o
  que comparar a data do HubCap. O `File.SetLastWriteTime` que o `LuaInstaller` carimba e a hora da
  **escrita**, nao do manifesto que o HubCap gerou, e e reescrito por qualquer reinstalacao — comparar
  aquilo reportaria "desatualizado" para copia atual e "atual" para copia velha. Fazer certo exige persistir
  o `file_modified` da origem junto de cada manifesto instalado, mais uma decisao de fuso sobre um valor que
  a API manda sem offset. E feature, nao limpeza; registrado no XML doc de `HubcapManifestStatus`.
- **Traducao das chaves `PENDING_TRANSLATION`.** 84 chaves seguem so em ingles por decisao, e nao foram
  traduzidas automaticamente. `check-i18n.py` continua listando todas a cada execucao.

### Testes

- Suite de **815 para 843**.
- `TrayServiceTests` (novo, 13): fechar comum esconde na bandeja e **nao** encerra; fechar encerra quando
  nada mantem o app residente; a opcao e lida a cada fechamento, nao capturada no construtor (ela muda em
  tempo de execucao, e `SessionTrayLock` chega por sinal de outra instancia); **Sair** encerra de verdade
  mesmo com fechar-para-bandeja ligado; `Dispose` acontece uma vez e so no caminho de encerramento; duplo
  clique e o item **Abrir** restauram pelo mesmo caminho; restaurar nao conta como pedido de saida; launch
  silencioso mostra o icone sem tocar na janela; e a tabela de decisao completa (4 combinacoes).
- `HubcapKeyMessageTests` (novo, 3): nenhum dos 30 RESX pode citar `smm` na recusa; todo idioma continua
  com um texto de recusa nao vazio (apagar o valor faria o app exibir o **nome da chave** e o
  `check-i18n.py`, que so compara conjuntos de chaves, ainda passaria); e o placeholder segue permitido.
- `ApiModelImmutabilityTests` (novo, 3): varredura por reflexao no namespace `Models` — nenhuma propriedade
  publica com `set` (distinguido de `init` pelo modificador `IsExternalInit`), a varredura de fato encontra
  os DTOs (senao um rename de namespace vira teste que aprova para sempre), e `init` continua permitindo a
  desserializacao.
- `PersistenceTests`: o default de `MinimizeToTray` passou a ser `true`, com `WantsResidentTrayApp` ainda
  `false` — quem nunca escolheu nada nao vira app residente numa instalacao silenciosa.
- `ShellIdentityTests` (novo, 5): a saudacao nomeia o fork nos 30 idiomas; o titulo da janela continua
  ligado a `App_DisplayName` em vez de literal; **nenhum** item do rail pode ter `Content=` literal (pega o
  proximo adicionado com pressa); o item Plugin le de `Nav_Plugin`; e a chave existe de fato — sem ela
  `Strings.Get` devolveria o **nome** da chave e o rail exibiria "Nav_Plugin".

## 1.5.1 — 2026-08-18

Correcao da troca de cor — que nunca chegou a funcionar — agora atras de um botao **Aplicar**, mais a
remocao de jogos dos Depots e os follow-ups de UX/seguranca do HubCap.

### Correcoes

- **Login com Discord nunca completava.** O app passava `&state=` para `/auth/v1/authorize` do Supabase.
  Esse parametro nao e do chamador: o Supabase **cunha** o proprio `state` e usa esse valor como chave da
  linha de flow-state, que e o que carrega o destino do redirect atraves da ida ao Discord e da volta.
  Mandar o nosso sobrescrevia essa chave. Sondado contra o endpoint ao vivo:

  ```
  com    &state=probe  ->  discord.com/...&state=probe
  sem    &state=       ->  discord.com/...&state=e4631a5c-2b82-4b95-8e5f-1729b53dbb65
  ```

  O Discord devolve ao Supabase apenas esse `state` e mais nada, entao sobrescreve-lo apaga o unico
  identificador do flow pendente. O redirect de volta para `http://localhost:53789/callback` nunca
  acontecia, o listener local esperava os 5 minutos inteiros e o usuario via "sign-in timed out" — o
  "botao do Discord nao loga de verdade". Correcao: remover o parametro.

  O nonce nao levava protecao junto. Quem liga o resgate a este cliente e o PKCE: um code injetado no
  listener por outro processo foi emitido contra outro `code_challenge`, entao a troca apresenta o nosso
  verifier e o Supabase recusa — no servidor, que e o lugar certo para recusar. O guard `StateMatches`
  virou codigo morto e foi removido junto com seus testes; no lugar entrou um teste que exige a AUSENCIA
  do parametro, porque reintroduzi-lo parece endurecimento e derruba o recurso inteiro.
- **Mensagem de timeout do login** passou a nomear o caminho que funciona (`/login` no servidor, colar o
  codigo em Configuracoes) em vez de deixar o usuario num beco sem saida.


- **A troca de cor de destaque nao funcionava.** Dois defeitos independentes, ambos silenciosos: build
  limpo, nenhuma excecao, nenhum log.

  1. **Brushes congelados.** O WPF congela todo `Freezable` no momento em que o `ResourceDictionary` passa
     a ser propriedade de `Application.Resources`. `App.RepaintAccentBrushes` e `ThemeRepaint.Apply`
     mutam `brush.Color` e pulam o que estiver congelado (`!brush.IsFrozen`) — ou seja, pulavam
     **tudo**. Medido: `SurfaceBaseBrush IsFrozen=True` com o dicionario anexado, `False` solto. Foi
     exatamente essa diferenca que fez os testes anteriores passarem contra um dicionario avulso enquanto
     o app nao trocava de cor. Correcao: em `Themes/Colors.xaml` cada brush da paleta declara a cor via
     `Color="{DynamicResource ...}"`; uma referencia dinamica torna o `Freezable` nao-congelavel, e a
     mutacao volta a valer. 49 brushes convertidos, com 23 chaves `Color` semente novas para os tokens
     translucidos. As cores de status ficam de fora de proposito — nao seguem o accent.
  2. **`PromoteSurfaceOverrides` recongelava.** Atribuir um `Freezable` dentro de `Application.Resources`
     tambem congela. A funcao promovia Colors **e** Brushes, entao a primeira troca funcionava e todas as
     seguintes nao mexiam mais nas superficies do WPF-UI. Correcao: promover **somente** chaves `Color`.
     Os brushes nao precisam de promocao — `Colors.xaml` e o ultimo merge e ja vence a resolucao.

### Mudancas

- **Fluxo de inicializacao virou sequencia declarada.** Antes nao havia sequencia: o Steam ficava aberto,
  o overlay de primeira execucao aparecia por cima, e o Steam era parado e reiniciado la dentro de
  qualquer instalador que rodasse. Quem ja tinha tudo instalado recebia prompts que nao levavam a lugar
  nenhum. Agora: **fecha o Steam -> roda o setup so se houver setup -> oferece o Steam de volta**. A
  decisao virou `StartupPlan.Decide`, testavel sem Application, sem instalador e sem Steam real.
  - Setup so aparece quando ha o que instalar. Quem ja passou por ele, ou simplesmente ja tem as
    ferramentas (reinstalacao, segunda maquina, maquina de dev), vai direto ao ponto.
  - Login **nao** entra nessa decisao: navegar como convidado e suportado no resto do app, entao exigir
    conta poria o instalador na frente de um convidado que ja tem tudo — o proprio incomodo removido.
  - `ShowSetup` e `OfferReopen` nunca vem juntos: o setup ja inicia o Steam ao terminar, e dois caminhos
    disputando o lancamento produzem o dialogo "Steam is already running".
- **Steam e pedido antes de ser forcado.** `StopSteam` era `Kill(entireProcessTree: true)` direto, em
  todo caminho — instalacao de plugin, troca de modo, onboarding. Terminar o cliente nega a ele a chance
  de gravar a config, que e o que produz o "Steam did not shut down correctly" no lancamento seguinte.
  Agora: `CloseMainWindow` com 10s de tolerancia, escalando so se autorizado. Na inicializacao a
  escalada **nao** e automatica — se o Steam nao fechar, o usuario decide. Os instaladores continuam
  com `allowKill: true`, porque para eles "o Steam esta parado" nao e negociavel.
  - O resultado e um enum (`NotRunning`/`ClosedGracefully`/`Killed`/`StillRunning`) e reporta o que e
    **verdade**, nao o que foi tentado: um kill pode falhar, e um chamador informado "Killed" iria
    reescrever arquivos que o Steam ainda mantem abertos.

### Desempenho

- **Deteccao do caminho do Steam memoizada.** `AutoDetectedPath` reabria ate tres chaves de registro mais
  um `File.Exists` a **cada leitura** — e `EffectivePath`/`StPlugInDir` sao lidos de 28 lugares, sendo os
  que importam por jogo. Atualizar a lista de Depots resolvia o caminho uma vez por titulo, entao uma
  biblioteca de 200 jogos fazia da ordem de 600 aberturas de registro para responder sempre a mesma
  coisa. O cache e revalidado (so e reusado enquanto ainda aponta para um `steam.exe` real) e `null`
  nunca e cacheado, para que uma instalacao que aparece depois seja notada.
- Varredura de async nao encontrou nada a corrigir: zero `.Result`, `.Wait()` ou
  `GetAwaiter().GetResult()` no codigo do app, `async void` so nos dois pontos ja documentados, e os
  `HttpClient` ja sao de vida longa. Nada foi mexido a toa.

- **Botao Aplicar para a cor de destaque.** Selecionar no combo passou a apenas *encenar* a escolha; nada
  e pintado nem gravado ate clicar em **Aplicar**. Se o usuario mudar de ideia e sair da tela, a cor
  anterior continua ativa e o `settings.json` fica intacto. O botao fica desabilitado quando o que esta
  selecionado ja e o que esta pintado, entao "nada a aplicar" se le no controle em vez de ser descoberto
  clicando. Evita a repintura acidental de encostar no dropdown.
- **Paleta tonal.** Escolher uma cor retinge o app inteiro — janela, cartoes, dialogos, bordas, textos
  auxiliares — e nao so os botoes. Cada paleta ganhou uma rampa neutra de 11 passos (`Plum`, `Moss`,
  `Wine`). As rampas alternativas sao derivadas por **luminancia relativa**, nao por lightness HSL: cada
  passo tem a mesma luminancia do passo Amethyst que substitui, entao todo contraste WCAG homologado
  transfere sem recalculo. Rotacao por lightness igual foi testada e descartada — verde no mesmo L e
  mais claro, e derrubava Danger sobre chip inset para 3,92:1 (reprova AA).
- **Remocao de jogo dos Depots.** A lista e a uniao de tres fontes em disco (lua vivo em `stplug-in`,
  build luas soltos, e a pasta do vault), e qualquer uma delas bastava para o jogo continuar aparecendo
  — por isso apagar pela pagina Manage nao resolvia: o vault devolvia o jogo. `LuaVault.ForgetGame`
  limpa as tres, com confirmacao modal e um icone discreto em cada linha (revelado no hover e tambem no
  foco de teclado). Persistencia e o proprio sistema de arquivos, entao a remocao sobrevive ao restart.
- **HubCap — aviso de expiracao da chave.** `api_key_expires_at` ja chegava e so aparecia como data no
  fim da linha de uso; agora vira aviso proprio quando faltam 7 dias ou menos. Descoberto no caminho: o
  campo vem **sem offset de fuso**, e era lido como hora local (ate 26h de erro entre usuarios). Passou a
  ser lido como UTC nos dois pontos que o consomem.
- **HubCap — campo da chave mascarado.** `ui:TextBox` trocado por `ui:PasswordBox`: a chave e uma
  credencial bearer e era digitada/colada em texto claro na tela.
- **HubCap — validacao de formato mais tolerante.** `^smm_[0-9a-f]{96}$` era uma aposta no formato atual
  de uma credencial de terceiro; se o HubCap rotacionasse o prefixo, o app recusaria localmente uma chave
  valida com uma mensagem indistinguivel de erro de digitacao. Agora aceita prefixo opcional e corpo hex
  de 16 a 256 caracteres, ancorado com `\A`/`\z` (em .NET `$` casa antes de `\n` final). `LogSanitizer`
  acompanhou o novo formato — chave que o app aceita enviar e chave que pode chegar num log.

### Testes

- Suite de **725 para 815**.
- `ThemeLiveSwitchTests` (novo): sobe uma `Application` real numa thread STA com os mesmos tres
  dicionarios do `App.xaml`. E o unico arranjo em que o congelamento existe — os testes antigos usavam
  dicionario avulso e por isso aprovavam um recurso inerte. Cobre: nenhum brush da paleta congelado,
  cores de status seguem congeladas, cada token repinta, **a segunda troca funciona como a primeira**,
  fundo da janela acompanha, e o slot de accent do WPF-UI acompanha.
- `SteamShutdownTests` / `StartupPlanTests` (novos): fecha antes de forcar; sem permissao um processo
  teimoso e **reportado**, nao forcado; kill que falha vira `StillRunning`; processo sem janela e
  escalado; todos sao pedidos antes de qualquer espera (a tolerancia e do usuario, nao por processo);
  e a matriz de decisao do launch, incluindo que setup e oferta-de-reabrir nunca coexistem.
- `AuthStateTests` (reescrito): a URL de authorize **nao** pode conter `state=`; PKCE e o redirect local
  continuam presentes; e a mensagem de timeout continua nomeando o caminho por codigo.
- `SteamPathResolutionTests` (novo): o override nunca e sombreado pelo cache, troca de override e vista
  na hora, limpar o override volta a deteccao, e leituras repetidas concordam.
- `AccentApplyButtonTests` (novo): selecionar nao pinta e nao grava; Aplicar pinta e grava; escolha
  aplicada sobrevive ao restart; botao desabilitado sem alteracao pendente; reabilita ao divergir;
  desabilita de novo apos aplicar; voltar para a cor ativa nao conta como pendente; `CanExecuteChanged`
  dispara (sem isso o botao nao redesenha); duas aplicacoes seguidas pintam duas vezes.

## 1.5.0 — 2026-08-18

Follow-ups da auditoria da integração HubCap, mais a primeira opção de personalização visual do fork.

### Novidades

- **Cor de destaque selecionável** em Configurações: Amethyst (roxo, padrão), Verde e Vermelho.
  A escolha é gravada em `settings.json` (`AccentColor`) e aplicada **sem reiniciar** — os brushes de
  accent são mutados no lugar, então as telas abertas repintam. As três rampas foram escolhidas por
  medição, não a olho: 300/400 acima de 4.5:1 sobre `SurfaceBase`, 500 acima de 3:1 (WCAG 1.4.11) e 600
  acima de 4.5:1 contra texto branco. O verde "óbvio" (`#16A34A`) alcançava só 3.3:1 com branco e foi
  descartado; o vermelho e o verde diretos colidiam com `SuccessText`/`Danger`, daí esmeralda e rosa.
- **Changelog dentro do app**, na aba Sobre. Embutido no binário — não lê `docs/CHANGELOG.md` nem busca
  na rede, então renderiza igual offline e não tem como falhar ao carregar.

### Melhorias

- **Mensagens de erro do HubCap traduzíveis.** As quatro strings que o usuário lê quando um download
  falha eram literais em inglês dentro do serviço; agora são chaves de recurso. As unidades de espera
  ("30 minutes") são chaves separadas, para idiomas que flexionam o substantivo por quantidade.
- **`HttpClient` do HubCap** passou a usar `SocketsHttpHandler` com `PooledConnectionLifetime` de 15
  minutos — uma conexão que nunca é reaberta nunca re-resolve DNS, e o HubCap fica atrás do Cloudflare.
  Toda requisição agora também se identifica com `User-Agent: LuaToolsAmethyst/<versão>`, derivado do
  assembly em vez de literal.
- A guarda de tema no startup passou a validar a rampa **ativa** em vez de assumir violeta, senão
  acusaria falha para quem escolhesse verde ou vermelho.

## 1.4.0 — 2026-08-16

Endurecimento pós-auditoria. Nenhuma funcionalidade nova; o tema desta versão é remover superfícies de
ataque que sobreviveram à 1.3.0 e tornar visível o que já era verificado em silêncio.

### ⚠️ Mudança de comportamento

- **`PluginAutoUpdate` agora vem DESLIGADO.** Antes, abrir o Steam com um plugin desatualizado baixava a
  nova release, **substituía `winmm.dll` na raiz do Steam** e reiniciava o Steam — sem prompt. É a ação
  mais poderosa do app e acontecia sem perguntar. A atualização continua alcançável pelo botão
  Install/Update na aba Plugin; ela deixou de ser silenciosa. Para o comportamento antigo:
  `"PluginAutoUpdate": true` em `settings.json`.

### Segurança

- **Injeção de comando eliminada nas junctions.** Três sítios montavam `cmd.exe /c mklink /j "{path}"` e
  `cmd.exe /c rmdir "{path}"` com o caminho interpolado na string de comando. Esse caminho deriva de
  `SteamPathOverride` no `settings.json` — arquivo que qualquer processo do usuário escreve — então uma
  aspa fechava o literal e o `cmd` executava o que viesse depois. O novo `DirectoryJunction` dirige o
  reparse point NTFS por `DeviceIoControl(FSCTL_SET_REPARSE_POINT)`: sem shell, sem string de comando,
  nada para escapar. A mitigação anterior (recusar caminho com aspa) foi removida por ter virado
  redundante.
  - Continua sendo **junction**, não symlink. Symlink de diretório exige admin ou Developer Mode; o app
    roda `asInvoker` e nunca eleva. `Directory.CreateSymbolicLink` teria quebrado o recurso para a maioria
    dos usuários.
  - A remoção usa `Directory.Delete(recursive: false)`, que corta o link sem descer no alvo. Coberto por
    teste com canário.
- **`plugin.zip` passa por triagem antes de extrair.** A checagem foi extraída para
  `PluginInstallerService.ScreenPluginArchive`, agora testável sem rede nem Steam.

### Transparência

- **Aviso antes de aplicar qualquer artefato de Modo ou Plugin**, mostrando origem (`owner/repo`), versão,
  SHA-256 e quais checagens passaram, com botão Cancelar e alguns segundos de carência. Cancelar não deixa
  meio-estado: nesse ponto tudo ainda está em pasta temporária.
  - O aviso é **advisory e falha aberto** por decisão explícita — os portões reais (pinagem, digest,
    triagem) já rodaram e já recusaram o que não puderam provar. Um toast quebrado não pode virar
    indisponibilidade funcional. Há teste fixando esse comportamento.

### Correções

- **`PluginAutoUpdate` não persistia.** A chave faltava no predicado `empty` de `SettingsService.SaveCore`,
  então um usuário cuja única alteração fosse ela teria o `settings.json` **apagado** e a escolha perdida
  no save seguinte. Introduzido na 1.3.0; encontrado pelo teste que fixa o novo default.
- **`plugin.zip` era recusado por usar `require`.** Instalar o BetterSteamTools falhava com
  `backend/main.lua: the lua contains 'require', which a Steam manifest never needs`. Erro de categoria: a
  triagem de `plugin.zip` aplicava as regras de **manifesto Steam** a código Lua de aplicação. Um manifesto
  é uma DSL minúscula (`addappid()`, `setManifestid()`), então a denylist dele proíbe `require`, `pcall`,
  `setmetatable`, `_G`, `rawget`/`rawset`, `collectgarbage`, `io.open` — tudo normal num programa Lua. Um
  plugin ficava impossível de instalar.

  `FixAnalyzer.AnalyzeArchive` passou a aceitar um `LuaScreeningProfile`. O padrão continua
  `SteamManifest`, então o fluxo de Correções não mudou em nada; o fluxo de Plugin usa `ApplicationCode`,
  cuja denylist é curta e só cobre execução: `os.execute`, `io.popen`, `package.loadlib`, `loadstring`,
  `load()` com argumento montado em tempo de execução, e `require`/`dofile`/`loadfile` apontando para URL.
  Ofuscação continua sendo detectada pela mesma passada de de-ofuscação.

  Escopo honesto, registrado no código: isso é defesa em profundidade, não fronteira de confiança. A mesma
  release entrega `winmm.dll`, que o steam.exe carrega — se a release for hostil, a DLL vence muito antes
  do Lua. Quem protege de fato é a pinagem de repositório e o digest fail-closed.

### Testes e ferramentas

- 442 → **536 testes xUnit**. Novos: `DirectoryJunctionTests` (16), `DownloadReviewTests` (17),
  `PluginArchiveScreeningTests` (22), `LuaCodeValidatorTests` (34), persistência de `PluginAutoUpdate` (2).
- **17 testes para `scripts/check-i18n.py`** (`unittest` da stdlib, sem dependência nova). O CI passou a
  rodá-los **antes** da validação de RESX: um checker quebrado reporta run limpo.
- Varredura confirmou zero literais de repositório fora do `AppConfig`.

### Documentação

- README: aviso pré-instalação, novo default de `PluginAutoUpdate`, procedimento de instalação manual
  quando uma verificação recusa, e duas pendências conhecidas registradas como tal — o último `cmd.exe`
  em `App.RelaunchApp` (não explorável: `Environment.ProcessPath` vem do SO e `"` não é caractere legal
  em caminho Windows) e a falha do `dotnet format` por ausência de `.editorconfig`.
- `docs/teste-manual-1.4.0.md`: roteiro de verificação em máquina real.

---

## 1.3.0 — LuaTools Amethyst

### Nome e identidade

- Projeto renomeado para **LuaTools Amethyst**, publicado em
  <https://github.com/ArthurS357/LuaTools_Amethyst>. Título da janela, aba **About** e metadados do
  assembly (`AssemblyTitle`/`Product`) usam o nome novo; a tag temporária "privacy fork" saiu do rodapé.
- A identidade **técnica** foi mantida de propósito: `AssemblyName` continua `LuaTools`, assim como
  `%AppData%\LuaToolsGui`, o mutex de instância única e o protocolo `luatools://`. O loader DLL inicia
  `LuaTools.exe` pelo nome e o Velopack chaveia a instalação nele — renomear órfãozaria toda instalação
  existente.

### Nova aba "About"

- Descreve o que é o fork, mostra a versão, **a fonte de update efetivamente em uso** (lida do
  `UpdateService`, não do `settings.json`, para não anunciar um repositório que o validador está
  ignorando), botão de verificação manual de updates e o caminho do `settings.json`.

### Auto-update

- `AppUpdateRepos` agora tem padrão compilado apontando para o repositório do próprio fork. Continua
  sobrescrevível pelo `settings.json`, e um array vazio desliga o update por completo.
- A trava contra repositórios oficiais segue ativa e é testada.

### Análise de correções (`FixAnalyzer`)

- **Corrigido zip-slip real** em `FixesViewModel.ApplyFix`: a extração fazia
  `Path.Combine(installDir, entry.FullName)` sem verificação de contenção — entrada `C:\Windows\...`
  escrevia lá, e `..\..\` saía da pasta do jogo. Agora há verificação no analisador **e** por entrada na
  extração.
- Novo `FixAnalyzer` roda antes de qualquer escrita: zip-slip, caminhos absolutos/UNC, destinos
  duplicados, contagem/tamanho/razão de compressão (zip bomb), lua perigoso — inclusive **ofuscado**
  (escapes `\xNN`/`\NNN`, concatenação de literais, indexação por string). Reutiliza o denylist do
  `LuaManifestValidator` em vez de duplicá-lo.
- Executáveis, arquivos aninhados e diretivas lua desconhecidas são **registrados, não bloqueados** —
  bloquear quebraria correções legítimas.

### Verificação de identidade do build

- `BuildIdentity` confere no startup se o assembly se declara `LuaTools Amethyst`. Se não, grava
  `BUILD:` no `crash.log` e mostra aviso não-bloqueante. Marcador positivo em vez de caça a resíduos do
  upstream — esta última dispararia no próprio fork, cujo código documenta os recursos removidos.

## 1.3.0 (base) — 2026-08-16

### Tema visual — paleta "Amethyst"

- Nova paleta roxa centralizada em `src/LuaToolsGui/Themes/Colors.xaml`, em duas camadas
  (primitivas `*Color` → semânticas `*Brush`). Substituiu **364 literais hexadecimais** espalhados
  por 13 arquivos XAML — nenhuma view carrega mais `#RRGGBB`.
- Superfícies, textos, bordas, estados de hover/pressed, selos de status e scrims agora saem de
  tokens nomeados por função (`SurfaceCardBrush`, `TextMutedBrush`, `AccentTintBrush`, …).
- O acento roxo também é injetado nos controles do WPF-UI (`App.ApplyAccentPalette`), para que a
  barra de navegação, botões primários e toggles não continuem usando o acento azul do Windows.
- Cores que viviam em ViewModels (`PluginStatusColor`, `HubcapKeyStatusColor`) passaram a guardar
  **chaves de recurso** em vez de hex, resolvidas pelo novo `ResourceKeyToBrushConverter`.
- Backdrop Mica desligado na janela principal: o tom do Mica vem do papel de parede do usuário, o
  que impedia garantir o fundo roxo.

### Acessibilidade

- Todos os tokens de texto foram medidos (WCAG 2.1) contra as superfícies reais renderizadas e
  passam em AA; a maioria em AAA. Pior caso: `TextDim` sobre card, 4.65:1.
- **Correção real de contraste:** o cinza `#6b7280` — a cor mais usada do app (51 ocorrências) —
  ficava em ~3.6:1 e reprovava em AA. Foi substituído por `TextDimBrush` `#978AB8` (6.17:1).

### Segurança / privacidade

- **DonateKeys permanece removido.** A reativação foi avaliada com sondagem TLS real do servidor e
  rejeitada por falta de suporte a HTTPS — ver `AppConfig.cs` e o relatório da entrega.
- **Fonte "Ryuu" em HTTP eliminada.** A URL `http://167.235.229.108/<appid>` era *dead data*: os campos
  `Url`/`SuccessCode` de `ApiSource` nunca eram lidos (o download resolve pelo NOME da fonte via proxy
  HTTPS do lua.tools). Os campos foram removidos, então a URL em claro deixou de existir no código.
- **Auto-update do app desligado por padrão.** `GithubReleasesRepos` apontava para o feed **oficial**:
  o fork acabaria baixando e instalando sozinho, em segundo plano, um build com telemetria e DonateKeys.
  Não há mais feed compilado — um build não configurado **não faz nenhuma requisição de update**. Para
  habilitar, defina `AppUpdateRepos` no `settings.json`. As entradas passam por `AppUpdateSources`, que
  exige `https://github.com/<owner>/<repo>`, **recusa `http://`** e **recusa os repositórios oficiais**
  em qualquer grafia (maiúsculas, barra final, `.git`, `www.`). Recusas vão para o `plugin-backend.log`
  com o motivo. Não afeta download de plugins/unlockers/manifests, que têm fontes próprias.
- **`check_apis` analisado e controlável.** É **somente metadados** (mapa nome-da-fonte → status); nunca
  transfere conteúdo — o download resolve a fonte pelo NOME e busca URL HTTPS assinada. Continua ligado
  por padrão porque **a lista de fontes que o usuário escolhe É a resposta dele**: desligar por padrão
  deixaria a maioria dos usuários sem fonte alguma. Agora há `"EnableSourceAvailabilityChecks": false`
  para não contatar o host, e `"InsecureMetadataNotice"` com `once` (padrão) / `always` / `off` para a
  frequência do aviso. Valor inválido cai para `once`, nunca para `off`.
- **Limpeza de desinstalação (`UninstallCleanup`)**, ligada ao hook `OnBeforeUninstall` do Velopack. O
  Velopack só apaga a pasta do app; ficavam para trás o *junction* do CDP (que mantinha o Steam abrindo
  a porta de depuração **não autenticada** 8080 para sempre), as DLLs loader na raiz do Steam, o
  registro do protocolo `luatools://` e o `%AppData%\LuaToolsGui` com o token e a chave de API.
- Nenhuma telemetria foi reintroduzida.

### Robustez do tema

- **WPF-UI fixado em `[4.3.0]`** (forma com colchetes: o NuGet recusa substituir a versão). O tema
  depende de nomes de recurso *internos* do WPF-UI, que não são contrato público.
- **Guarda de runtime no startup** (`App.VerifyAccentApplied`): confere se `SystemAccentColorPrimary`
  ficou com o roxo esperado. Se não, grava linha `THEME:` no `crash.log` e mostra aviso — a falha seria
  invisível de outro modo (nada lança, o app só volta ao cinza).
- Acento com **fonte única**: `ApplyAccentPalette` agora lê `Violet*Color` do dicionário em vez de
  repetir os hexadecimais.
- `Colors.xaml` enxuto: 6 tokens órfãos removidos; os demais documentados.

### Identificação do fork

- Rodapé e título da janela mostram `privacy fork` ao lado da versão, para distinguir do build oficial
  (instalar um release oficial por cima reintroduz telemetria e DonateKeys).

### Versão

- `1.2.8` → `1.3.0` em `LuaToolsGui.csproj` (`<Version>`), única fonte da versão: alimenta
  `AssemblyVersion`, `AssemblyFileVersion` e `AssemblyInformationalVersion`, que o
  `MainViewModel` lê para o rodapé do menu.
