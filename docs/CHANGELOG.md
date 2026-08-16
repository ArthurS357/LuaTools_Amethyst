# Changelog

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

### Testes e ferramentas

- 442 → **492 testes xUnit**. Novos: `DirectoryJunctionTests` (16), `DownloadReviewTests` (17),
  `PluginArchiveScreeningTests` (16), persistência de `PluginAutoUpdate` (2).
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
