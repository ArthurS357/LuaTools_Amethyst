# Auditoria de performance — LuaTools 1.7.12.8 (build local)

**Data:** 2026-11-02
**Escopo:** caches em memória, carregamento de imagens, loops/polling, I/O de JSON e Freezables do tema.
**Regra da sessão:** implementar só correções seguras (sem mudar contrato público nem fluxo de threads);
o resto fica registrado como dívida técnica, com o motivo.

Os números de memória abaixo são **estimativas** (contas de tamanho de objeto do .NET, não medição com
profiler). Este ambiente é Linux e não roda a interface WPF; tudo o que foi corrigido tem teste automatizado
(CI `windows-latest` + harness `net10.0` no Linux), mas nenhum ganho foi medido em uma máquina real.

## Resumo

| # | Área | Achado | Ação |
|---|------|--------|------|
| 1 | `SteamAppListCache` | Lista de ~341 mil nomes lida com `ReadAllTextAsync` (string UTF-16 do arquivo inteiro) e gravada com `Serialize` para string | **Corrigido**: leitura e escrita por `Stream`, gravação atômica (temp + move) |
| 2 | `SteamAppListCache` | Sem limite nem expiração em memória | **Mantido de propósito** (é uma tabela de lookup, não um cache de uso) |
| 3 | `SteamAppInfoCache` | Cada blob `appdetails` (dezenas de KB, com HTML) virava string antes do parse, em 4 caminhos | **Corrigido**: `JsonDocument.Parse` direto do `FileStream` |
| 4 | `SteamAppInfoCache` | `GetCached` faz I/O de disco síncrono no hot path da grade na primeira vez de cada appid | **Dívida** |
| 5 | `SteamAppInfoCache` | `_cache`, `_filterCache`, `_overviewCache` sem limite | **Mantido** (limitados pela biblioteca do usuário; entradas pequenas) |
| 6 | `ImagePathToSourceConverter` | Capas decodificadas em tamanho cheio mesmo numa miniatura de 92×43; bitmap não congelado | **Corrigido**: `DecodePixelWidth` via `ConverterParameter` + `Freeze()` |
| 7 | `CefInjectorService` | Concatenava `polyfill + luatools.js` a cada ~1 s, precisando ou não | **Corrigido**: script composto em cache, refeito só quando o conteúdo muda |
| 8 | `CefInjectorService` | Polling de 150 ms | **OK**: roda no thread pool, com `Task.Delay`; backoff de 1 s em erro |
| 9 | `DownloadQueue` | Loop do pump | **OK**: espera um `SemaphoreSlim` (orientado a evento), sem polling |
| 10 | `DepotDownloaderService` | `while (!proc.WaitForExit(2000))` bloqueia uma thread | **Dívida** (é thread do pool, não a de UI) |
| 11 | `ThemeRepaint` / glass | Criação de `Brush`/`Freezable` em loop | **OK**: nada criado em loop |
| 12 | `Thread.Sleep` | Procurado no código todo | **Nenhum** encontrado |

## 1. Caches em memória

### `SteamAppListCache` (appid → nome)

- **Para que serve:** responder "qual o nome do appid X" para qualquer jogo, inclusive deslistado. A fonte
  (Morrenus) entrega a lista inteira (~341 mil entradas) num JSON só.
- **Limite / LRU:** **não faz sentido aqui**. Não é um cache de uso, é uma tabela completa: uma entrada
  despejada não volta sem reler o arquivo inteiro, e as consultas vêm de qualquer appid (Manage, Fixes,
  Downloads). Um LRU só trocaria memória por releituras de um arquivo de mais de 10 MB.
- **O que pesava de verdade era o I/O.** A leitura fazia `File.ReadAllTextAsync` (o arquivo inteiro como
  uma string UTF-16, ~2× o tamanho do arquivo, estimados ~24 MB transitórios), passava ao parser e descartava.
  A gravação fazia `JsonSerializer.Serialize(_names)` para uma string do mesmo tamanho e depois
  `WriteAllTextAsync`. **Corrigido:** `DeserializeAsync`/`SerializeAsync` direto no `FileStream` (buffer de
  64 KB). A gravação agora vai para `steam-applist.json.tmp` e é movida por cima do cache: um kill no meio
  deixa a lista anterior intacta, em vez de uma truncada (que antes custava um novo download de toda a lista
  no próximo início). O formato do arquivo não mudou (teste com um JSON gravado pela versão anterior).
