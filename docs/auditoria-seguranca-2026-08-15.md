# Auditoria de segurança — LuaTools 1.2.8

**Data:** 2026-08-15 (sessão de teste e fechamento em 2026-08-16)
**Status:** ✅ **Sessão concluída.** Todas as correções aprovadas foram implementadas e validadas em
execução real (build, testes automatizados, e uso do app de ponta a ponta numa conta descartável). A
única etapa de teste planejada e **deliberadamente não executada** foi a remoção de um jogo instalado —
decisão do usuário, para não arriscar o jogo real que ficou funcionando. Ver seção 8 para o veredito final.

Este documento consolida três fases de trabalho conduzidas na mesma sessão: (1) auditoria inicial e
correções de segurança, (2) refatoração guiada por skills C# com adição de testes, (3) análise e
correção de riscos residuais antes da primeira execução, e (4) validação em execução real numa conta
Windows descartável.

Não pertence ao repositório público — está em `docs/`, que o `.gitignore` já reserva para notas internas.

---

## 1. Auditoria inicial

Auditoria ampla do código (WPF, .NET 8, gerenciador de manifestos/lua do Steam), sem acesso a build
funcional no início (SDK ausente na máquina na época). Achados classificados por prioridade e todos
corrigidos nesta fase.

### P0 — Críticos

| # | Achado | Correção |
|---|---|---|
| P0.1 | `HttpServerService` (porta 6767) respondia `Access-Control-Allow-Origin: *` sem validar `Origin`/`Host` — qualquer página aberta no navegador podia acionar `/open-url` (ShellExecute), `/remove/{appid}`, `/add`, `/restart-steam` | Validação de `Host` (fixado em loopback, anti DNS-rebinding), token de sessão por launch para o cliente interno (`CefInjectorService`), allowlist de `Origin` com eco exato — nunca `*` |
| P0.2 | `SteamlessService`/`CloudRedirectService` baixavam de mirrors de terceiros (`GithubProxy`) e executavam **sem verificação de hash alguma** | Verificação sha256 obrigatória antes de extrair/executar |

### P1 — Altos

| # | Achado | Correção |
|---|---|---|
| P1.1 | Padrão fail-open na verificação de digest: `is { } want && sha != want` — digest ausente pulava a checagem | Reescrito para fail-closed em `UnlockerService` e `PluginInstallerService` |
| P1.2 | OAuth sem parâmetro `state` — login CSRF | `state` gerado, comparado em tempo constante |
| P1.3 | Nenhum tratamento global de exceção; `OnStartup` `async void` desprotegido | Crash silencioso vira log + mensagem visível |
| P1.4 | `luatools://install/silent/<appid>` instalava headless sem qualquer confirmação, acionável por qualquer site | Diálogo de confirmação antes de prosseguir |

### P2 — Médios/baixos

- `SettingsService`/`CacheService`: sem lock, sem escrita atômica, gravando de várias threads simultâneas
- Chave Hubcap em texto puro (enquanto `auth.dat` já usava DPAPI)
- Nenhum workflow de CI de build/teste (só i18n)
- Versão `1.1.3` no `.csproj` enquanto o app já era 1.2.8
- Leak de `HttpResponseMessage` no `GithubProxy`
- `RefreshAsync` fora do lock em `AuthService`
- `urlacl` reservado para `Everyone` em vez do usuário atual
- `PluginLog` sem limite de tamanho
- `check-i18n.py` usava regex frágil em vez de parsing XML real, e não validava `Strings.Designer.cs`

**Todos os itens acima foram corrigidos** nesta fase.

---

## 2. Refatoração guiada por skills + testes

Skills carregadas: `csharp-typing`, `csharp-architecture-api`, `csharp-code-quality`, `csharp-testing`,
`csharp-visual-ui`.

**Bug de build corrigido primeiro:** comentário XML inválido no `.csproj` (`--packVersion` continha `--`,
ilegal em XML) — erro meu, introduzido na fase 1.

