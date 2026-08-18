<p align="center">
  <img height="336" alt="luatools" src="https://github.com/user-attachments/assets/54702ada-93a8-439b-ab3e-5cd73747ed46" />
</p>

# LuaTools Amethyst

**An unofficial, privacy-focused fork of [LuaTools](https://lua.tools).** Not affiliated with or endorsed
by the upstream project.

A Windows desktop client for managing Steam manifest/lua configurations, built with WPF on .NET 8.
It browses and installs manifest sources, edits `stplug-in` lua files (depot pinning, per-depot
enable/disable), manages unlocker modes, and injects a companion plugin into Steam's store pages. It
ships translated in 29 languages and auto-updates via Velopack.

Current version: **1.5.1** · Repository: <https://github.com/ArthurS357/LuaTools_Amethyst>

### Checking which build you are running

Open the **About** tab in the app. It shows the product name, the exact version, and the repository
updates come from. The window title and the navigation footer show the name and version too.

If the app ever warns that it "may not be LuaTools Amethyst", the binary that is running does not
identify itself as this fork — most likely an official LuaTools installer was run over it, which brings
back analytics and the Steam key upload. Reinstall from the repository above.

---

## What is different from the official build

| | Official | This fork |
|---|---|---|
| Umami telemetry | Per-launch ping, no opt-out anywhere in the UI, spoofed Chrome User-Agent to get past bot filtering | **Removed.** No analytics request is made at all |
| DonateKeys | **On by default.** Scraped per-depot `DecryptionKey` values out of Steam's `config.vdf` and uploaded them over plain HTTP to a bare IP | **Removed.** See below |
| Cleartext requests | Key upload + manifest lookups | Only one metadata lookup remains, and it announces itself |
| App auto-update | Points at the official release feed | **Off by default**; opt-in to your own repo, official repos refused |
| Theme | Default | "Amethyst" purple palette, centralised and contrast-checked |

### Why DonateKeys was not re-enabled

Reinstating it HTTPS-only was evaluated and rejected on evidence, not preference:

- The endpoint answers `401` over `http://` but `404` over `https://` — the route exists **only** on the
  cleartext entrypoint.
- Port 443 does complete a TLS handshake, but serves Traefik's **default self-signed placeholder
  certificate** (`CN=TRAEFIK DEFAULT CERT`), which is what Traefik returns when no certificate is
  configured. There is nothing to validate against.
- The host is a bare IP with no domain name, so a publicly trusted certificate cannot simply be pointed
  at it.

The only way to "use HTTPS" here would be to pin or ignore that self-signed certificate, which removes
peer authentication entirely — the decryption keys would go to whoever answered the connection. That is
worse than plain HTTP, because the padlock implies a protection that is not there. The feature stays out
until the operator publishes the endpoint on a domain with a CA-issued certificate; at that point it can
return as opt-in, default off. The full probe is recorded in
[`AppConfig.cs`](src/LuaToolsGui/AppConfig.cs).

---

## Updating

Automatic updates are **on**, and point at this fork's own repository:

```
https://github.com/ArthurS357/LuaTools_Amethyst
```

The app checks on launch and stages the update in the background; it is applied when you close the app.
You can also check on demand from the **About** tab ("Check for updates"), which uses exactly the same
update path as the automatic check.

To update **manually** instead, download the latest release from the repository and run the installer.

### Changing or disabling the update source

The compiled-in default can be overridden in `settings.json`:

```json
{ "AppUpdateRepos": ["https://github.com/you/YourFork"] }
```

List more than one for fallback — they are tried in order, which covers a primary repo becoming
unreachable (banned, DMCA'd, account removed) rather than merely being out of date.

To turn self-update **off** entirely, set it to an empty array. The app then makes no update request at
all:

```json
{ "AppUpdateRepos": [] }
```

The About tab always shows the feed that is actually in use, so you can confirm what took effect.

Entries are validated before use ([`AppUpdateSources.cs`](src/LuaToolsGui/Services/AppUpdateSources.cs)):

- `https://github.com/<owner>/<repo>` only. **`http://` is refused** — an update feed decides which
  executable replaces this one, so it is never accepted over a transport that can be rewritten.
- **The official LuaTools release repos are refused outright**, however they are spelled (casing,
  trailing slash, `.git`, `www.`). This is the backstop for the realistic mistake: someone pasting the
  upstream URL in to "get updates working" and quietly reinstating telemetry and the key upload.
- Rejected entries are logged to `plugin-backend.log` with the reason, so ignored config does not look
  like a broken app.

### Still: do not install official releases over this build

The blocklist protects the *auto*-updater. It cannot stop you running an official LuaTools installer by
hand — that restores telemetry and DonateKeys, in a program that still launches and looks the same.
The app warns at startup if the binary no longer identifies as LuaTools Amethyst, and the **About** tab
always shows what is actually running.

> **Scope:** all of the above concerns the app updating **itself**. Plugin, unlocker, Steamless and
> manifest downloads use entirely separate sources and are unaffected whether self-update is on or off.

---

## Remaining cleartext endpoint

Exactly one HTTP request survives, and the app tells you about it.

**`http://167.235.229.108/check_apis?appid=<id>`** — the source-availability lookup.

### What it is, precisely

It is **metadata only**. It returns a `{ source name → "available" | "unavailable" }` map. It never
transfers a manifest, a zip, a lua file or anything else that gets installed: downloading resolves a
source **by name** against lua.tools and fetches a **signed HTTPS** URL.

- **What is exposed:** the appid you are looking up, and nothing else. No account, token, API key or
  Steam credential is involved. An observer can also forge the answer, so treat "available/unavailable"
  as a hint rather than a fact — a forged reply can hide a source or advertise a dead one, but it cannot
  redirect a download.
- **Why it is still HTTP:** the server has no working TLS (probe above). Pointing the constant at
  `https://` would break the feature rather than secure it.

### Why it is not disabled by default

It is metadata, but it is not optional-feeling: **the source list you pick from IS its response.** With
it off, most users see no sources at all except the key-gated Hubcap one. Defaulting it off would
disable the app's main function to hide an appid, so the trade-off is made explicit instead of silently
chosen for you.

### Controlling it

**Stop the call entirely** (LuaTools then never contacts that host):

```json
{ "EnableSourceAvailabilityChecks": false }
```

Downloading still works for everything that doesn't depend on the lookup: Hubcap with your own API key,
drag-and-drop `.lua`/`.manifest`/`.zip`, and `luatools://` links. Turning it off degrades gracefully —
the source list is simply empty, not an error.

**Or keep it and tune the disclosure:**

```json
{ "InsecureMetadataNotice": "once" }
```

| Value | Behaviour |
|---|---|
| `"once"` | *(default)* One notice per session |
| `"always"` | A notice before **every** call — noisy on purpose, for auditing what the app contacts |
| `"off"` | No notice. The request still happens |

An unrecognised value falls back to `"once"` rather than `"off"`: a typo must not silently switch a
privacy disclosure off. The older `"WarnOnInsecureMetadata": false` is still honoured as `"off"`.

Everything else — sign-in, manifest/plugin/unlocker downloads, GitHub access — is HTTPS.

A second HTTP URL used to exist: a hardcoded `http://167.235.229.108/<appid>` download source in the
built-in fallback list. It turned out to be dead data (nothing ever read the field), so it was deleted
rather than gated.

---

## Downloaded fixes are screened before they touch your disk

Fixes and manifests arrive as archives that end up either in Steam's folders or inside a game's install
directory. Before anything is written, [`FixAnalyzer`](src/LuaToolsGui/Services/FixAnalyzer.cs) inspects
the staged download and **refuses** it on:

- **Path escapes (zip-slip)** — entries traversing out of the target folder with `..`, or using absolute
  or UNC paths. This closed a real hole: the fix extractor previously joined entry paths straight onto
  the game folder, so an entry named `C:\Windows\...` wrote there.
- **Duplicate destinations** — two entries resolving to the same file, a known way to have a checker
  inspect one payload while the extractor writes another.
- **Absurd size or shape** — implausible entry counts, oversized entries, and decompression bombs
  (measured by expansion ratio, and only above a size floor so ordinary compressible files are unaffected).
- **Dangerous lua** — `os.execute`, `io.open`, `loadstring` and friends, screened by the same denylist
  the installer uses, and screened a **second time** after de-obfuscation so `"os" .. ".execute"` or
  `"\x6f\x73"` is caught too.
- **Unreadable archives** — something that cannot be inspected is not extracted.

Things that are **recorded but allowed**, because blocking them would break legitimate fixes: executables
(a game fix is executables), nested archives, and lua lines that are not recognised manifest directives.
Every decision is written to `plugin-backend.log`, and a refusal is shown as a toast explaining why.

## Where Modes and the Plugin come from

The Mode and Plugin pages are **managers, not bundlers**: nothing they install ships inside this app. Each
one fetches a GitHub release, verifies it, and places the result. These are the sources:

| Page | Source repository | What it places | Where |
|---|---|---|---|
| Mode — SteamTools | `mendy-tools/verynotsusdllsthataredefnotstrelated` | `dwmapi.dll`, `xinput1_4.dll` | Steam root |
| Mode — BetterSteamTools | `OpenSteam001/OpenSteamTool` | the same two + `OpenSteamTool.dll` | Steam root |
| Mode — BST Nightly | `madoiscool/OST-Nightly` | the same three | Steam root |
| Mode — CloudRedirect | `Selectively11/CloudRedirect` | `CloudRedirectCLI.exe` (**executed**), `cloud_redirect.dll` | Steam root |
| Plugin | `madoiscool/LTSP` | `plugin.zip`, `winmm.dll` | `%AppData%`, Steam root |
| Manage — Remove Steam DRM | `atom0s/Steamless` | `Steamless.CLI.exe` (**executed**) | cache |

Every one of them is verified as described under [GitHub mirrors](#github-mirrors) — HTTPS, GitHub host,
**pinned to the owner/repo in this table**, and SHA-256 fail-closed — and `plugin.zip` additionally goes
through the same [archive screen](#downloaded-fixes-are-screened-before-they-touch-your-disk) the Fixes
page uses, so a decompression bomb inside a genuinely-published release is refused too.

### You are told before anything is applied

Verification used to be entirely silent: it worked, and you had no way to see that it had. Immediately
before an artifact is written — after it is downloaded and verified, before it goes anywhere near your
Steam folder — a notice shows what is about to happen:

```
Installing from madoiscool/LTSP
plugin.zip · version v1.2 · 2 file(s)
SHA-256 e3e2d22e098ff3fb
Verified: repository pinned · SHA-256 matched · archive screened
```

It carries a **Cancel** button and waits a few seconds before continuing on its own. Cancelling costs
nothing: at that point everything still lives in a temp folder and not a byte has been written to the
Steam root.

The notice is **advisory and deliberately fails open** — if it cannot be shown (a silent auto-update, the
HTTP bridge, no window), the install proceeds. That is not a hole. The checks that actually protect you
already ran and already refused anything they could not prove; making a UI fault block installs would
create an outage without adding safety. The short hash is there so you can compare it against what the
release publishes if you want to.

### Honest limits of that

**None of these are this fork's repositories, and this fork cannot make them so.** They are the upstream
and community projects that actually build these binaries; there is nothing to mirror that would not just
be a stale copy of someone else's work, re-signed by us and no more trustworthy for it. Two consequences
are worth stating plainly rather than leaving implied:

- **The pinning guarantees provenance, not intent.** It proves the bytes are the ones `madoiscool/LTSP`
  published. It cannot tell you whether that release is benign. If one of these projects ships something
  hostile, this app will faithfully verify it and install it.
- **There is a deliberate asymmetry with the app's own updates.** `AppUpdateSources` refuses to let the
  app update *itself* from `madoiscool/LuaTools`, because that repo publishes the official build with the
  telemetry and key-upload this fork removed. That refusal does not, and cannot, extend to the loader DLL —
  the plugin genuinely lives at `madoiscool/LTSP` and there is no fork of it to point at.

What is available today to reduce exposure, in rough order of effect:

1. **`PluginAutoUpdate` is off by default.** Nothing in the Steam root changes unless you press
   Install/Update on the Plugin page. Set it to `true` only if you want silent updates back.
2. **Read the pre-install notice** and press Cancel if the source or version is not what you expected.
3. `"GithubDownloadMirrors": []` and `"GithubApiMirrors": []` — direct connections only, which removes the
   mirror operator from the picture entirely and makes the repository pin belt-and-braces.
4. Don't install the Plugin at all. Modes, downloads and the Fixes page work without it; the Plugin only
   adds the "Add via LuaTools" button on Steam store pages.

Running a curated mirror of these projects is the one real alternative, and it is a maintenance commitment
(tracking five upstreams, re-publishing, and owning the delay when a fix lands upstream and not on the
mirror) rather than a code change. It is not done here, and the code does not pretend otherwise.

### If an install is refused

A refusal is the app doing its job — it could not prove the bytes were what the project published, so it
stopped. The message names which check failed. Do **not** work around it by disabling verification; there
is deliberately no setting for that. Instead:

1. **Try again.** The most common cause is a mirror serving a stale or truncated response. Retrying often
   hits a different mirror, or GitHub directly.
2. **Cut the mirrors out.** Set `"GithubDownloadMirrors": []` and `"GithubApiMirrors": []` in
   `settings.json` and retry. If it now succeeds, a mirror was the problem.
3. **Install by hand.** Go to the release page for the repository in the table above, download the asset
   yourself over HTTPS, and compare its SHA-256 against the digest GitHub shows for that asset:
   ```powershell
   Get-FileHash .\winmm.dll -Algorithm SHA256
   ```
   Only if they match, place the file yourself: loader DLLs and Mode DLLs go in the Steam root (next to
   `steam.exe`, with Steam fully closed); `plugin.zip` extracts to `%AppData%\LuaToolsGui\plugin`. You are
   performing the verification the app would have performed — if the hashes differ, do not use the file.
4. **A release with no published digest cannot be verified at all.** GitHub only began populating the
   asset `digest` field in mid-2025, so older releases carry none. The app refuses those rather than
   guessing, with one audited exception (`SteamlessPinnedSha256`, a hash compiled into this build).

## Configuration

Settings live in `%AppData%\LuaToolsGui\settings.json`. Most are managed from the Settings page; the
keys below are advanced and have no UI. The file is only written when something is worth persisting, so
it may not exist until you change a setting — you can create it by hand.

### GitHub mirrors

github.com is unreachable or throttled in some regions, so any GitHub request is tried **direct first**,
then through mirror prefixes until one works. The defaults are public third-party proxies. Point them at
your own, or disable them entirely.

```json
{
  "GithubDownloadMirrors": ["https://your-mirror.example/"],
  "GithubApiMirrors": ["https://your-api-proxy.example/"]
}
```

- Each entry is a **prefix** and must end with `/`. Requests become `<prefix>https://github.com/...`.
- `[]` disables mirrors entirely — direct connections only. This is the most private option: no third
  party sees which releases you fetch.
- Omitting a key keeps the compiled-in default.
- The two lists are separate on purpose: public proxies serve downloads but `403` the REST API, and the
  API proxy `400`s downloads. Mixing them just wastes a round trip.

Downloads stay integrity-checked regardless of which mirror serves them. Any asset that gets executed, or
placed where another program will load it, is pinned three ways before a byte is written:

1. **HTTPS, GitHub-owned host** — a plaintext URL is modifiable in transit no matter what is checked later.
2. **The publishing repository** — the URL must be that project's own
   `github.com/<owner>/<repo>/releases/download/…`. This one matters more than it looks: the download URL
   and the SHA-256 come from the *same* release JSON, and an API mirror can serve that JSON. Host pinning
   alone left a hostile mirror free to name any *other* github.com repository and hand over that payload's
   matching hash — both checks would pass and the file would still land next to `steam.exe`.
3. **SHA-256, fail-closed** — a missing or malformed digest is a refusal, not a skipped check.

A hostile mirror can therefore still lie, but only about *which release of the real project* you get — it
cannot substitute a different project's content.

### Privacy and update keys

| Key | Default | Effect |
|---|---|---|
| `EnableSourceAvailabilityChecks` | `true` | `false` stops the cleartext lookup entirely (see above) |
| `InsecureMetadataNotice` | `"once"` | `"always"` / `"off"` — how often the lookup is disclosed |
| `AppUpdateRepos` | this fork's repo | Where the app updates itself from. `[]` disables self-update |
| `PluginAutoUpdate` | `false` | `true` lets the store-page plugin update itself unattended |

`PluginAutoUpdate` defaults to **off**, and that is a deliberate change from earlier builds. With it on,
an out-of-date plugin is updated the moment you open Steam: the new release is downloaded, `winmm.dll` in
the **Steam root** is replaced, and Steam is stopped and restarted — no prompt. That is the most powerful
thing this app does, and it was previously happening without asking. `AppUpdateRepos` does **not** cover
it; that key governs only the app updating itself.

Left off, the plugin still updates — just when you press Install/Update on the Plugin page, which tells you
an update is available. Turn it on with `"PluginAutoUpdate": true` if you prefer the old behaviour.

A full example:

```json
{
  "AppUpdateRepos": ["https://github.com/ArthurS357/LuaTools_Amethyst"],
  "EnableSourceAvailabilityChecks": true,
  "InsecureMetadataNotice": "always",
  "PluginAutoUpdate": true,
  "GithubDownloadMirrors": []
}
```

---

## Building

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (the released installer only
needs the .NET 8 **Desktop Runtime**).

```bash
dotnet build LuaToolsGui.sln -c Release
```

```bash
dotnet test LuaToolsGui.sln -c Release
```

```bash
py scripts/check-i18n.py
```

All three must pass before a release. `check-i18n.py` validates the 29 translation files against the
English baseline — key parity, no duplicate keys, valid XML, matching `{0}` placeholders, and that
`Strings.Designer.cs` exposes every key. It resolves the repository from its own location, so it can be
run from any working directory (`--root` overrides that).

### Producing a release binary

Use the script rather than pasting the `dotnet publish` line by hand — it resolves the repository root from
its own location, so there is no drive letter to edit, and it prints the artifact path instead of leaving
you to reconstruct it:

```powershell
.\scripts\build-release.ps1
```

It publishes a self-contained single-file `win-x64` build and writes
`src/LuaToolsGui/bin/Release/<tfm>/win-x64/publish/LuaTools.exe` (~165 MB). `-RepoRoot`, `-Configuration`
and `-Runtime` are accepted if you need to override any of them. A non-zero exit code means the publish
failed; nothing downstream should run.

This produces the **binary only**. Packaging it into an installer is a separate `vpk pack` step and is
deliberately not part of the script.

### Repository layout

```
src/LuaToolsGui/     the application (Models, Services, ViewModels, Views, Themes, Resources)
tests/               xUnit suite
scripts/             build-release.ps1, check-i18n.py
docs/                CHANGELOG and the security audit
.github/workflows/   build + test on Windows, i18n check on Linux
```

`LuaToolsGui.sln` and `Directory.Build.props` stay at the root because `dotnet`, the CI workflows and
Velopack all resolve them from there.

### Known: one remaining `cmd.exe` call

Three places used to build a shell command by pasting a path into a string —
`cmd.exe /c mklink /j "{path}"` and `cmd.exe /c rmdir "{path}"` — where the path came from
`SteamPathOverride` in `settings.json`. A quote in it closed the literal and cmd ran whatever followed.
All three now go through `DirectoryJunction`, which drives the NTFS reparse point through
`DeviceIoControl` directly: no shell, no command string, nothing to escape.

One call survives, in `App.RelaunchApp` (used after a language change):

```csharp
new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"")
```

It is **not exploitable**, for a specific reason rather than by luck: `exe` is
`Environment.ProcessPath`, supplied by the OS, and `"` is not a legal character in a Windows path — so the
interpolated value cannot carry the quote the attack needs. It is not reachable from `settings.json`, the
HTTP bridge, or any downloaded content.

It stays because the shell is doing real work here: waiting for this process's single-instance mutex to
release before launching the replacement. Rewriting it natively means redesigning the relaunch handshake,
which is a behaviour change to a release-critical path and not worth making for a non-issue. Anyone
touching it should keep `DirectoryJunction`'s approach in mind and not reintroduce a path-in-a-command-
string anywhere it *is* reachable.

### Known: `dotnet format --verify-no-changes` fails

It exits **2**, reporting 8 whitespace violations. This is expected and is **not** a regression — do not
"fix" it by reformatting.

The repository has no `.editorconfig`, so `dotnet format` falls back to the SDK's built-in defaults, which
this codebase never adopted. The violations are all pre-existing, in files untouched by recent work:

```
src/LuaToolsGui/AssemblyInfo.cs          src/LuaToolsGui/ViewModels/HomeViewModel.cs
src/LuaToolsGui/Models/ApiModels.cs      src/LuaToolsGui/ViewModels/ManageViewModel.cs
src/LuaToolsGui/Services/AppInfo/LaunchOptionsService.cs
src/LuaToolsGui/Services/LuaEditor.cs    src/LuaToolsGui/ViewModels/PagedListViewModel.cs
tests/LuaToolsGui.Tests/LaunchModStoreTests.cs
```

Running `dotnet format` to clear them would rewrite continuation-line indentation across those files and
bury real changes under formatting noise in every future diff.

**The fix, when someone takes it on:** add a versioned `.editorconfig` that encodes the style the codebase
actually uses, then reformat, **in its own branch and its own commit**, touching nothing else. Only after
that does `--verify-no-changes` belong in CI as a gate. Until then it is informational.

### A note on the WPF-UI dependency

The theme depends on WPF-UI's **internal** resource key names, which are not a public contract. The
package is therefore pinned to `[4.3.0]` in
[`LuaToolsGui.csproj`](src/LuaToolsGui/LuaToolsGui.csproj) — the bracket form makes NuGet refuse to
substitute another version instead of silently resolving one.

If you deliberately upgrade it, **run the app once**. A startup guard verifies the accent actually
applied and, if it did not, writes a `THEME:` line to `%AppData%\LuaToolsGui\crash.log` and shows a
warning. Without that check the failure is invisible: nothing throws, and the app just quietly reverts to
grey chrome with the Windows accent colour. `App.VerifyAccentApplied` documents how to re-map the keys.

---

## Uninstalling

Uninstalling through Windows **Apps & features** runs a cleanup hook that removes what Velopack itself
does not: the CDP marker next to `steam.exe`, the loader DLLs in the Steam root, the `luatools://`
protocol registration, and `%AppData%\LuaToolsGui` (which holds your saved token and API key).

The CDP marker matters most: while it exists, Steam opens an **unauthenticated debug port** on
`127.0.0.1:8080` on every launch, which lets any local process drive Steam's built-in browser under your
session. It is only ever created after an explicit consent prompt, and that consent should not outlive
the app.

### Manual verification checklist

The uninstall hook runs once, on a real machine, while the app is being deleted, and it deliberately
swallows its own errors so it can never block the uninstall. The automated tests in
[`UninstallCleanupTests.cs`](tests/LuaToolsGui.Tests/UninstallCleanupTests.cs) cover the logic against
temp directories, but the Velopack hook itself is only exercisable from a packaged install. Verify it by
hand after any change to the removal path:

1. Install a packaged build and let the store-page plugin install.
2. **Accept** the CDP consent prompt, so the marker is actually created.
3. Confirm the starting state:
   - `dir /a "<Steam>\.cef-enable-remote-debugging"` — the junction exists.
   - `<Steam>\winmm.dll` and `winmm_real.dll` exist.
   - `reg query "HKCU\Software\Classes\luatools"` returns a key.
   - `%AppData%\LuaToolsGui\` exists.
4. **Fully close Steam** (tray included). A running Steam locks `winmm.dll`; the hook reports the failure
   and continues rather than blocking, which is correct but leaves the file behind.
5. Uninstall via Windows Apps & features.
6. Confirm every item from step 3 is gone. The junction is the important one — if it survives, Steam
   keeps opening port 8080.
7. Check `%TEMP%\luatools-uninstall.log`. It is written **only** when a step failed; its absence means a
   clean pass.
8. Start Steam and confirm it launches normally with no leftover LuaTools behaviour.

Repeat step 5 on a machine where Steam was never installed — cleanup must complete without errors when
the Steam directory cannot be detected.

---

## Credits / Adjacent software

- [LuaTools](https://lua.tools) — the upstream project this is forked from
- [Millennium](https://steambrew.app/) — the Steam plugin framework whose injection API this app
  polyfills when Millennium isn't installed
- [Velopack](https://velopack.io/) — installer and auto-update framework

## Licence

MIT, same as upstream — see [LICENSE](LICENSE). The upstream copyright notice is retained; this fork
adds no additional restrictions.
