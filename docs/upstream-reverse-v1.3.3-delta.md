# Engenharia reversa do upstream v1.3.3: complemento (DNS, endpoints, veneno, convivência)

Data: 2026-11-19. Base do fork: `b046198` (1.7.13.3). Nenhum código do fork foi alterado nesta rodada.

Este relatório complementa [parity-audit-v1.3.3-decompiled.md](parity-audit-v1.3.3-decompiled.md) (05/11). Aquele cobriu as features do changelog. Este responde ao que ele não cobriu: DNS fallback, inventário de endpoints, injeção, UAC, domínios e o que acontece se os dois apps estiverem na mesma máquina.

## 0. Premissa conferida

| Premissa do prompt | Fato |
|---|---|
| "v1.3.3+" | A v1.3.3 (03/11) continua sendo a última release. Não há binário mais novo. |
| O binário não foi analisado | Já foi, em 05/11. O pacote baixado hoje tem o mesmo digest (`LuaTools-1.3.3-full.nupkg`, `sha256:3a246b2e…495a`) e o mesmo `LuaTools.dll` (`sha256:b5792220…5972`). |
| O Cloudflare DNS Fallback existe só no binário | Está no código público (`9461259`, "Add Cloudflare DNS fallback"). O binário é equivalente ao público nesse ponto. |
| Alvo: `LuaTools.exe` ou `.zip` | `LuaTools.exe` é só o apphost (260 KB). A lógica está em `lib/app/LuaTools.dll`, que foi a DLL decompilada. |
| Launch options a reimplementar | Já entrou no fork: `RestoreAll`, `LaunchOverridesDialog` e "Show in folder" (`bc1ddaa`). |

**Ambiente:** pacote baixado de `madoiscool/LuaTools` e extraído no scratchpad da sessão. `ilspycmd` 11.2.0.10050 foi instalado num `--tool-path` local, sem instalação global. Nada do upstream foi executado nem misturado ao build do fork.

## 1. O que ele adicionou de novo

### 1.1 Delta binário × código público (`9461259`)

O conteúdo é o mesmo da auditoria anterior: Tools Page, Achievements/Stats (namespace `SAM.API`), Playtime, Launch overrides, migração OST→BST e troca forçada do CloudRedirect. Nada novo apareceu nesta passada.

- **Hosts novos:** só `shared.akamai.steamstatic.com`, que serve os ícones de conquista (`AchievementIconCache`). As outras URLs que aparecem "só no binário" são constantes que o compilador concatenou e que existem no público (`check_apis`, `donatekeys/send`, `analytics.lua.tools`, repositórios do GitHub).
- **Provedores de manifesto e DLC:** os mesmos de antes, sem nenhum provedor novo: `lua.tools`/`db.lua.tools`, `167.235.229.108/check_apis`, `hubcapmanifest.com`, `applist.morrenus.xyz`, `api.steamcmd.net` e os mirrors de GitHub `ghproxy.net`, `ghfast.top` e `gh.ddlc.top`.
- **P/Invoke novo:** só 3 imports de `kernel32.dll` (`LoadLibraryEx`, `GetProcAddress`, `SetDllDirectory`) em `SAM.API/Steam.cs`. Eles servem ao auxiliar de stats, que carrega `steamclient64.dll`.

### 1.2 Injeção e bypass: sem mudança

- Os nomes de DLL proxy são idênticos aos do público: `dwmapi`, `xinput1_4`, `winmm`/`winmm_real`, `bcrypt`/`bcrypt_real`, `dbghelp`/`dbghelp_real`, `OpenSteamTool.dll` e `millennium.dll`.
- Não há `WriteProcessMemory`, `VirtualAllocEx`, `CreateRemoteThread`, `VirtualProtect` nem `SetWindowsHookEx` no assembly.
- O contorno de detecção da Valve, se existir, está nas DLLs nativas (BST/OST), que **não foram analisadas**. O `LuaTools.dll` só copia esses arquivos.

### 1.3 Cloudflare DNS Fallback: como funciona