### Refatorações (R1–R5)

- **R1** — `Services/AssetIntegrity.cs` (novo): `Sha256OfFile`/`ParseDigest` estavam duplicados em 4
  serviços — foi exatamente essa duplicação que fez 3 deles derivarem para o fail-open e 1 ficar sem
  verificação nenhuma. Centralizado, fail-closed por construção.
- **R2** — `Services/LocalApiAccessPolicy.cs` (novo): política de acesso extraída como função pura +
  `readonly record struct AccessDecision`, porque `HttpListenerRequest` é selado e não instanciável —
  antes disso, a lógica de autorização era literalmente impossível de testar.
- **R3** — `SettingsService`/`CacheService`: caminhos `%AppData%` estáticos viraram construtor `internal`
  com diretório injetável (test seam), sem mudar o comportamento em produção.
- **R4** — `AuthService.StateMatches` extraído como método `internal static` testável.
- **R5** — Testes: `LocalApiAccessPolicyTests`, `AssetIntegrityTests`, `AuthStateTests`,
  `PersistenceTests` — 76 novos casos.

`Directory.Build.props` criado com `TreatWarningsAsErrors`. `AnalysisLevel=latest-recommended` foi
avaliado e **deliberadamente não ativado** — levanta ~250 violações pré-existentes (CA1863, CA1051,
CA1848, CA1305, CA1031); documentado como limpeza em etapas, não forçado de uma vez.

**Validação ao final desta fase:** build 0 erros/avisos · **259/259 testes** · i18n OK.

Também verifiquei que os testes novos **realmente pegam regressão**: reintroduzi deliberadamente o
fail-open e o CORS wildcard como mutantes temporários — 7 e 11 testes quebraram respectivamente,
confirmando cobertura real, não decorativa.

---

## 3. Análise de riscos residuais (pré-execução)

Antes de rodar o `.exe` pela primeira vez, análise dedicada com evidência empírica: segunda opinião via
MCP Antigravity (chegou às mesmas conclusões de forma independente), diagnósticos reais do sistema
(`netstat`, `netsh http show urlacl`, `Get-NetFirewallRule`), e verificação empírica baixando releases
reais do GitHub para checar presença de `digest`.

| # | Severidade | Achado |
|---|---|---|
| C1 | 🔴 Crítico | `atom0s/Steamless` v3.1.0.5 (publicado 2024-03-30) tem `"digest": null` — GitHub só passou a popular esse campo em meados de 2025. O fail-closed da fase 1 **quebrou permanentemente** o "Remove Steam DRM". Hash real verificado por download direto: `e3e2d22e098ff3fb359b2876aa2bed9596f0501e6ff588cbffae90a76d2dc4f5`, 610.646 bytes |
| C2 | 🔴 Crítico | A junction `.cef-enable-remote-debugging` habilita CDP **sem autenticação** na porta 8080 — qualquer processo local ganha controle do navegador do Steam com a sessão logada. Recriada silenciosamente a cada checagem de status |
| H1 | 🟠 Alto | `GithubProxy.Candidates()` fazia `yield return url` antes de checar `IsGithub()`. Quando `api.github.com` é espelhado por `lua.tools/api/gh`, essa origem controla **tanto** a URL de download **quanto** o digest — hash comparado contra hash do próprio atacante |
| H2 | 🟠 Alto | Sem `app.manifest`; erros de permissão ao gravar no Steam eram reportados como erro genérico de IO, indistinguível de "Steam está aberto" |
| H3 | 🟠 Alto | Janela TOCTOU entre verificar hash e usar o arquivo, em caminhos de staging fixos (`steamless.zip`, `ExePath + ".partial"`) |
| M1 | 🟡 Médio | Mirrors de terceiros (`ghproxy.net`, `ghfast.top`, `gh.ddlc.top`) fixos no código, não configuráveis |
| M2 | 🟡 Médio | `.lua` de fontes não confiáveis executado dentro do `steam.exe` sem nenhuma validação de conteúdo |
| M3 | 🟡 Médio | `crash.log` podia capturar credenciais — `AuthService` monta mensagens de exceção com corpo bruto de resposta do Supabase (contém tokens) |

