# Auditoria — selecao manual de fonte do plugin (1.6.2)

Data: 2026-08-27 · Escopo: `PluginSource`, `PluginSourceResolver`, `PluginSourceSelection`,
`PluginLoaderPolicy`, `PluginInstallerService`, `PluginViewModel`, `PluginView.xaml`, `SettingsService`,
`AppConfig`.

Registro de engenharia da revisao que acompanhou a troca do fallback automatico pela escolha manual de
fonte por criador. As notas de release ficam em [`CHANGELOG.md`](CHANGELOG.md); o que esta aqui e o
raciocinio, os achados e o porque de cada correcao — util para quem for mexer nesses arquivos depois, nao
para quem so quer saber o que mudou na versao.

## Verificado sem alteracao

- **Portao fail-closed por fonte.** `PluginSourceResolver.Verify` exige tag, todos os assets obrigatorios,
  `browser_download_url` fixado ao proprio `owner/repo` da fonte e sha256 publicado por asset. Digest
  ausente e recusa, nao check pulado. Uma fonte falha inteira — os assets de uma nunca se misturam aos de
  outra.
- **O pin roda duas vezes, de proposito.** Em `Verify` (antes de baixar um byte, para virar erro nomeado) e
  de novo dentro de `GithubProxy.DownloadAssetAsync`. Os dois recusam as mesmas URLs.
- **`settings.json` seleciona, nunca nomeia.** O slug persistido e validado contra o catalogo compilado no
  momento do uso. Valor que nao casa e ignorado e o padrao vale. Coberto por
  `PluginSourceSelectionTests` incluindo `attacker/payload`, confusao de prefixo e URL completa.
- **Precedencia e migracao.** `escolha do usuario -> fonte do manifesto -> padrao do catalogo`. Manifesto
  antigo sem `Source` le como upstream, que e a unica coisa que poderia ter sido; instalacao existente nao
  e migrada por atualizacao do app.
- **Troca de fonte e instalacao completa.** `InstallSourceAsync` e o mesmo caminho de Instalar/Atualizar. A
  preferencia so e gravada depois do sucesso; falha deixa o estado anterior intacto.
- **Sem residuo da implementacao anterior.** Varredura por `_bytesFailedSources`, `InstallOutcome`,
  `Choose`, `Usable(`, selos de reserva e qualquer caminho de fallback automatico: nada sobrou em codigo,
  XAML, recursos ou testes.

## Achados e correcoes

### 1. A pagina afirmava o que nao sabia sobre o loader

`UpdateAvailable` e `DllMatches` voltam `false` em dois casos completamente diferentes: esta tudo em dia,
ou **nao houve release nenhum para comparar** (offline, ou fonte ativa quebrada). A pagina lia o segundo
como o primeiro e mostrava, no mesmo card, tres coisas que nao podem ser verdade juntas:

- a caixa de erro vermelha dizendo que a fonte nao pode ser alcancada;
- a pilha verde `Atualizado` na linha da versao;
- o aviso ambar `Desatualizado` na linha do loader.

Duas dessas nunca foram estabelecidas. Pior no caso ambar: manda o usuario atualizar usando exatamente a
fonte que acabou de falhar.

**Correcao.** A regra saiu do view-model e virou `Services/PluginLoaderPolicy.cs`, tipo puro no mesmo
padrao de `ActiveBackendPolicy` / `StartupPlan` / `PluginSourceResolver` — sem disco, sem rede, sem
relogio. Expoe `IsInstalled`, `LatestKnown`, `Loader` e `ShowUpToDate` sobre um `PluginStatus`. O enum
`PluginLoaderState` ganhou um quarto valor, `Unverifiable`: instalado, e so isso e o que se sabe (icone
neutro `Info24`, texto `Instalado`). O motivo continua na caixa de erro logo acima.

Efeito colateral desejado: os quatro flags do XAML passam a sair de **um** valor de enum, entao a linha nao
pode desenhar dois icones nem nenhum. A exclusividade virou estrutural em vez de asserida.

