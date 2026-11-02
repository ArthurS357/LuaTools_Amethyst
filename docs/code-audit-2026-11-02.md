# Auditoria de código e segurança (release candidate) — LuaTools 1.7.12.8

**Data:** 2026-11-02
**Escopo:** as mudanças desde a 1.7.12: F-04 (auto-update), F-17, auditoria de exceções, limpeza de backups,
glassmorphism, remoção do CloudRedirect e a rodada de performance (`docs/performance-audit-2026-11-02.md`).
**Base auditada:** `9c11ed4` (main).

**Resultado:** nenhum achado **CRÍTICO** ou **ALTO**. Nenhuma mudança de código de produção nesta rodada; só
testes de regressão novos (modos de falha da leitura da lista de apps). Os achados MÉDIOS e BAIXOS ficam abaixo
como dívida técnica, com o motivo de cada um.

**Limites desta auditoria:** feita por leitura de código, grep e testes. Este ambiente é Linux e não roda a
interface WPF, então nada foi verificado visualmente (glass, capas). As skills nomeadas no pedido
(`csharp-typing`, `csharp-architecture-api`, `code-reviewer`, `steamtools-security`, `csharp-code-quality`,
`caveman`, `csharp-testing`) não existem neste ambiente; os critérios foram aplicados diretamente.

## Achados

| ID | Sev. | Área | Achado |
|----|------|------|--------|
| M1 | MÉDIA | Settings / DPAPI | Se o DPAPI falhar ao **proteger**, a chave Hubcap é gravada em texto puro no `settings.json` (com log). Trade-off documentado e anterior a esta rodada |
| M2 | MÉDIA | `SteamAppInfoCache` | `GetCached` lê o blob do disco na thread de UI na primeira vez de cada appid (dívida D2 do relatório de performance) |
| M3 | MÉDIA | Testes | A recuperação de `SteamAppListCache.LoadAsync` (catch → download → cache velho) não é testável de ponta a ponta: o caminho do cache é fixo em `%AppData%` |
| B1 | BAIXA | `UnlockerService` | `InstallAsync`/`GetStateAsync(UnlockerMode.CloudRedirect)` lançariam `InvalidOperationException` (`Def` usa `First`) em vez de falhar com mensagem. Inalcançável hoje |
| B2 | BAIXA | `MainWindow` | O handler de `SystemParameters.StaticPropertyChanged` (alto contraste) nunca é removido. A janela é singleton com a vida do app, então não vaza na prática |
| B3 | BAIXA | `CefInjectorService` | `luatools.js` alterado por fora do app (não pelo instalador) só entra no próximo início. Igual a antes do cache de script |
| B4 | BAIXA | `SaveBackupService` | Janela de corrida entre enumerar os saves (sem links) e abrir cada arquivo: um processo do mesmo usuário poderia trocar um arquivo por symlink e o conteúdo apontado iria para o zip do próprio usuário |
| B5 | BAIXA | `AmethystToolService` | `LastBackupDirectory` é um getter com efeito colateral (regrava o `installed.json` quando a pasta sumiu), executado na UI |
| B6 | BAIXA | `SteamAppListCache` | O `catch {}` do caminho "cache fresco" cai no download sem logar o motivo (arquivo bloqueado/corrompido fica invisível no log) |
| B7 | BAIXA | Testes | `MainWindow.ApplyGlass` não tem teste de unidade (precisa de janela/STA); a decisão (`GlassPolicy`) e o markup têm |

## Fase 1 — Segurança e arquitetura

### 1.1 CloudRedirect

- Grep de `CloudRedirect`, `cloud_redirect`, `CloudRedirectCLI`, `stfixer`, `ModeKind.Cli` em `src/`: sobram só o
  membro-túmulo do enum, comentários (ModeModels, UnlockerService, ActiveBackendPolicy, AmethystToolPlan,
  InstallManifest) e o texto histórico do changelog in-app.
- Nenhum `Process.Start` do app executa algo do CloudRedirect. Os que restam: URLs/explorer, `steam.exe`,
  `netsh` (urlacl local), `Steamless.CLI.exe` e `DepotDownloaderMod` (ambos com digest fixado).
- `ActiveBackendPolicy.ActiveMode` devolve `null` para `"CloudRedirect"` e para `"2"` → `ActiveBackend.None`.
  Mutação feita na rodada anterior: remover essa linha derruba 2 testes. Card, detecção, status e remoção
  partem de `SelectedMode`/`AllModes`, que nunca contêm o túmulo (ver B1 para a API pública).

### 1.2 Glassmorphism

- **Handles:** trocar `WindowBackdropType` só chama `DwmSetWindowAttribute` (WPF-UI 4.3.0 decompilado:
  `WindowBackdrop.ApplyBackdrop/RemoveBackdrop`); nenhum handle ou recurso nativo é alocado. O único handler
  que o WPF-UI registra (`Loaded`) acontece uma vez, entre `SourceInitialized` e `Loaded`.
- **Fallback:** `GlassPolicy.IsActive` exige Mica suportado (`IsOSWindows11OrNewer`) e `!SystemParameters.HighContrast`;
  senão a janela volta a `WindowBackdropType.None` + `SurfaceBaseBrush` (o visual de antes). O WPF-UI só
  força `None` em alto contraste quando o TEMA do app é HighContrast, e este app fixa Dark: a checagem do
  `SystemParameters` é o que realmente cobre esse caso. Ver B2.

### 1.3 Exceções engolidas