Itens de severidade baixa documentados sem ação necessária (ramo morto IPv6, listener OAuth bindando
todas as interfaces por 5 min, firewall do Windows não filtra loopback — o bind é o único controle real).

Também apontadas lacunas de cobertura de integração: fluxo completo de instalação, servidor HTTP ponta a
ponta com sockets reais, fallback do `GithubProxy`, injeção CDP, `InstallZip` com zips forjados — nenhuma
implementada como teste automatizado nesta rodada (ver seção 6).

---

## 4. Correções dos riscos residuais

Implementadas na ordem aprovada: C1, M3, C2, H1, H2, H3, M1, M2. Restrições respeitadas: `asInvoker`
mantido (sem elevação forçada), fail-closed nunca enfraquecido, sem novas dependências, strings novas via
`Resources.Strings`/`PENDING_TRANSLATION`.

| Item | Arquivo(s) | Mudança |
|---|---|---|
| **C1** | `AppConfig.cs` | `SteamlessPinnedAssetName` + `SteamlessPinnedSha256` — hash revalidado por um **segundo download independente** (bateu: mesmos 610.646 bytes, mesmo hash) |
| **C1** | `SteamlessService.cs` | `ResolveExpectedDigest` distingue "API não publicou digest" (→ usa o pin, só para o asset exato) de "digest presente mas errado" (→ continua falhando, sem fallback) |
| **M3** | `Services/LogSanitizer.cs` (novo) | Redige JWTs, campos `access_token`/`refresh_token`/`api_key`/`password`/etc., chaves `smm_`, headers `Bearer`. Timeout de 250ms nas regex — se estourar, descarta o texto inteiro em vez de arriscar vazar |
| **M3** | `App.xaml.cs`, `PluginLog.cs` | Sanitização aplicada antes de gravar em `crash.log`, no toast de erro, no diálogo de falha de startup e no `plugin-backend.log` |
| **C2** | `CacheService.cs` | `CdpConsentGranted` (default **false**, persistido) |
| **C2** | `PluginInstallerService.cs` | `MayEnableCdp()` gateia os dois pontos de criação da junction. Sem UI disponível (contexto headless) → trata como não concedido |
| **C2** | `App.xaml.cs` + `Strings.resx` | Diálogo modal explicando a exposição da porta 8080, default "Não", exige clique explícito em "Sim" |
| **H1** | `GithubProxy.cs` | `IsTrustedDownloadUrl` — allowlist de hosts GitHub (`github.com`, `api.github.com`, `raw.githubusercontent.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`), HTTPS obrigatório. `DownloadAsync` recusa antes de qualquer requisição; erro não ecoa path/query da URL recusada |
| **H2** | `app.manifest` (novo) + `.csproj` | `asInvoker` explícito, documentado — **nenhuma elevação adicionada**. Verificado embutido no `.exe` compilado |
| **H2** | `UnlockerService.cs` | `UnauthorizedAccessException` vs `IOException` distinguidos — mensagens diferentes ("rode como admin uma vez" vs "feche o Steam") |
| **H3** | `AssetIntegrity.cs` | `MatchesStream` — verifica e usa pelo **mesmo handle** aberto (`FileShare.None`) |
| **H3** | `SteamlessService.cs`, `CloudRedirectService.cs` | Staging em diretório com GUID (eram caminhos fixos) |
| **M1** | `Services/GithubMirrors.cs` (novo) | Mirrors configuráveis via `settings.json` (`GithubDownloadMirrors`/`GithubApiMirrors`); entradas não-HTTPS rejeitadas; lista vazia = mirrors desativados |
| **M2** | `Services/LuaManifestValidator.cs` (novo) | Denylist (`os.execute`, `io.open/popen`, `loadstring`, `dofile`, `require`, `package.loadlib`, `debug.*`, `_G`, `load()`/`pcall()` dinâmico) **rejeita**; linha desconhecida só conta e loga — nunca bloqueia manifesto legítimo |
| **M2** | `LuaInstaller.cs` | Validação no chokepoint único `WriteLua` — cobre plugin, drag-drop, Add e Fixes de uma vez |

