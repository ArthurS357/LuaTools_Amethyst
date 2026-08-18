# Changelog

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