**Arquivos:** `LuaToolsGui.Services/AppHttp.cs` e `LuaToolsGui.Services/DohResolver.cs`.

- **Mecanismo:** não usa `Dns.GetHostAddresses` nem troca de `HttpClient`. Há um único `SocketsHttpHandler` compartilhado com `ConnectCallback`, e todo cliente sai de `AppHttp.Create`. As exceções são o próprio `DohResolver` e `SteamDepotInfo`.
- **Modos:** `Auto` (padrão), `Always` e `Never`. Ficam numa ComboBox em Settings (`SettingsView.xaml`, `DnsModeOptions`), e o valor é salvo como `DnsMode` em `settings.json`.
- **`Auto`:** tenta o DNS do sistema (`Socket.ConnectAsync(host, port)`). Em qualquer exceção que não seja cancelamento, consulta o DoH e conecta nos IPs devolvidos. Se der certo, liga `_systemDnsSuspect`, um flag **global e permanente até fechar o app**. A partir daí, todo host passa a ir primeiro pelo DoH.
- **`Always`:** usa o DoH primeiro e cai para o DNS do sistema se falhar.
- **Exceções:** `localhost`, `*.local`, `*.localhost`, IP literal e nome sem ponto nunca passam pelo DoH (`ShouldBypass`).
- **Resolver:** faz `GET https://1.1.1.1/dns-query?name=<host>&type=A` com `Accept: application/dns-json` e timeout de 5 s, num `HttpClient` próprio (não entra em recursão). O cache é um `ConcurrentDictionary` com TTL limitado entre 30 s e 5 min.
- **Só IPv4:** a consulta pede só registro A. O parser aceita o tipo 28 (AAAA), mas nunca o pede.

**Limites (inferência pelo código, não testado):**
- O fallback só dispara quando o *connect* falha. Se o provedor responder com o IP de uma página de bloqueio que aceita TCP na 443, o connect funciona, o TLS falha depois e o modo `Auto` não reage.
- Depois que o flag liga, o nome de **todo** host contatado vai para a Cloudflare, não só `lua.tools`.

**No fork:** não existe. Não há `DohResolver` nem `ConnectCallback`, e os serviços criam `HttpClient` com handlers próprios ([AuthService.cs:59](../src/LuaToolsGui/Services/AuthService.cs), [HubcapService.cs:125](../src/LuaToolsGui/Services/HubcapService.cs), [SteamAppInfoCache.cs:93](../src/LuaToolsGui/Services/SteamAppInfoCache.cs), [SteamDepotInfo.cs:50](../src/LuaToolsGui/Services/SteamDepotInfo.cs)).

### 1.4 UI/UX

Nada além do que a auditoria anterior listou. O seletor de DNS em Settings já vinha do público.

## 2. O que é valioso para o usuário

| Item | Dor real | Vale reimplementar? |
|---|---|---|
| Fallback DoH | Provedor que bloqueia `lua.tools` por DNS deixa o app sem catálogo | **Sim**, com escopo menor que o do upstream (seção 4) |
| Endurecimento da convivência (seção 3.2) | Rodar o upstream uma vez apaga a escolha do AmethystTool e pode ligar o DonateKeys deles | **Sim**, as medidas baratas |
| Playtime, Achievements/Stats por arquivo | Já avaliados em 05/11 | Continuam dependendo do spike da seção 7 do relatório anterior, que ainda não foi feito |

## 3. O que é veneno

### 3.1 Confirmado no binário