- **Custo residual:** a tabela em si fica em memória (estimativa: ~110 B por entrada → ~35–40 MB). Ver dívida D1.

### `SteamAppInfoCache` (nome/capa, filtros, overview)

- **Limites:** `_cache`, `_filterCache` e `_overviewCache` não têm limite, mas só ganham entradas para os
  appids que o usuário de fato tem/abre (biblioteca), não para os 341 mil. As entradas são pequenas (nome +
  URL; campos de filtro; descrição curta + listas). Para uma biblioteca de alguns milhares de jogos, a ordem é
  de poucos MB. Um LRU traria complexidade (e releitura de disco ao rolar a grade) sem ganho real. **Mantido.**
- **I/O de JSON — corrigido:** `ReadInfoFromDetails`, `GetFilterData`, `GetOverview` e `GetGameDetails` liam
  o blob `details/<appid>.json` inteiro com `File.ReadAllText` antes do parse. Um blob `appdetails` traz
  `detailed_description` em HTML e passa de dezenas de KB; a grade e os filtros fazem isso uma vez por jogo.
  Agora um helper privado (`OpenDetails`) faz `JsonDocument.Parse` direto de um `FileStream`
  (`SequentialScan`, mesmo `FileShare.Read` do `ReadAllText`, então escrita concorrente se comporta igual).
  O marcador de deslistado `{}` é reconhecido pelo objeto vazio (`IsDelistedMarker`). `GetFullDetails`
  (público) não mudou.
- **Dívida D2:** ver abaixo (I/O síncrono no hot path).

## 2. Imagens na UI

`ImagePathToSourceConverter` já usava `CacheOption=OnLoad` (não trava o arquivo), mas decodificava a capa em
tamanho cheio em todo lugar. As capas são o `header.jpg` da Steam (460×215; apps novos chegam a 920×430).

**Corrigido:** o converter aceita `ConverterParameter` = largura de decode em pixels (int ou string numérica;
inválido/zero/negativo = tamanho cheio, nunca uma capa em branco) e congela o bitmap quando possível
(`CanFreeze`: arquivo local já decodificado; URL http ainda baixando fica sem congelar).

| Onde | Desenhado | Decode | Motivo |
|------|-----------|--------|--------|
| Downloads (linha) | 92×43 | **184** | 2× para telas a 200% |
| Fixes (tile da grade) | 248×116 | **460** | 2× seria 496, acima do `header.jpg`: decodificar maior que a fonte AUMENTA a memória (o WPF amplia no decode). 460 não muda nada no header clássico e corta 4× as capas novas de 920 |
| Fixes (painel de detalhe) | 1 por vez | cheio | uma imagem só; sem ganho que justifique |
| Manage (tile) | 248 | 248 | já usava `DecodePixelWidth = 248` + `Freeze()` (`ManageViewModel`) — não mexido |

Estimativa por miniatura de Downloads: 460×215×4 B ≈ 395 KB → 184×86×4 B ≈ 63 KB (~84% a menos).

## 3. Threads, loops e polling

- **Nenhum `Thread.Sleep`** no código.
- **`CefInjectorService.InjectionLoop`:** roda via `Task.Run` (thread pool, nunca na UI), `await Task.Delay(150)`
  por tick, descoberta de abas a cada 7 ticks (~1 s) e `Task.Delay(1000)` depois de erro (Steam fechada,
  CDP fora). Correto. **Corrigido:** o script injetado (`polyfill + "\n" + luatools.js`) era concatenado
  em todo tick de descoberta, houvesse ou não aba precisando de injeção, ou seja, duas cópias de uma string do
  tamanho do plugin por segundo durante a vida do app. Agora `ComposedScript()` guarda o resultado e só refaz
  quando `ReloadPluginFilesAsync` troca o polyfill ou o `luatools.js` (comparação por referência; só o
  próprio loop lê e grava o cache).
