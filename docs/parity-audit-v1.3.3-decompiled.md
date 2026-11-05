# Auditoria de paridade: upstream v1.3.3 (binário decompilado) vs. fork 1.7.13.1

Data: 2026-11-05. Base do fork: `a11b7b4`. Nada foi implementado nesta rodada.

## 1. Conclusão

- **O código público não contém a v1.3.3.** As tags `v1.3.2` e `v1.3.3` apontam para `9461259`. O assembly publicado declara o commit `c4d6d32`, que não existe no repositório público.
- **O binário confirma o changelog.** As cinco features existem no binário: Tools Page, Achievements, Stats, Playtime e Launch options.
- **Nenhum bug crítico a portar.** A comparação entre tag e binário não mostrou correção de segurança que falte no fork.
- **Sem sinal de malware em `LuaTools.dll`.** A análise foi estática, com os limites da seção 8. Há um comportamento de risco fora do fork: a substituição silenciosa do CloudRedirect (seção 5.6).
- **Recomendação:** começar por Launch options, que é pequeno e já tem o backend no fork. Depois fazer um spike em runtime antes de Playtime e Achievements/Stats por arquivo. A rota ao vivo depende de decisão sua, porque altera dados da conta na Valve.

## 2. Tabela de paridade

| Feature | Upstream (binário) | Fork | Relevância | Ação | Modelo sugerido |
|---|---|---|---|---|---|
| Launch options: "All overrides" e "Restore all" | Existe | Backend quase pronto, sem UI | Importante | Reimplementar | Sonnet, medium |
| Launch options: "Show in folder" | Existe | Só em Downloads | Importante | Reimplementar junto | Sonnet, medium |
| Playtime | Edição local de VDF | Não tem | Importante | Reimplementar após spike | Sonnet, medium |
| Achievements/Stats: leitura e gravação em arquivo | Existe | Não tem | Importante | Reimplementar após spike | Sonnet, high |
| Achievements/Stats: rota ao vivo | Processo auxiliar + cliente Steam | Não tem | Opcional, com decisão | Só com opt-in e spike aprovado | Opus, high |
| Tools Page | Aba de 5 seções sobre a página de Depots | Não tem | Importante, se houver ferramentas novas | Montar quando a primeira ferramenta entrar | Sonnet, medium |
| Remoção do OST | Remove e migra para BST | Card Nightly ainda ativo | Opcional, com decisão | Remover o card sem instalar BST | Sonnet, medium |
| CloudRedirect do fork deles | Existe, com troca automática | Removido | Opcional: conflita com a política | Não portar | Nenhum |
| "Check for updates" manual | Em Settings | Já existe em About | Ignorar | Nada | Nenhum |

## 3. Mudanças fora do changelog

| Mudança | Onde | Observação |
|---|---|---|
| Substituição automática do `cloud_redirect.dll` | `LuaToolsGui.Services/UnlockerService.cs:565` | Ver seção 5.6 |
| Sidebar comum de jogos na página Depots/Tools | `LuaToolsGui.Views/GameListSidebar.xaml` | Parte da montagem da Tools Page |
| Editor de launch options extraído para controle próprio | `LuaToolsGui.Views/LaunchOptionsEditor.xaml` | Permite embutir o editor numa aba |

A comparação foi heurística (seção 8), e o restante da diferença foi ruído de decompilação: tipos em arquivos separados e constantes concatenadas pelo compilador.

## 4. Permanece no upstream e continua fora do fork

- `AnalyticsService` dispara no launch: `LuaToolsGui/App.xaml.cs:470`.
- `DonateKeysService` dispara no launch (`App.xaml.cs:469`), com padrão ligado (`LuaToolsGui.Services/SettingsService.cs:62`) e destino HTTP em IP nu.
- `AssetHash.Matches` aceita arquivo quando não há digest: `LuaToolsGui.Services/AssetHash.cs:28-30`.
- `SteamAutoCrackService` continua no assembly.

## 5. Como cada feature funciona

### 5.1 Tools Page