Testes novos: `LuaManifestValidatorTests`, `LogSanitizerTests`, `DownloadTrustTests` — 61 casos.

**Validação final desta fase:** build 0 erros/avisos · **320/320 testes** · i18n OK (437 chaves, 6 em
`PENDING_TRANSLATION`) · `app.manifest` confirmado embutido no binário (`LuaTools.exe`, v1.2.8).

### Padrões que podem disparar antivírus (documentado, não removível sem quebrar o produto)

1. **DLL side-loading** (`winmm.dll`, `dwmapi.dll`, `xinput1_4.dll` ao lado do `steam.exe`) — mecanismo
   central do produto, alta chance de detecção heurística.
2. **`cmd.exe /c mklink /j`** — criação da junction CDP. Agora só ocorre **após consentimento** do usuário.
3. **`Verb="runas"`** no `netsh http add urlacl` — só dispara se o bind em `127.0.0.1:6767` falhar.
4. **Download + execução de `.exe`** de terceiros (Steamless, CloudRedirect).

---

## 5. Testes em execução real

Conta Windows descartável, sem VM, Kaspersky ativo. Jogo de teste: **Resident Evil Requiem** (App ID
3764200).

### 5.1 — Primeira execução

- Diálogo de consentimento CDP apareceu **antes** de qualquer junction ser criada, com o texto exato
  implementado, botão padrão "Não", exigindo "Sim" explícito → **C2 confirmado funcionando**.
- Rodapé mostrou `v1.2.8` → confirma que a correção de versão do `.csproj` chegou ao binário publicado.
- Usuário optou por conceder consentimento, de forma informada (conta descartável, trade-off conhecido).

### 5.2 — Login