| Item | Onde | O que faz |
|---|---|---|
| Telemetria Umami | `AnalyticsService.TrackAppLaunchAsync`, `AppConfig.UmamiHost` | A cada launch, faz `POST https://analytics.lua.tools/api/send` com website id, `hostname=desktop.lua.tools` e `url=/launch/<versão>`. **O User-Agent finge ser Chrome 120.** |
| DonateKeys | `DonateKeysService.SendPendingKeysIfEnabledAsync` | Lê os `DecryptionKey` de `<Steam>/config/config.vdf` e envia `appid:key` em `POST http://167.235.229.108/donatekeys/send`, em **HTTP puro para um IP nu**. Vem ligado por padrão (`_settings.DonateKeys ?? true`) |
| SteamAutoCrack | `api.github.com/repos/SteamAutoCracks/Steam-auto-crack` | Continua no assembly |
| CloudRedirect forçado | `UnlockerService.MigrateLegacyCloudRedirectAsync`, chamado em `App.xaml.cs:116` | Detalhe abaixo |
| Escrita nos arquivos do CloudRedirect | `GameStatsService` | Grava `<Steam>/cloud_redirect/stats/<conta>/<appid>.json` e `pending_edits.json` |

**Fluxo do CloudRedirect:**
- A cada launch, se `cloud_redirect.dll` existir na raiz do Steam e não contiver o texto UTF-16 `/repos/madoiscool/CloudRedirect/releases` (`IsForkBuild`), o app baixa a última release de `api.github.com/repos/madoiscool/CloudRedirect/releases/latest` e substitui a DLL sem perguntar.
- O digest só é conferido quando o GitHub o informa.
- O mesmo serviço liga e desliga `[cloud] enabled` em `opensteamtool.toml`.

**Não encontrado:**
- `runas` só aparece no fallback do netsh (`HttpServerService`: `netsh http add urlacl url=http://127.0.0.1:6767/ user=Everyone`).
- Nenhuma outra elevação, exclusão no Defender, `schtasks` ou edição do arquivo `hosts`.
- Nenhuma biblioteca de telemetria além do Umami (Sentry, AppCenter, Mixpanel, PostHog: nada).
- Nenhum domínio desconhecido novo. O único destino deles fora de `*.lua.tools` é `167.235.229.108`, para `check_apis` e `donatekeys`, que já existia no público.

### 3.2 Convivência na mesma máquina (achado novo)

Os dois apps compartilham identificadores. Isso não é novidade da v1.3.3, mas responde à pergunta "nosso código colide se ele for instalado junto?".

| Recurso compartilhado | Upstream | Fork | Efeito |
|---|---|---|---|
| `%AppData%\LuaToolsGui\settings.json` | `ModeMigration.Migrate` não conhece `"AmethystTool"`: grava `SelectedMode = null` e marca `OnboardingComplete = false` em `cache.json` | Guarda `"AmethystTool"` ([ActiveBackendPolicy.cs:50](../src/LuaToolsGui/Services/ActiveBackendPolicy.cs)) | **Abrir o upstream uma vez apaga a escolha do backend do fork.** Ao voltar, o fork resolve `None`. O upstream reabre o onboarding dele, que oferece BST, e o BST sobrescreve `dwmapi.dll`/`xinput1_4.dll` (não verifiquei se ele instala sem clique) |
| Mesmo arquivo, outras chaves | Regrava com `JsonSerializer.Serialize(_settings)` e o `AppSettings` dele, sem `JsonExtensionData` | Tem chaves que ele não declara, como `SaveBackupFolder` e `CustomSaveFolders` | Essas chaves do fork são **perdidas** na primeira gravação do upstream |
| Chave `DonateKeys` | Ausente vale `true` | O fork nunca grava essa chave | No upstream, o envio de chaves fica ligado por padrão, mesmo depois de o usuário ter usado o fork |
| `auth.dat` | DPAPI CurrentUser sobre JSON | DPAPI CurrentUser ([AuthService.cs:587](../src/LuaToolsGui/Services/AuthService.cs)) | Mesmo arquivo. Compatibilidade de schema não verificada |
| Mutex `LuaToolsGui.SingleInstance` e evento `LuaToolsGui.ShowWindow` | Iguais | Iguais ([Program.cs:67](../src/LuaToolsGui/Program.cs), `:177`) | Só um roda por vez. Abrir um com o outro na bandeja mostra a janela do outro. Confunde, mas não causa dano |
| Protocolo `luatools://` | Registra | Registra ([ProtocolService.cs:8](../src/LuaToolsGui/Services/ProtocolService.cs)) | Vale o último registrado. Links do site abrem no app que rodou por último |
| Plugin `%AppData%\LuaToolsGui\plugin\…\luatools.js` | Mesmo caminho | Mesmo caminho ([CefInjectorService.cs:415](../src/LuaToolsGui/Services/CefInjectorService.cs)) | O `luatools.js` deles não manda o token da ponte 6767 do fork, então o fork pode recusar as chamadas (hipótese não testada) |
| `cloud_redirect.dll` na raiz do Steam | Troca pela build deles a cada launch | Deixa intocada ([AmethystToolPlan.cs:123](../src/LuaToolsGui/Services/AmethystToolPlan.cs)) | Um arquivo deixado por builds antigos é substituído em silêncio. Segundo o comentário do fork, fica inerte sem o `OpenSteamTool.dll`. Não verifiquei no código nativo do AmethystTool |
| Velopack `packId` | `LuaTools` | `LuaToolsAmethyst` | **Sem colisão:** instalações e updates separados |