- **Navegação:** o item passa a se chamar "Tools", mas aponta para a mesma `BuildsView` (`LuaToolsGui/MainWindow.xaml:45`).
- **Layout:** uma sidebar escolhe o jogo, e um `TabControl` mostra Depots, Achievements, Stats, Launch options e Playtime (`LuaToolsGui.Views/BuildsView.xaml:182-189`).
- **Estado:** `BuildsViewModel` expõe um `GameToolsViewModel` singleton (`LuaToolsGui.ViewModels/BuildsViewModel.cs:295`), que recarrega tudo quando o jogo ativo muda.

**Segurança de portar:** segura, é só UI. **No fork:** hoje o editor abre pela página Manage ([ManageView.xaml:819](../src/LuaToolsGui/Views/ManageView.xaml)). O custo real está em traduzir as strings novas para todos os idiomas.

### 5.2 Achievements e Stats

Não usa `Steamworks.NET` nem `steam_api64.dll`. Há duas rotas, escolhidas por `GameStatsService.SaveAsync` (`LuaToolsGui.Services.GameData/GameStatsService.cs:328`).

**Leitura (sempre local):**
- O schema vem de `appcache/stats/UserGameStatsSchema_<appid>.bin`, em VDF binário.
- Os valores vêm de `appcache/stats/UserGameStats_<conta>_<appid>.bin` (`GameStatsService.cs:80-98`).
- A conta vem de `config/loginusers.vdf`: o usuário com `MostRecent`, ou o timestamp mais recente (`SteamAccount.cs:46-71`).
- Se o schema não existir e o Steam estiver aberto, um pedido de edição vazio faz o Steam gerá-lo (`GameStatsService.cs:386-407`).
- Conquistas protegidas (`permission & 3`) ficam bloqueadas na UI (`AchievementRowViewModel.cs:87`).
- Ícones vêm do CDN `shared.akamai.steamstatic.com`, com cache em disco (`AchievementIconCache.cs`).

**Rota ao vivo (Steam aberto):**
- O app relança a si mesmo com `--stats-helper <appid>`, antes do mutex de instância única (`LuaToolsGui/Program.cs:46`). A troca de dados é JSON por stdin/stdout, com timeout de 30 s (`StatsHelperClient.cs:13-33`).
- O auxiliar define `SteamAppId`, carrega `steamclient64.dll` e resolve `CreateInterface`, `Steam_BGetCallback` e `Steam_FreeLastCallback` (`SAM.API/Steam.cs:88-116`, `SAM.API/Client.cs:34-69`).
- Depois chama a vtable de `ISteamUserStats013`: pede os stats, aplica as mudanças e chama `StoreStats`, aguardando callbacks por até 10 s (`StatsHelper.cs:42-118`).
- Se o Steam recusar, relê o arquivo por alguns instantes, porque a mudança pode já ter sido aplicada (`GameStatsService.cs:352-371`). Se continuar recusado, a UI oferece fechar o Steam e gravar em arquivo (`GameToolsViewModel.cs:762-766`).

**Rota por arquivo (Steam fechado):**
- Recusa gravar com o Steam aberto (`GameStatsService.cs:426-429`).
- Cria `.bak` só na primeira vez, altera os bits e `AchievementTimes`, zera `PendingChanges` e grava por arquivo temporário (`GameStatsService.cs:449-516`, `SAM.Game/KeyValue.cs:192-200`).

**Dependência do CloudRedirect:** se existir `cloud_redirect/stats/<conta>/<appid>.json`, o upstream atualiza esse JSON e `pending_edits.json`, e bloquear conquista exige reiniciar o Steam (`GameStatsService.cs:330-348`, `514-521`). O fork não deve escrever nesses arquivos. Se o JSON existir, a edição deve ser recusada com aviso, porque builds antigos podem ter deixado esses arquivos.

**Segurança de portar:**