- **`DownloadQueue.PumpAsync`:** espera um `SemaphoreSlim` (`Kick`), sem polling; o trabalho de cada item roda
  em `Task.Run`. Correto.
- **`SteamGameLauncher`:** polling da Steam com `Task.Delay` e `ConfigureAwait(false)`. Correto.
- **`DepotDownloaderService.RunAsync`:** `while (!proc.WaitForExit(2000))` bloqueia uma thread durante todo o
  download do depot. A chamada vem de `DownloadQueue` dentro de `Task.Run`, então é uma thread do pool, não a
  de UI. Ver dívida D3.

## 4. JSON e I/O de arquivos

Tratado nos itens 1 e 3 (`SteamAppListCache`, `SteamAppInfoCache`). Restante, verificado e mantido:

- `LuaVault` lê arquivos `.lua` individuais (KB) e um índice pequeno com `ReadAllText`: sem ganho.
- `CacheService` (`cache.json`) e `SettingsService` (`settings.json`): arquivos pequenos.

## 5. WPF / Freezables

- **`ThemeRepaint.Apply`/`Retint`:** só **mutam** brushes existentes (`brush.Color = …`). O único objeto criado
  é um `DynamicResourceExtension` por brush, e só na primeira pintura (guardado por `IsExpression`): não
  cresce a cada troca de accent. O loop de `Retint` percorre os recursos uma vez por troca (ação do usuário).
- **Glassmorphism (`MainWindow.ApplyGlass`):** só `SetResourceReference` para chaves existentes
  (`GlassShellBrush`/`SurfaceBaseBrush`); nenhum brush novo, nenhum freeze. `GlassShellBrush` é declarado
  com `{DynamicResource}` e continua mutável (há teste para isso).
- **Bitmaps:** agora congelados quando possível (item 2), o que é o uso recomendado para imagens e não
  interfere no repaint do tema (que só envolve brushes).

## Dívida técnica (não implementado, com motivo)

- **D1. `SteamAppListCache` inteiro em memória (~35–40 MB estimados).** Opções: `FrozenDictionary` depois da
  carga (menos overhead que `ConcurrentDictionary`) ou índice em disco/SQLite com consulta sob demanda. Muda
  a estrutura que o download preenche concorrentemente e o modelo de leitura de todos os chamadores; não é
  uma correção segura nesta sessão. Ganho intermediário possível: desserializar direto na estrutura final
  (hoje há um `Dictionary` temporário copiado para o `ConcurrentDictionary`, pico de memória dobrado na carga).
- **D2. `SteamAppInfoCache.GetCached` faz I/O síncrono na UI.** Na primeira renderização de cada appid, lê o
  blob do disco na thread de UI (memoizado depois). Com milhares de jogos, a primeira rolagem da grade pode
  engasgar. Corrigir exige API assíncrona ou pré-aquecimento em background e mudança nos bindings da grade.
  Esta sessão só reduziu o custo de cada leitura (item 3).
- **D3. `DepotDownloaderService`: `WaitForExit(2000)` em loop.** Bloqueia uma thread do pool por download de
  depot. Trocar por `WaitForExitAsync` + verificação de silêncio por timer muda a lógica do watchdog (que mata
  o processo após X minutos sem saída); vale fazer com teste dedicado de watchdog.
- **D4. Polling do CDP com Steam fechada.** Com a Steam fechada o loop tenta a cada ~1 s (falha + backoff).
  Barato, mas poderia espaçar (ex.: 5 s) quando não há abas. Mudaria a latência de injeção logo após abrir a
  Steam, então fica para quando houver medição.
- **D5. `HttpClient` por serviço** (já registrado na auditoria anterior): sem `IHttpClientFactory`; os
  serviços são singletons, então não há esgotamento de sockets, só sem rotação de DNS.

## Testes

- `PerformanceAuditTests` (CI Windows): capa decodificada na largura pedida mantendo proporção, tamanho cheio
  sem parâmetro, bitmap local congelado, parâmetro inválido = tamanho cheio, round trip da lista por stream,
  formato antigo ainda carrega, gravação substitui por inteiro e não deixa `.tmp`.
- Harness Linux (`net10.0`): o mesmo round trip da lista, incluindo uma lista de 100 mil entradas com Unicode.
