# Roteiro de teste manual — LuaTools Amethyst 1.4.0

O que os 492 testes automatizados **não** cobrem: a instalação empacotada, o Steam real, a junction em
disco, e o toast aparecendo de fato. Este roteiro é a verificação que só uma máquina real faz.

> **Antes de começar.** Faça isto numa máquina onde perder a instalação do Steam não seja um problema, ou
> ao menos com backup de `<Steam>\config\`. Vários passos param e reiniciam o Steam.
>
> Anote o caminho da sua instalação do Steam — abaixo aparece como `<Steam>`.

---

## Preparação

```powershell
# Estado inicial limpo. Feche o Steam COMPLETAMENTE (inclusive a bandeja) antes.
Get-Process steam -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item "$env:APPDATA\LuaToolsGui" -Recurse -Force -ErrorAction SilentlyContinue
```

Confirme que a raiz do Steam não tem resíduo:

```powershell
Get-ChildItem "<Steam>" -Force -Include winmm.dll,winmm_real.dll,dwmapi.dll,xinput1_4.dll,.cef-enable-remote-debugging
```

Deve retornar vazio. Se não, remova à mão antes de seguir.

---

## 1 — Instalação e identidade da build

1. Instale o pacote publicado e abra o app.
2. Vá em **About**.

**Critérios de aceite**
- [ ] A versão exibida é **1.4.0**, não 1.3.0. (Se mostrar 1.3.0, o `Version` do `.csproj` não chegou ao
      `InformationalVersion` — é o bug que já aconteceu antes, ver comentário no `.csproj`.)
- [ ] O repositório mostrado é `ArthurS357/LuaTools_Amethyst`.
- [ ] **Não** aparece o aviso de startup "esta não é uma build Amethyst".
- [ ] A interface está roxa (Amethyst), não cinza. Cinza significa que o tema WPF-UI silenciosamente não
      aplicou — verifique `%AppData%\LuaToolsGui\crash.log` por uma linha `THEME:`.

---

## 2 — O aviso de segurança aparece (o novo comportamento)

1. Vá na aba **Plugin**.
2. Pressione **Install**.
3. Aceite o prompt de reinício do Steam.
4. **Observe o canto inferior direito imediatamente.**

**Critérios de aceite**
- [ ] Um toast aparece **antes** de qualquer arquivo ser escrito, com:
      - título `Installing from madoiscool/LTSP`
      - `plugin.zip · version <tag> · <n> file(s)`
      - uma linha `SHA-256` com 16 caracteres hexadecimais
      - `Verified: repository pinned · SHA-256 matched · archive screened`
- [ ] Há um botão **Cancel**.
- [ ] Se você **não** fizer nada, o toast some após ~6s e a instalação continua sozinha.

### 2b — Cancelar realmente cancela

Desinstale o plugin, repita, e desta vez **pressione Cancel dentro dos 6 segundos**.

- [ ] A instalação para com "Cancelled before anything was installed."
- [ ] `<Steam>\winmm.dll` **não** existe.
- [ ] `%AppData%\LuaToolsGui\plugin` **não** existe.
- [ ] `Get-ChildItem $env:TEMP -Filter "luatools-plugin-*"` está vazio (a pasta temporária foi limpa).

O ponto do teste é este último conjunto: cancelar não pode deixar meio-estado.

### 2c — O mesmo aviso na aba Modo

1. Vá em **Mode**, escolha um modo, confirme o fechamento do Steam.

- [ ] O toast aparece com o `owner/repo` **daquele modo** (não o do plugin) — p.ex.
      `Installing from OpenSteam001/OpenSteamTool`.
- [ ] Para um modo em zip, a linha de checagens **não** inclui `archive screened` — modos extraem por
      allowlist de nomes, não por caminho, e o app não deve alegar uma checagem que não fez.

---

## 3 — `PluginAutoUpdate` está desligado por padrão

1. Com o plugin instalado e **sem** nunca ter criado `settings.json`, feche o app.
2. Abra o Steam e espere o app subir junto.

**Critérios de aceite**
- [ ] Nenhuma troca de DLL acontece sozinha; o Steam **não** é reiniciado por conta própria.
- [ ] Se houver atualização disponível, a aba Plugin mostra o botão **Update** — a atualização continua
      alcançável, só deixou de ser silenciosa.

### 3b — O opt-in funciona e persiste

Crie `%AppData%\LuaToolsGui\settings.json` com **apenas**:

```json
{ "PluginAutoUpdate": true }
```

Abra o app, mude alguma configuração qualquer na tela de Settings, feche, e reabra.

- [ ] O arquivo `settings.json` **ainda existe** e ainda contém `"PluginAutoUpdate": true`.

> Este passo tem um motivo específico: a chave estava faltando no predicado de persistência, e o sintoma
> era o arquivo ser **apagado** quando ela era a única alteração. Corrigido e coberto por teste, mas vale
> confirmar no arquivo real.

---

## 4 — Download bloqueado (verificação recusando)

Não dá para forjar uma resposta do GitHub sem um proxy, então force pelo caminho do mirror.

Em `settings.json`:

```json
{ "GithubDownloadMirrors": ["https://example.invalid/"] }
```

Depois **desconecte a rede** e tente instalar um Modo.

**Critérios de aceite**
- [ ] A instalação falha com uma mensagem sobre não alcançar o GitHub — **não** com uma exceção não
      tratada, e **não** silenciosamente.
- [ ] Nada foi escrito em `<Steam>`.
- [ ] O app continua utilizável (nada travou).

### 4b — Host não confiável é recusado antes do primeiro byte

Este é coberto por teste automatizado (`DownloadTrustTests`), mas se quiser confirmar à mão: qualquer URL
de asset que não seja `https://github.com/<owner>/<repo>/releases/download/…` do repositório esperado é
recusada **antes** da requisição. A mensagem de erro mostra só esquema e host, nunca o caminho ou a query.