| Parte | Avaliação | Motivo |
|---|---|---|
| Leitura local | Segura | Só lê arquivos do Steam. Limitar profundidade do parser, pois o do SAM recursa sem limite (`SAM.Game/KeyValue.cs`) |
| Gravação em arquivo | Segura para os dados locais, efeito incerto | Backup e troca atômica são bons. Não se sabe se o Steam mantém a edição (seção 7) |
| Rota ao vivo | Condicional | ABI por vtable em interfaces antigas, sem contrato estável. O processo separado contém crashes, e isso deve ser mantido |

**Risco de política na rota ao vivo:** `StoreStats` altera dados da conta nos servidores da Valve. É um fluxo novo de dados e exige opt-in, confirmação clara e declaração na release note. Consequências para a conta não aparecem no código e não foram avaliadas.

### 5.3 Playtime

- É só edição de arquivo: `userdata/<conta>/config/localconfig.vdf`, chave `UserLocalConfigStore/Software/Valve/Steam/apps/<appid>/Playtime`, em minutos (`PlaytimeService.cs:13`, `30`). Não há rede nem registro.
- Um tokenizador acha a posição exata do valor e substitui só esse trecho, preservando o resto do arquivo e o BOM. Se o jogo ainda não tem bloco, insere um (`PlaytimeService.cs:128-152`).
- Recusa gravar com o Steam aberto. A UI fecha o Steam, grava e reabre (`GameToolsViewModel.cs:814-827`).
- Cria `.bak` uma vez e grava por arquivo temporário (`PlaytimeService.cs:82-99`).
- Não altera `Playtime2wks` nem `LastPlayed`.

**Segurança de portar:** segura. É a feature de menor risco técnico depois de Launch options. **No fork:** `SteamConfigVdf.ParseVdf` ([SteamConfigVdf.cs:102](../src/LuaToolsGui/Services/SteamConfigVdf.cs)) lê uma árvore sem posições, então a edição precisa de um localizador novo e pequeno, coberto por testes.

### 5.4 Launch options

- **Estado:** `launchmods.json` guarda, por jogo, o original e o desejado (`LaunchModStore.cs`). O fork tem a mesma estrutura ([LaunchModStore.cs:59](../src/LuaToolsGui/Services/AppInfo/LaunchModStore.cs)).
- **All overrides:** novo diálogo que lista `Store.All`, com nome do jogo, entradas e botão de editar (`LaunchOverridesDialog.xaml.cs:37-52`).
- **Restore all:** novo método que aplica todos os originais em `appinfo.vdf` numa só gravação e, se der certo, remove os registros (`LaunchOptionsService.cs:106-118`). A UI pede confirmação (`LaunchOverridesDialog.xaml.cs:54-71`).
- **Show in folder:** abre a pasta de instalação do jogo (`LaunchOptionsViewModel.cs:505-511`).

**Segurança de portar:** segura. **No fork:** já existem `Reapply`, `Apply` e `FindDrifted` ([LaunchOptionsService.cs:112](../src/LuaToolsGui/Services/AppInfo/LaunchOptionsService.cs)). Faltam `RestoreAll`, o diálogo e o botão.

### 5.5 Migração do OpenSteamTools

- **Modos:** o binário tem só `Bst` e `Custom` (`LuaToolsGui.Models/UnlockerMode.cs:3-7`).
- **Detecção:** lê o valor salvo em settings, não os arquivos (`LuaToolsGui/App.xaml.cs:241`). `Ost`, `OpenSteamTools` e `OpenSteamToolsNightly` viram `Bst` (`ModeMigration.cs:28-34`).
- **Pendência:** quem estava em `Ost` recebe uma flag persistida (`App.xaml.cs:246-249`).
- **Remoção:** não há rotina própria. A migração reinstala o BST por cima dos mesmos arquivos e limpa a flag só em caso de sucesso, então uma falha é repetida no próximo launch (`UnlockerService.cs:232-248`).

**Segurança de portar:** não portar a migração automática. O fork trata o BST como terceiro com aviso de privacidade ([Changelog.cs:51](../src/LuaToolsGui/Resources/Changelog.cs)), e o onboarding instala só o ecossistema Amethyst ([OnboardingViewModel.cs:14](../src/LuaToolsGui/ViewModels/OnboardingViewModel.cs)). Instalar BST sem pedir violaria essa regra.