### 2. Isolamento entre auto-update e fontes de conteudo so cobria upstream

`ContentSourceIsolationTests` conferia apenas `madoiscool/LTSP` contra o feed de auto-update. A fonte
padrao nova, `ArthurS357/Front-end-Amethyst`, fica sob **o mesmo owner** do repositorio de auto-update
(`ArthurS357/LuaTools_Amethyst`) — que e justamente o par que uma limpeza descuidada juntaria.

Vale tambem para a lista de bloqueio: `madoiscool` publica tanto o app oficial quanto um plugin que o
usuario pode legitimamente escolher, entao ela precisa distinguir `madoiscool/LuaTools` de
`madoiscool/LTSP` em vez de casar pelo owner. Os dois testes agora iteram `AppConfig.PluginSources`
inteiro.

### 3. Nenhum teste de paridade XAML <-> ViewModel para a pagina Plugin

Binding de WPF falha em SILENCIO: um caminho com nome errado renderiza um card cujo botao *Usar esta fonte*
nunca aparece, sem erro de build e sem nada na tela. A pagina Modos ja tinha `ModeUninstallBindingTests`
por esse motivo; a Plugin ganhou ~20 bindings novos sem equivalente. Novo `PluginSourceBindingTests`, mesmo
formato.

### 4. Chave i18n fora da verificacao, e varredura de residuo estreita

`Plugin_Row_Source` ficou de fora da lista de chaves conferidas (19 checadas, 20 adicionadas). A checagem
de residuo `Plugin_Source_Fallback` varria 2 dos 30 `.resx` — um residuo sobrevivendo num dos 28 traduzidos
e exatamente a copia que um passe futuro de "restaurar traducoes faltando" reintroduziria. Ambas
corrigidas.

As outras 28 linguas caem em ingles para as chaves novas ate serem traduzidas: comportamento padrao de
satelite do .NET, e o mesmo que ja acontece a cada release antes do passe de traducao.

### 5. README se contradizia sobre troca de fonte

Uma secao afirmava "nothing switches source on your behalf"; outra dizia que auto-update aplica uma troca
feita a mao em `settings.json`. As duas descreviam coisas diferentes sem dizer isso. Reescritas: a primeira
agora fala de **falha** nunca trocar de fonte, e nomeia as duas unicas coisas que trocam (o botao e a
chave); a segunda explica que editar a chave **e** trocar de fonte, e que o efeito chega sem prompt na
proxima atualizacao automatica. Com link cruzado entre elas.

## Achados na propria auditoria (segunda passada)

A primeira rodada de correcoes foi revisada e tinha dois problemas serios:

- **Teste tautologico.** `The_loader_states_are_mutually_exclusive_and_total` reimplementava as formulas do
  view-model dentro do teste e depois testava a reimplementacao. Trocar `DllUnknown` por `false` em
  producao mantinha o teste verde. Removido e substituido por `PluginLoaderPolicyTests`, que exercita
  `PluginLoaderPolicy` de verdade, mais um teste de pareamento enum <-> flag do XAML.
- **A correcao principal foi entregue sem teste de regressao.** `RefreshAsync` dependia de
  `PluginInstallerService` concreto (9 dependencias, sem interface), entao nao havia como cobri-la. Foi o
  que motivou extrair `PluginLoaderPolicy`. Alternativas consideradas e recusadas: extrair
  `IPluginInstaller` (introduziria o primeiro mock de servico de uma suite de 1200+ testes que nao tem
  nenhum) e tornar `GetStatusAsync` virtual (exigiria construir o servico com nove `null!`).
- **Data fixa em `ChangelogTests`.** `Released.Should().Be("2026-08-27")` so passou no bump 1.6.2 por
  coincidencia — a release saiu no mesmo dia da 1.6.1. Substituida por `Release_dates_never_go_backwards`,
  que checa o que estaria de fato errado (entrada nova datada antes da que ela sucede) e nao precisa de
  edicao a cada bump.