- Os 49 `catch` da auditoria `e4f2466` foram relidos nos serviços sensíveis (LuaInstaller, PluginInstaller,
  Unlocker, Protocol, HttpServer, CEF): todos eram vazios antes e continuam no mesmo comportamento, só que
  logando. Nenhum deles fica num caminho de integridade ou de credencial.
- **Integridade:** `AssetIntegrity.Matches` devolve `false` em `IOException`/`UnauthorizedAccessException`
  (fail-closed); `OfficialGithubDownloader` recusa, apaga o pacote e loga `REFUSED`; a busca do digest não tem
  try/catch (API inacessível = sem atualização).
- **DPAPI:** `Unprotect` falhando devolve `null` ("não configurado", pede a chave de novo), o que é seguro. `Protect`
  falhando grava em texto puro com log (M1).
- `catch { return true; }` restantes: `SteamShutdown.HasGone` ("processo já saiu"), a sonda da porta CDP
  ("porta ocupada por outro") e `ManifestFile.KeyLooksValid` (na dúvida, "a chave parece válida"). Este último é
  fail-open de propósito e documentado: só decide se mostra um erro melhor antes de um download que vai falhar;
  os portões fail-closed são `AssetIntegrity.Matches` (binário) e `IsSteamManifest` (o que chega ao depotcache).

### 1.4 Política do fork

Grep de `DonateKeys`, `SteamAutoCrack`, `Telemetry` em código, XAML, RESX e JS de `src/`: só aparecem em
comentários que explicam a remoção, no aviso `Mode_Warn_Telemetry` do card BetterSteamTools (29 idiomas), na
mensagem que recusa o feed de update do upstream (`AppUpdateSources`) e no changelog. Nenhum código funcional.

### 1.5 Path traversal / zip-slip

- **Restore e importação (`SaveBackupService`):** toda entrada passa por `FixAnalyzer.IsContained` e por
  `HasLinkBetween(anchor, destino)` na validação (tudo-ou-nada, antes de escrever qualquer arquivo) e de novo
  logo antes de cada escrita; `:` é recusado (ADS do NTFS); escrita atômica (`AtomicFile`). Importação exige
  `.zip` comum (sem link); restore exige um zip nosso, direto na pasta de backups ou numa pasta de jogo sem link.
- **Criação:** enumeração com `AttributesToSkip = ReparsePoint` e `!IsLink`; pasta do jogo que é link recusa.
  Ver B4.
- **Limpeza (`BackupService.DeleteTree`):** nunca entra em reparse point (desanexa o link sem apagar o alvo);
  revalida tudo antes de apagar; idade mínima de 5 min. TOCTOU residual de microssegundos documentado no código.

## Fase 2 — Performance e memória

- **Streams (`SteamAppListCache`):** `await using` em leitura e escrita (o handle fecha mesmo com exceção). Arquivo
  bloqueado ou corrompido lança (`IOException`/`JsonException`) e o `LoadAsync` cai no download e depois no
  cache velho; uma gravação que falha apaga o `.tmp` e deixa a lista anterior intacta. Agora coberto por testes
  (M3 cobre o que ainda falta). Ver B6.
- **`DecodePixelWidth` / `Freeze`:** bitmaps locais saem congelados; nenhuma referência é guardada pelo
  converter (`IgnoreImageCache`), então o GC libera quando o container recicla. A grade do Fixes é
  virtualizada; a lista de Downloads não é, mas é limitada (`MaxHistory = 100` + fila): ~6 MB de capas a 184 px
  contra ~39 MB antes. Sem risco realista de `OutOfMemoryException` por rolagem.
- **Cache do script CEF:** não usa `LastWriteTime` e não precisa. A chave é a identidade das strings em memória,
  que só mudam por `ReloadPluginFilesAsync`, chamado pela instalação/atualização (`InstallSourceAsync`) e pela
  desinstalação do plugin. O frescor é idêntico ao de antes do cache. Ver B3.

## Fase 3 — Testes

- **Modos de falha da lista de apps (novos):** JSON truncado, inválido e de formato errado lançam
  `JsonException` (nunca uma lista pela metade); arquivo bloqueado lança `IOException` e o handle é liberado
  depois; gravação que falha mantém a lista anterior e não deixa `.tmp`. No CI (Windows) e, os que não dependem
  de lock obrigatório, no harness Linux (`FileShare.None` é só consultivo no Linux).
- **Glass:** persistência no `settings.json` (padrão, desligar, religar explícito, predicado `empty`), VM aplica
  ao vivo e não reaplica ao abrir, e a tabela da política: sem Mica ou com alto contraste → sólido
  (`SurfaceBaseBrush`). Ver B7.
- **Mutação:** a comparação de SHA-256 do auto-update (`OfficialGithubDownloader.cs:96`, `if (!AssetIntegrity.Matches(...))`
  → `if (false && ...)`) derrubou 2 testes (`A_digest_mismatch_refuses_the_package_and_deletes_it`,
  `A_refusal_is_written_to_the_app_log`). Código restaurado, diff vazio. Na rodada anterior, a mesma prova foi
  feita para o fallback do CloudRedirect no `ActiveBackendPolicy` (2 testes caem).

## Sugestões para a dívida (quando houver tempo)

- **B1:** `Def` → `TryDef`; `InstallAsync` devolve `ModeInstallResult.Fail(Unlocker_Err_NoLongerAvailable)` e
  `GetStateAsync` devolve `ModeStatus.Unknown` para um modo sem definição.
- **B6:** logar o motivo no `catch` do caminho "cache fresco" antes de cair no download.
- **M3:** construtor interno com o caminho do cache (e a fonte de download) para testar a recuperação inteira.
- **M1:** em vez de gravar em texto puro, manter a chave só em memória na sessão e avisar na UI.