**Correção de premissa:** o card principal do fork já é BetterSteamTools ([AppConfig.cs:88](../src/LuaToolsGui/AppConfig.cs)). O ponto em aberto é o card Nightly, que aponta para `madoiscool/OST-Nightly` ([AppConfig.cs:93](../src/LuaToolsGui/AppConfig.cs)), sem push desde 2026-07-22.

**Recomendação:** remover o card Nightly com o padrão de lápide de `CloudRedirect` ([ActiveBackendPolicy.cs:69](../src/LuaToolsGui/Services/ActiveBackendPolicy.cs)). Quem usa o modo mantém os arquivos, vê um aviso e escolhe o próximo backend.

### 5.6 CloudRedirect

- **Origem:** `madoiscool/CloudRedirect` (`LuaToolsGui/AppConfig.cs:29`), fork de `crghofficial/cloudredirect`.
- **Alerta:** a cada launch, se `cloud_redirect.dll` existir na pasta do Steam e não contiver um texto específico com o nome do repositório, o app baixa a última release e substitui a DLL sem perguntar. O Steam carrega essa DLL no próximo início (`UnlockerService.cs:560-580`, `App.xaml.cs:115-116`).
- **Integridade:** o teste de "build do fork" é só busca de texto, não assinatura. O digest só é conferido quando o GitHub o informa (`UnlockerService.cs:481-482`).

Não parei a auditoria por isso: o changelog anuncia a troca de origem e não vi ocultação nem código hostil. Mesmo assim, é uma atualização forçada de DLL nativa de terceiro. O fork não tem esse código. **Não analisei a DLL do CloudRedirect.**

**Decisão:** manter a remoção. A existência do fork deles não muda a política. [Changelog.cs:48](../src/LuaToolsGui/Resources/Changelog.cs) diz que o projeto "no longer exists", o que é registro histórico e deixou de ser verdade. Vale não repetir essa frase em textos novos.

## 6. Erros do upstream a não repetir

| Problema | Onde | Como evitar |
|---|---|---|
| `ToDictionary` por nome sem deduplicar, fora do `try`: nome repetido no schema gera exceção | `GameStatsService.cs:442-443` | Deduplicar como a leitura já faz (`:137`) e tratar erro |
| Steam não reabre se ocorrer exceção antes do `StartSteam` | `GameToolsViewModel.cs:776-782` | `try/finally` |
| `RestoreAll` apaga registros de jogos que `Apply` pulou por não estarem no cache, perdendo o original | `LaunchOptionsService.cs:109-115`, `147-155` | Remover só o que foi realmente escrito |
| Rota ao vivo lê o caminho do registro, a de arquivo usa `EffectivePath`: divergem com caminho personalizado do Steam | `SAM.API/Steam.cs:63`, `GameStatsService.cs:62` | Passar o caminho efetivo ao auxiliar |
| Download de ícone sem limite de tamanho nem validação de host | `AchievementIconCache.cs:41` | Host fixo em `AppConfig` e limite de bytes |
| Parser recursivo sem limite de profundidade | `SAM.Game/KeyValue.cs` | Limitar profundidade, inclusive se reutilizar `BinaryVdf` |
| `StopSteam` mata a árvore de processos, incluindo jogos abertos | `SteamService.cs:185-192` | Usar `StopSteamGraceful` do fork ([SteamService.cs:170](../src/LuaToolsGui/Services/SteamService.cs)) |

## 7. Spike necessário antes de implementar

Nada foi executado: o binário não rodou e nenhum arquivo do Steam foi alterado. Estas respostas não saem do código. Rode numa conta descartável.