---

## 5 — ZIP malicioso é bloqueado

Precisa de uma release controlada, então este é o passo mais trabalhoso. Se não puder montá-lo, os casos
estão cobertos por `PluginArchiveScreeningTests` (16 testes) e você pode pular para o passo 6.

Se puder: publique num repositório de teste um `plugin.zip` contendo uma entrada chamada
`..\..\..\evil.js`, aponte o app para ele e tente instalar.

**Critérios de aceite**
- [ ] A instalação é recusada com `plugin.zip was refused by the safety check: … escapes the target folder`.
- [ ] Nenhum arquivo aparece fora de `%AppData%\LuaToolsGui\plugin`.
- [ ] Especificamente, **nada** foi escrito três níveis acima dessa pasta.

---

## 6 — Junction CDP: criação e remoção

Este é o comportamento reescrito nesta versão (era `cmd.exe /c mklink`).

Com o plugin instalado e o consentimento CDP **aceito**:

```powershell
Get-Item "<Steam>\.cef-enable-remote-debugging" -Force | Select-Object Name, Attributes, LinkType, Target
```

**Critérios de aceite**
- [ ] Existe.
- [ ] `Attributes` inclui `ReparsePoint`.
- [ ] `LinkType` é `Junction` — **não** `SymbolicLink`. Se for symlink, algo trocou a implementação e vai
      falhar em máquinas sem Developer Mode.
- [ ] O `Target` aponta para um caminho que não existe. Isso é intencional.
- [ ] Você **não** precisou rodar o app como administrador para chegar até aqui. É o ponto principal da
      escolha por junction.

### 6b — Caminho do Steam com espaço e acentuação

Se puder, instale o Steam (ou aponte `SteamPathOverride`) para algo como
`D:\Jogos e Programas\Steam Configuração\`.

- [ ] A junction é criada normalmente. O código não escapa mais nada, então caminhos "difíceis" devem
      simplesmente funcionar.

---

## 7 — Desinstalação e limpeza

1. Feche o Steam **completamente**, bandeja inclusive. (Um Steam rodando trava `winmm.dll`; o hook reporta
   a falha e continua, o que é correto, mas deixa o arquivo para trás.)
2. Desinstale por **Aplicativos e Recursos** do Windows.

**Critérios de aceite**
- [ ] `<Steam>\.cef-enable-remote-debugging` sumiu. **Este é o mais importante** — enquanto existir, o
      Steam abre a porta de depuração não autenticada 8080 a cada inicialização.
- [ ] `<Steam>\winmm.dll` e `winmm_real.dll` sumiram.
- [ ] `reg query "HKCU\Software\Classes\luatools"` não retorna nada.
- [ ] `%AppData%\LuaToolsGui\` sumiu (é onde ficam o token salvo e a chave de API).
- [ ] `%TEMP%\luatools-uninstall.log` **não existe**. Ele só é escrito quando um passo falha; a ausência
      dele é o sinal de sucesso.

### 7b — A remoção não destrói o alvo da junction

Regressão específica desta versão. Antes de desinstalar, aponte o marcador para uma pasta **real** com
conteúdo:

```powershell
Remove-Item "<Steam>\.cef-enable-remote-debugging" -Force -Recurse
New-Item -ItemType Directory "C:\temp\alvo-canario" | Out-Null
"não posso sumir" | Set-Content "C:\temp\alvo-canario\canario.txt"
New-Item -ItemType Junction -Path "<Steam>\.cef-enable-remote-debugging" -Target "C:\temp\alvo-canario"
```

Desinstale, e então:

- [ ] `C:\temp\alvo-canario\canario.txt` **ainda existe**.

Se sumiu, a remoção está descendo pela junction em vez de cortá-la — falha grave, e o teste
`Remove_severs_the_link_and_leaves_the_target_contents_alone` deveria ter pego.

### 7c — Máquina sem Steam

Repita a desinstalação numa máquina onde o Steam nunca foi instalado.

- [ ] Completa sem erros. Nenhum diálogo, nenhum log de falha.

---

## Resumo de aceite

| # | Passo | Aceite |
|---|---|---|
| 1 | Instalação e About | versão 1.4.0, tema roxo, repo correto |
| 2 | Aviso de segurança | toast com origem/hash/checagens, Cancel funciona, sem meio-estado |
| 3 | `PluginAutoUpdate` | off por padrão; opt-in sobrevive a reload |
| 4 | Download bloqueado | falha limpa, nada escrito, app vivo |
| 5 | ZIP malicioso | recusado, nada fora da pasta destino |
| 6 | Junction CDP | `LinkType = Junction`, sem elevação |
| 7 | Desinstalação | marcador, DLLs, registro e AppData removidos; alvo da junction intacto |

Falha em **6** (virou symlink) ou **7b** (alvo apagado) é bloqueante para release. As demais, avalie caso
a caso.