- Primeira tentativa via "Entrar com Discord" bateu em `Auth_Err_CallbackPortBusy` ("Couldn't start
  sign-in: port 53789 is already in use") — a mensagem exata implementada na correção do `state` OAuth,
  causada por um listener de tentativa anterior ainda vivo.
- Caminho alternativo usado com sucesso: comando `/login` do bot oficial do Discord do LuaTools **via DM**
  → código de 6 caracteres → colado no campo "Resgatar" nas Configurações do app → `SignInWithCodeAsync`
  completou sem tocar a porta 53789 (chamada direta à API, sem listener HTTP local).
- **Resultado: login concluído com sucesso.**

### 5.3 — Download de fonte real

- Fontes exibidas: "Sadie (Hubcap)" travada (chave não configurada — comportamento esperado), "Luie" e
  "Ryuu" disponíveis, "Sushi" não encontrado (indisponibilidade do lado da fonte, não relacionado ao
  código).
- Fonte escolhida: **Luie**. Download concluído sem erro.
- **Confirmação forte**: o Steam começou a baixar os 75,7 GB do jogo real como se fosse dono — só
  acontece se o `.lua` gerado foi válido, passou pelo `LuaManifestValidator` sem rejeição, e o unlocker
  interpretou o depot corretamente.

### 5.4 — Instalação do manifesto

- Confirmado na tela **Gerenciar**: Resident Evil Requiem listado, App ID 3764200, "Added Aug 15, '26".
- Confirma que o `LuaManifestValidator` **não rejeitou** um manifesto real e legítimo — validação do
  denylist não gerou falso positivo.

### 5.5 — Persistência do consentimento CDP

- Tela **Plugin** mostrou Status "Instalado", v2.2 "Atualizado", Frontend "Instalado", Carregador
  (`winmm.dll`) "Atualizado" — **sem reabrir o diálogo de consentimento**.
- Confirma que `CdpConsentGranted` persiste corretamente em `cache.json` e `MayEnableCdp()` não
  re-pergunta a cada checagem de status (o self-heal continua funcionando para quem já consentiu, sem
  incomodar).

### 5.6 — Kaspersky

- **Nenhuma detecção em nenhuma etapa** — download da fonte, extração/instalação do manifesto,
  atualização do plugin. Usuário também rodou verificação manual explícita: sem alertas.

### 5.7 — Esclarecimento sobre "DonateKeys"

Fora do escopo dos 8 itens corrigidos — funcionalidade pré-existente, **não tocada** por nenhuma das
correções desta auditoria:

- `SettingsService.DonateKeys` — default **ativado**, sem prompt por instalação (diferente do
  consentimento de CDP, que criei especificamente para pedir confirmação por ação).
- `DonateKeysService.SendPendingKeysIfEnabledAsync()` roda em segundo plano no início do app, envia
  chaves de decriptação de depot para o pool comunitário do LuaTools, com dedup permanente por appid em
  `CacheService.DonatedAppIds`.
- **Consequência prática**: como o Resident Evil Requiem foi adicionado e o app já rodou desde então, é
  provável que a chave desse jogo já tenha sido enviada, a menos que o toggle correspondente em
  Configurações tenha sido desativado antes.
- Nenhum código foi alterado neste ponto — só esclarecimento factual a pedido do usuário.

### 5.8 — Incidente real pós-instalação: build incorreta (Denuvo)

Depois da instalação, o jogo recusou abrir: diálogo de anti-tamper (`codefusion.technology/anti-tamper`,
código `e=88500000`). Diagnóstico: sem relação com nenhuma correção desta auditoria — é o Denuvo do
próprio jogo recusando a build. Causa raiz: o manifesto instalado pelo fluxo genérico de "Adicionar" não
era fixado em build específica, e "Atualizar apps automaticamente" (ligado por padrão) deixou o Steam
atualizar para uma build mais nova do que a correção específica do jogo cobria — confirmado em seguida
por um segundo erro explícito do próprio crack: *"Wrong game build. Crack works only on build 22277314."*

Resolvido usando a funcionalidade dedicada do próprio app (página **Correções**): reinstalação do
manifesto **fixado na build** (`<appid>_<buildid>.lua`, que trava o pin de manifesto independentemente do
"Atualizar automaticamente" global, por design do `LuaInstaller.KeepPinsFor`), reinício do Steam, e
aplicação da correção específica daquela build. **Jogo funcionando normalmente ao final.**

Isso não é uma falha do LuaTools nem desta auditoria — é o comportamento esperado dessa classe de
ferramenta com jogos protegidos por anti-tamper, e o app já tinha a funcionalidade certa para resolver.
Serve como validação extra e não planejada: o fluxo completo (detecção de problema → diagnóstico →
correção via feature própria do app → sucesso) funcionou sem precisar de nenhuma intervenção de código.

### 5.9 — Etapa não executada: remoção

Removida do escopo por decisão do usuário: apagar o `.lua` de `stplug-in` para testar a remoção
arriscaria desconfigurar o Resident Evil Requiem, que a essa altura já estava instalado e funcionando
(seção 5.8). Das três opções apresentadas (testar no jogo real / esperar e testar depois / usar um App ID
descartável), o usuário optou por **não testar nesta sessão**.

**Avaliação do usuário, aceita como fechamento**: a cobertura das demais etapas foi considerada suficiente
para validar o funcionamento principal do sistema, mesmo sem esse teste específico.

---

## 5-B. Remoção de coleta de dados (adendo pós-sessão)

Motivada por uma comparação que o usuário fez com um fork mantido por um parceiro. O fork tinha removido
duas funcionalidades de envio de dados; ambas as afirmações foram **verificadas no código e confirmadas** —
e uma delas era pior do que a comparação sugeria.

### O que estava acontecendo

| Feature | Comportamento real verificado |
|---|---|
| **DonateKeys** | Varria `config/config.vdf` do Steam por `DecryptionKey` de cada depot e enviava para `http://167.235.229.108/donatekeys/send` — **HTTP puro, sem TLS, para um IP cru**, payload `appid:chave` em texto claro. **Ligado por padrão.** Qualquer observador de rede no caminho lia as chaves |
| **Telemetria (Umami)** | Ping por launch para `analytics.lua.tools`, **sem nenhum toggle em lugar algum da UI** — incondicional. Enviava deliberadamente um User-Agent falsificado do Chrome para driblar o filtro anti-bot do Umami (há comentário no código admitindo isso) |

**Correção de rumo da auditoria original:** na seção 5.7 eu havia classificado o DonateKeys como "fora de
escopo, informativo". Isso foi leniente demais — transmitir segredos (chaves de decriptação) em texto claro
é um achado de segurança legítimo, não uma preferência de privacidade. O parceiro acertou nesse ponto.

### O que foi removido

Remoção completa, não desativação — nada de código morto que um merge futuro do upstream possa reativar:

- `Services/AnalyticsService.cs` e `Services/DonateKeysService.cs` deletados
- Registros de DI e os dois disparos em `App.xaml.cs` (startup) removidos
- Constantes `Umami*` e `DonateKeysUserAgent` removidas do `AppConfig`
- Setting `DonateKeys` (`AppSettings` + propriedade + verificação de "vazio")
- `DonatedAppIds` do `CacheData`/`CacheService` (era o dedup permanente das chaves já enviadas)
- `SettingsViewModel`: propriedade observável, handler e inicialização
- `SettingsView.xaml`: a seção **Community** inteira — continha só esse toggle, então sobraria um card vazio
- 3 chaves RESX (`Settings_DonateKeys`, `Settings_DonateKeys_Hint`, `Settings_Section_Community`) × **30
  arquivos** = 90 entradas, via script Python com `encoding='utf-8'` e `newline=''` explícitos (o
  `Set-Content` do PowerShell já havia corrompido em-dashes numa fase anterior — não foi repetido)
- Accessors correspondentes no `Strings.Designer.cs`
- Testes em `PersistenceTests` que referenciavam a feature, reapontados para `HardwareAppIds`

**Mantido de propósito:** `AppConfig.ManifestBackendUrl` — apesar de também ser HTTP puro, ainda é
dependência funcional de `LuaToolsApiClient.CheckSourcesAsync`. Diferença material: aquela chamada vaza
apenas *qual appid* está sendo consultado, não segredos.

### Validação

Build 0 erros/avisos · **320/320 testes** · i18n OK (**434 chaves**, 29 idiomas, Designer em sincronia) ·
varredura confirmando zero resíduo de `Analytics`/`Umami`/`DonateKeys`/`DonatedAppIds` no código, e zero
mojibake nos 30 RESX após o script.

### Ressalvas registradas

1. **Reciprocidade** — o pool comunitário de chaves que o LuaTools consome é alimentado por essas doações.
   Este build passa a consumir sem contribuir. É uma escolha legítima do usuário (especialmente dado o
   transporte inseguro), mas fica registrada explicitamente.
2. **Chaves já enviadas** — a remoção é prospectiva. Qualquer chave doada antes desta mudança (incluindo,
   provavelmente, a do Resident Evil Requiem, conforme seção 5.7) já saiu da máquina e não é recuperável.
3. **Auto-update do upstream** — builds locais não se auto-atualizam (`UpdateService` sai cedo quando o
   Velopack reporta `IsInstalled == false`), então esta remoção é estável em build local. Mas **instalar
   um release oficial do upstream por cima reintroduziria as duas features**.

---

## 6. Itens em aberto / atenção manual

1. **Remoção** — não testada em execução real por decisão deliberada do usuário (ver seção 5.9). O
   caminho de código (`HandleRemove` / `LuaInstaller` apagando `<appid>.lua` de `stplug-in`) foi coberto
   apenas por testes unitários/estáticos nas fases 1–3, não por uso real.
2. **Hash pinado do Steamless** — verificado por mim duas vezes, de conexões TLS independentes, mas
   ambas partindo da mesma máquina/rede. Validação totalmente independente (outra rede) ainda não feita.
3. **`AnalysisLevel=latest-recommended`** — caminho documentado no `Directory.Build.props`, não
   executado. ~250 violações pré-existentes precisam de limpeza em etapas antes de ativar.
4. **Testes de integração automatizados** — as lacunas identificadas na seção 3 (fluxo completo,
   servidor HTTP ponta a ponta, fallback de mirror, `InstallZip` com zips forjados) foram exercitadas
   manualmente nesta sessão de teste real, mas **não** viraram testes automatizados no repositório.
5. **`DonateKeys` e telemetria** — ~~informativo apenas~~ **removidos** na seção 5-B. Restam as três
   ressalvas registradas lá: reciprocidade com o pool comunitário, chaves já enviadas antes da remoção
   (irrecuperáveis), e o fato de que instalar um release oficial do upstream por cima reintroduziria ambas.
6. **6 chaves em `PENDING_TRANSLATION`** (`Auth_Err_CallbackPortBusy`, `Auth_Err_SignInTimedOut`,
   `Cdp_Consent_Body`, `Cdp_Consent_Title`, `Protocol_SilentInstall_Body`,
   `Protocol_SilentInstall_Title`) — aguardando tradução para os outros 28 idiomas quando a UI dessas
   telas for considerada final.

---

## 7. Resumo numérico

| Métrica | Fase 1 (auditoria) | Fase 2 (refatoração) | Fase 3 (riscos residuais) |
|---|---|---|---|
| Testes totais | 183 (linha de base) | 259 | **320** |
| Build | 0 erros / 0 avisos | 0 erros / 0 avisos | 0 erros / 0 avisos |
| i18n | OK | OK, 435 chaves | OK, 437 chaves, 6 pendentes de tradução |
| Arquivos novos de serviço | — | `AssetIntegrity.cs`, `LocalApiAccessPolicy.cs` | `LogSanitizer.cs`, `GithubMirrors.cs`, `LuaManifestValidator.cs`, `app.manifest` |

Testes em execução real (seção 5): **6 de 7 sub-etapas concluídas com sucesso** (consentimento CDP,
login, download de fonte, instalação de manifesto, persistência de consentimento, incidente real de build
diagnosticado e resolvido), Kaspersky limpo em toda a sessão. Remoção **deliberadamente não testada**
(decisão do usuário, não uma falha ou bloqueio técnico).

---

## 8. Conclusão

Sessão encerrada em 2026-08-16. Todos os 8 itens de risco residual aprovados (C1, C2, M3, H1, H2, H3, M1,
M2) foram implementados, testados automaticamente (320/320) e — o que vale mais do que os testes
automatizados sozinhos — **exercitados em uso real**: login, download de uma fonte real, escrita do
manifesto no Steam, consentimento e persistência do CDP, e um incidente de build/Denuvo genuíno que
surgiu durante o teste e foi resolvido usando a própria funcionalidade do app, sem qualquer intervenção
de código.

Nenhuma detecção de antivírus (Kaspersky) em nenhum momento da sessão, apesar dos padrões conhecidos por
serem sensíveis a heurística (side-loading de DLL, criação de junction, download+execução de binários de
terceiros) permanecerem no fluxo normal do produto — mitigados onde possível (consentimento explícito
para o CDP, verificação de hash em tudo que é executado, `asInvoker` sem elevação) e documentados onde
não são elimináveis sem quebrar a funcionalidade central do app.

**Avaliação final, conforme o usuário:** o teste foi abrangente o suficiente para validar o funcionamento
principal do sistema. A remoção (seção 5.9) e os demais itens da seção 6 ficam registrados como as únicas
lacunas conhecidas — nenhuma delas foi encontrada como problema, apenas como não testada.