## 4. Recomendação

### Entra na 1.8.0

1. **Fallback DoH, reimplementado do zero e com escopo menor:**
   - Um `ConnectCallback` compartilhado pelos clientes que falam com `lua.tools`/`db.lua.tools`.
   - Fallback só para uma allowlist de hosts em `AppConfig`, não para todo host.
   - Nada de flag global permanente: guardar a suspeita por host, com expiração.
   - Endpoint `1.1.1.1` em `AppConfig`, declarado na release note como request nova.
   - Modo `Never` disponível.
   - Testes para o parser e para o roteamento, com um handler falso.
   - Modelo sugerido: Sonnet, medium.
2. **Proteção contra o upstream na mesma máquina (pequeno):**
   - Gravar `"DonateKeys": false` no `settings.json`. É uma chave que só o upstream lê, e assim o padrão `true` dele não vale nesta máquina.
   - Guardar uma cópia do backend escolhido fora do `settings.json` (`cache.json` também é compartilhado, então teria de ser outro arquivo). No launch, se o `SelectedMode` aparecer `null` e a cópia disser `AmethystTool`, avisar e restaurar.
   - Modelo sugerido: Sonnet, medium.

### Continua dependendo de pendências anteriores

- Playtime e Achievements/Stats por arquivo dependem do spike (seção 7 do relatório de 05/11).
- A remoção do card Nightly depende da sua decisão.

### Fica de fora

Telemetria Umami, DonateKeys, SteamAutoCrack, troca forçada e escrita de arquivos do CloudRedirect, migração automática OST→BST, rota ao vivo de stats (até o spike e o opt-in) e o `user=Everyone` no `urlacl`.

## 5. Decisões suas

1. **Padrão do DNS no fork:** `Auto` limitado à allowlist (recomendo) ou desligado por padrão?
2. **Gravar `DonateKeys: false`:** é uma chave sem efeito no fork e serve só para proteger contra o upstream. Recomendo gravar.
3. **Restaurar o backend apagado pelo upstream:** restaurar automaticamente com aviso (recomendo) ou só avisar?

## 6. Limites

- Analisei só `LuaTools.dll`. Ficaram de fora `LuaTools.exe` (apphost), `LuaTools_ExecutionStub.exe`, `Squirrel.exe`, as DLLs de terceiros, a DLL do CloudRedirect e o BST/OST.
- A análise foi só estática. Ausência de achado não prova ausência de comportamento.
- A comparação binário × público foi por strings, nomes de arquivo e P/Invoke. Ela pode deixar passar mudança de lógica que não traga string nova.
- As referências sem link (`AppHttp.cs`, `DohResolver.cs`, `UnlockerService` etc.) apontam para a saída do decompilador, que está no scratchpad desta sessão e não vai para o repositório. Para reproduzir: baixar o `.nupkg`, conferir o digest e rodar `ilspycmd -p` sobre `lib/app/LuaTools.dll`.