1. O Steam aceita o `UserGameStats_*.bin` editado? O upstream não trata campo `crc`, e não verifiquei se o arquivo real tem um.
2. A edição sobrevive a reiniciar o Steam online? Zerar `PendingChanges` sugere que ela não sobe para o servidor, e uma sincronização pode sobrescrevê-la (inferência).
3. O `Playtime` em `localconfig.vdf` sobrevive a reiniciar o Steam online?
4. A rota ao vivo funciona com jogos desbloqueados via Lua sob AmethystTool? O fluxo de recusa do upstream sugere que pode falhar (inferência).
5. As DLLs proxy na pasta do Steam (`dwmapi.dll`, `xinput1_4.dll`) entram no processo auxiliar? `steamclient64.dll` é carregada com busca alterada para essa pasta (`SAM.API/Steam.cs:99-101`). É hipótese, mas é mais um motivo para manter o processo isolado.
6. Definir `SteamAppId` faz o Steam mostrar "jogando" ou somar tempo? Hipótese não testada.

## 8. Limites desta auditoria

- **Analisado:** só `LuaTools.dll` (`sha256:b5792220f2944b9e3be94abfcf55d6d50f1040e7aa1b17965f92d777e6725972`), do pacote `LuaTools-1.3.3-full.nupkg` (`sha256:3a246b2e…`, digest conferido), decompilado com `ilspycmd` 11.2.
- **Não analisado:** `LuaTools.exe`, `LuaTools_ExecutionStub.exe`, `Squirrel.exe`, DLLs de terceiros, a DLL do CloudRedirect e o BST.
- **Sem análise dinâmica:** ausência de achado não prova ausência de comportamento malicioso.
- **Ofuscação:** não encontrei marcador de ofuscador, os nomes ficaram legíveis, e o projeto decompilado só traz strings, XAML e ícones como recursos.
- **Comparação tag × binário:** heurística, por strings e métodos em cada arquivo. Pode deixar passar mudança de lógica sem string nova.
- **Referências `arquivo:linha` sem link:** apontam para a saída do decompilador, apagada ao fim da rodada. Para reproduzir, baixe o pacote, confira o digest e rode `ilspycmd -p` sobre `lib/app/LuaTools.dll`.

## 9. Regras para a reimplementação

- **Sem copiar código do upstream.** Este relatório só descreve mecanismos.
- **Base da camada nativa:** usar o código público de [gibbed/SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager), licença zlib, mantendo o aviso exigido, e não a saída do decompilador. Só o mínimo necessário: 3 exports e as funções usadas das interfaces.
- **Política do fork:** `AppLog`, sem pacote NuGet novo, endpoints em `AppConfig`, requests novas declaradas na release note, `asInvoker` mantido.
- **Reuso:** `BinaryVdf.Read` com `indexedKeys: false` deve servir para os `.bin` ([BinaryVdf.cs:116](../src/LuaToolsGui/Services/AppInfo/BinaryVdf.cs)). Confirmar no spike e tornar a gravação atômica.
- **Auxiliar nativo:** tratar `--stats-helper` antes de `ProtocolService.Register` ([Program.cs:40](../src/LuaToolsGui/Program.cs)) e do mutex.
- **CloudRedirect:** nenhum download nem escrita. Se houver arquivos antigos, só avisar e recusar.

## 10. Ordem recomendada

| # | Trabalho | Modelo | Depende de |
|---|---|---|---|
| 1 | Launch options: `RestoreAll`, All overrides, Show in folder | Sonnet, medium | Nada |
| 2 | Spike da seção 7, itens 1 a 3 | Sonnet, medium | Sua conta de teste |
| 3 | Playtime | Sonnet, medium | Item 2 |
| 4 | Achievements/Stats: leitura e arquivo, com Tools Page e traduções | Sonnet, high | Item 2 |
| 5 | Remover card Nightly | Sonnet, medium | Sua decisão |
| 6 | Rota ao vivo: auxiliar nativo e opt-in | Opus, high | Sua decisão e seção 7, itens 4 a 6 |

## 11. Decisões suas

1. **Rota ao vivo:** aceitar que o app altere conquistas e stats na conta Valve, com opt-in? Recomendo adiar até o spike e começar só com arquivo.
2. **Card Nightly:** remover sem instalar BST? Recomendo sim.
