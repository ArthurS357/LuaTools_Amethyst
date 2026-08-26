<p align="center">
  <img height="336" alt="luatools" src="https://github.com/user-attachments/assets/54702ada-93a8-439b-ab3e-5cd73747ed46" />
</p>

# LuaTools Amethyst

**An unofficial, privacy-focused fork of [LuaTools](https://lua.tools).** Not affiliated with or endorsed
by the upstream project.

A Windows desktop client for managing Steam manifest/lua configurations, built with WPF on .NET 10.
It browses and installs manifest sources, edits `stplug-in` lua files (depot pinning, per-depot
enable/disable), manages unlocker modes, launches games through Steam, and injects a companion plugin
into Steam's store pages. It ships translated in 29 languages and auto-updates via Velopack.

Current version: **1.6.0** · Repository: <https://github.com/ArthurS357/LuaTools_Amethyst>

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
(The file keeps that name for continuity — it is the app's only log, not just the plugin bridge's. The
class behind it is `AppLog`.)

## Where Modes and the Plugin come from

The Mode and Plugin pages are **managers, not bundlers**: nothing they install ships inside this app. Each
one fetches a GitHub release, verifies it, and places the result. These are the sources:

| Page | Source repository | What it places | Where |
|---|---|---|---|
| Mode — AmethystTool | `ArthurS357/BetterSteamTools-Amethyst` | `AmethystTool.dll`, `amethysttool.toml`, `dwmapi.dll`, `xinput1_4.dll` | Steam root |
| Mode — BetterSteamTools | `OpenSteam001/OpenSteamTool` | `dwmapi.dll`, `xinput1_4.dll`, `OpenSteamTool.dll` | Steam root |
| Mode — BST Nightly | `madoiscool/OST-Nightly` | the same three | Steam root |
| Plugin | `madoiscool/LTSP` | `plugin.zip`, `winmm.dll` | `%AppData%`, Steam root |
| Manage — Remove Steam DRM | `atom0s/Steamless` | `Steamless.CLI.exe` (**executed**) | cache |

Two Modes are no longer offered on the page. **SteamTools** (`mendy-tools/verynotsusdllsthataredefnotstrelated`,
`dwmapi.dll` + `xinput1_4.dll`) is retired: upstream stopped publishing updates for it, so LuaTools will not
install it any more. **CloudRedirect** (`Selectively11/CloudRedirect`) is hidden while the mode is broken.
Both definitions stay in the app so that an existing install of either keeps its uninstall record and keeps
its proxy DLLs protected from being removed by something else — and if SteamTools is still your active Mode,
its card stays on the page so you can uninstall it.

Every one of them is verified as described under [GitHub mirrors](#github-mirrors) — HTTPS, GitHub host,
**pinned to the owner/repo in this table**, and SHA-256 fail-closed — and `plugin.zip` and
`AmethystTool-*.zip` additionally go through the same
[archive screen](#downloaded-fixes-are-screened-before-they-touch-your-disk) the Fixes page uses, so a
decompression bomb inside a genuinely-published release is refused too.

### AmethystTool

`AmethystTool` is an independent fork of BetterSteamTools maintained alongside this app, focused on
privacy: **auto-update is disabled and it reports nothing back**. Its card is **first** on the **Mode**
page, above the other unlockers. It belongs there rather than with the store-page plugin because it is the
same kind of thing they are: `dwmapi.dll` and `xinput1_4.dll` are proxy DLLs that `steam.exe` loads by name
and that forward into `AmethystTool.dll`, all of it sitting in the Steam install root next to `steam.exe` —
and those first two are the very files BetterSteamTools and BST Nightly place. Having AmethystTool
installed and having a Mode installed are the same slot, so LuaTools shows them in the same list, and a
Mode install and an AmethystTool install cannot run at the same time. Install it from the **Install**
button on the Mode page; Steam is stopped for the copy and relaunched afterwards, because those DLLs are
locked while it runs.

**Exactly one backend shows as ACTIVE.** Which one holds the proxy-DLL slot is a single stored value, not a
flag per card, so installing AmethystTool demotes whichever Mode was active and installing a Mode demotes
AmethystTool. The badge also needs the payload to still be on disk, so deleting the files by hand outside
the app does not leave a card claiming to be active.

Three things about it are worth knowing before you press it:

- **Exactly four files are ever written.** The release archive also carries `INSTALL.txt`, `README.md`,
  `RELEASE_NOTES.md` and `TESTING.md`; those are documentation and never reach your Steam folder. The list
  is an allow-list, so a file a future release adds is ignored rather than installed by default.
- **Nothing is overwritten without a copy first.** If `dwmapi.dll` or `xinput1_4.dll` is already there —
  because another tool owns it, or because you are reinstalling — the existing file is moved into
  `AmethystTool-backup-<timestamp>\` inside your Steam folder *before* the replacement is written. The
  Mode page tells you which folder. That includes `amethysttool.toml`: reinstalling replaces the config,
  and your previous one is in that folder.
- **An unverifiable release is refused, not installed.** The SHA-256 GitHub publishes for the asset is
  required; if it is missing or does not match, the install stops. There is no pinned-hash fallback here
  (unlike Steamless) and no setting to turn the check off.

### Uninstalling a plugin or a Mode

The Plugin page's card, and every card on the Mode page, have an **Uninstall** button. It works from an
**install record**, not from a list of file names — and that distinction is the whole reason it is safe to press.

LuaTools keeps that record in `%AppData%\LuaToolsGui\install-manifest.json`: which plugin placed which
files in your Steam folder, when, and what each one hashed to. Uninstall removes exactly those, and:

- **A file another install still needs is kept.** `dwmapi.dll` and `xinput1_4.dll` are placed by
  AmethystTool *and* by every one of the Mode page's unlockers, and only one of them ever holds the slot at
  once (see above) — so removing them out from under the other is the one thing that must never happen.
  Installing AmethystTool over an old Mode now also cleans up that Mode's manifest entry so it stops
  claiming the two proxies it no longer owns; a stale claim left over from before that cleanup existed still
  keeps the files in place and says so, rather than guessing them safe to delete.
- **Nothing is deleted, it is moved.** Everything removed goes to `Removal-backup-<timestamp>\<plugin>\`
  inside your Steam folder. Changed your mind, or something else needed that file? Move it back.
- **Steam is closed and is not reopened for you.** The files are locked while it runs, so it has to go
  down; bringing it back up unasked — onto a client that was loading a DLL a moment ago and now is not —
  is not the uninstaller's call. Reopen it when you are ready.
- **No record, no removal.** If the button is disabled, LuaTools cannot prove which files next to
  `steam.exe` are its own — most likely you placed them by hand. It will not guess. Reinstall through the
  app to create a record, or remove the files yourself.

To remove AmethystTool by hand instead: close Steam, delete `AmethystTool.dll`, `amethysttool.toml`,
`dwmapi.dll` and `xinput1_4.dll` from the Steam root (leave the last two if a Mode is active), and — if you
want the files it displaced back — move them out of the newest `AmethystTool-backup-*` folder.

### Uninstalling a Mode

The active Mode's card gains an **Uninstall** button, and it runs the same machinery as the Plugin page —
same install record, same shared-file rule, same backup folder, same "Steam stays closed" decision.

A Mode only becomes uninstallable once LuaTools has installed it. Installing or switching a Mode now writes
an `install-manifest.json` entry naming the files it placed; only one Mode entry exists at a time, and
switching folds any file the previous Mode abandoned (`OpenSteamTool.dll`, typically) into the new entry, so
one uninstall clears the whole chain instead of stranding a file nothing admits to owning.

Two consequences worth knowing:

- **A Mode that was auto-detected cannot be uninstalled from the app.** On first run with no Mode selected,
  LuaTools hashes the DLLs next to `steam.exe` against published releases and adopts a match as the active
  Mode. That proves what those files *are*, and nothing about who put them there — so no record is written,
  and the card shows a short explanation instead of a button you would not want enabled. Reinstall the Mode
  through the app to create a record, or remove the files yourself.
- **A file AmethystTool still claims is left alone**, exactly as a Mode's files are left alone when
  AmethystTool is uninstalled. You are told which names stayed.

After a successful uninstall the Mode selection returns to "none" and the Mode page goes back to offering
each Mode as something to install. `settings.json` is unaffected beyond that one field, which has always
been allowed to be absent.

### Steam is closed and is not reopened, on purpose

This is true of **every** uninstall path — the store-page plugin, AmethystTool, and now Modes — and it is a
deliberate difference from install, which does relaunch Steam.

The files being removed are proxy DLLs that `steam.exe` loads by name, so they cannot be moved while it is
running: the uninstall asks Steam to close, and forces it only if it refuses. Bringing it back up
afterwards would put a client on screen that was loading a DLL a moment ago and now is not — an unasked-for
restart into a state the user has not yet had a chance to look at. That call is yours. Every uninstall toast
says Steam was closed, and the **Restart Steam** item in the sidebar reopens it when you are ready.

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

## Closing the window leaves LuaTools in the tray

Clicking the window's **X** hides LuaTools to the system tray and leaves it running. The tray icon
double-clicks to restore, and its right-click menu offers **Open** and **Exit** — **Exit** is the only
thing that actually quits.

That is the default because the window is not the whole app. LuaTools runs a local HTTP bridge on
`127.0.0.1:6767` that the Steam store-page plugin talks to; quitting takes it down, and the store-page
integration then stops answering with nothing on screen to say why. It used to quit, with close-to-tray
available but switched off by default.

Turn it off under **Settings → Startup → Minimize to System Tray** and the X button quits again. The
choice is persisted, so this default only decides for anyone who has not expressed one.

One deliberate exception: a headless `luatools://install/silent/<appid>` launch — which any web page can
trigger — still exits once it has reported the result, unless you have explicitly asked for a tray-resident
app. It does not leave behind a process you never started.

## Playing a game from the Manage tab

Each game in **Manage** has a primary action button, and the same action is on the card's right-click
menu. The label follows the game's state on disk:

| Files on disk | Button says | What it fires |
|---|---|---|
| Yes | **Play** | `steam://rungameid/<appid>` |
| No | **Install** | `steam://install/<appid>` — Steam's download dialog |
| Steam not located | **Play** | `steam://rungameid/<appid>` |

The third row is not a bug. When Steam cannot be located the library is *unreadable*, which is a
different answer from "read it, the game is not there" — so the app does not claim to know. `rungameid`
is self-correcting in that case: Steam answers it with its own install prompt for a game you own but do
not have, and with the store page for one you do not own. Refusing instead would strand you behind a dead
button in exactly the case where the app is the one that is unsure.

If Steam is not running it is started first, and the URL is held back until the client can actually accept
it — `steam://` sent at a Steam that is still booting is dropped silently, which reads as "the button did
nothing". Steam gets 45 seconds to come up. Nothing blocks the window while that happens.

Only a numeric appid in a closed range reaches the shell. The URL is handed to `Process.Start` with
`UseShellExecute = true`, so a validated `long` — never a passed-through string — is what makes the result
provably a well-formed `steam://` URL. An appid that fails validation starts no process at all; there is
no raw-string fallback. See `SteamLaunchPolicy` (the decision, pure and tested) and `SteamGameLauncher`
(the I/O).

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

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the released installer only
needs the .NET 10 **Desktop Runtime**).

Both projects target `net10.0-windows` as of 1.5.4. The move off `net8.0-windows` is a support deadline,
not a feature: .NET 8 stops receiving security fixes in November 2026, and .NET 10 is LTS until November
2028. `LangVersion` is deliberately not declared anywhere — it follows the TFM, so this is also what puts
the compiler on C# 14.

```bash
dotnet build LuaToolsGui.sln -c Release
```

```bash
dotnet test LuaToolsGui.sln -c Release
```

```bash
py scripts/check-i18n.py
```

```bash
dotnet format LuaToolsGui.sln --verify-no-changes
```

All four must pass before a release. `check-i18n.py` validates the 29 translation files against the
English baseline — key parity, no duplicate keys, valid XML, matching `{0}` placeholders, and that
`Strings.Designer.cs` exposes every key. It resolves the repository from its own location, so it can be
run from any working directory (`--root` overrides that). `dotnet format` reads its rules from
[`.editorconfig`](.editorconfig).

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

It resolves `<tfm>` by reading `TargetFramework` out of the project rather than hardcoding it, so the
1.5.4 move to `net10.0-windows` needed no change to the published path.

This produces the **binary only**. Packaging it into an installer is a separate `vpk pack` step and is
deliberately not part of the script.

> **Carry the TFM move into `vpk pack`.** That step is manual and lives outside this repo, so nothing here
> updates it for you. A framework-dependent Velopack build names the runtime its setup bootstraps on a
> clean machine — that argument has to go from the .NET 8 desktop runtime to the .NET 10 one
> (`--framework net10.0-x64-desktop`), or the installer provisions a runtime the 1.5.4 binary will not
> start on. The `packId` does **not** change: it keys every existing install and renaming it orphans them.

### Code signing (optional)

`build-release.ps1` signs `LuaTools.exe` with `signtool.exe` when a certificate is configured, and skips
signing with a warning when it isn't — releases build unsigned by default, exactly as before this existed.
**An unsigned `LuaTools.exe` is expected to trigger Windows SmartScreen** ("Windows protected your PC") on
a machine that has never run it before; signing with a certificate that has built up reputation is what
suppresses that.

**Requirements:**

- [Windows SDK](https://developer.microsoft.com/windows/downloads/windows-sdk/) for `signtool.exe`
  (installed by default under `C:\Program Files (x86)\Windows Kits\10\bin`; the script also checks `PATH`).
- A code-signing certificate. **OV (Organization Validation) or EV (Extended Validation)** — a plain
  self-signed or DV certificate will not clear SmartScreen's reputation check regardless of signing, so it
  is not worth configuring here. Purchased from a CA (DigiCert, Sectigo, SSL.com, etc.); EV additionally
  requires the private key live on a hardware token (USB HSM) rather than a plain file, which this
  file-based `/f`+`/p` flow does not support — an EV setup needs `signtool`'s `/csp`+`/kc` token form
  instead of `CERTIFICATE_PATH`/`CERTIFICATE_PASSWORD`, and is out of scope for this script as written.
- The certificate exported as a **`.pfx`** (PKCS#12) file, password-protected. From the Windows certificate
  store: `certmgr.msc` → find the cert → All Tasks → Export → **Yes, export the private key** → `.pfx`,
  set a password on export. From the CA directly: most issue the `.pfx` (or a `.p12`, same format) at
  enrollment.

**Configuring it:**

```powershell
$env:CERTIFICATE_PATH = 'C:\path\to\certificate.pfx'
$env:CERTIFICATE_PASSWORD = 'the PFX password'
.\scripts\build-release.ps1
```

Neither is a script parameter, specifically so a value never lands in a CI job's recorded command line.
**Locally**, prefer setting them from a file you `Get-Content` rather than typing the password directly at
an interactive prompt — PowerShell's PSReadLine module persists interactive command history to
`$env:APPDATA\Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt` in **plain text** by
default, and a literal `$env:CERTIFICATE_PASSWORD = '...'` typed at the prompt lands in it. Running it from
a `.ps1` file (this script, or a local untracked wrapper) is not affected — PSReadLine only records what is
typed interactively.

If a certificate is configured but `signtool.exe` cannot be found, the script **fails** rather than
shipping an unsigned binary silently — a certificate being set is read as "this build must be signed."

**Never commit the `.pfx`** — `.gitignore` already excludes `*.pfx`, `*.p12`, and `*.pem` repository-wide,
but a certificate placed inside the repo tree is one `git add -A` away from being staged regardless; keep
it outside the working copy (e.g. a machine-local secrets folder) as a second line of defense. **In CI**,
supply both variables from the platform's own secret store — for GitHub Actions, repository or
environment **encrypted secrets** (`Settings → Secrets and variables → Actions`) injected via
`env: CERTIFICATE_PATH: ${{ secrets.CERTIFICATE_PATH }}` (with the `.pfx` itself uploaded as a separate
secret or artifact, decoded to a temp path at job start) — never a plaintext value in the workflow YAML,
and scope the secret to the release job/environment only, not every workflow in the repository.

The certificate itself is never committed, logged, or read from a repository file by this script.
`signtool sign` takes the PFX password on its own command line; that is the one place the secret is
visible — to any process on the same machine that inspects `signtool.exe`'s command line during that
single call — and is a limitation of `signtool` itself rather than something this script adds. On a
single-tenant, ephemeral CI runner (e.g. a fresh GitHub-hosted runner) this is a low, generally-accepted
risk; on a shared or long-lived build machine, prefer a dedicated, access-controlled signing host.

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

### `dotnet format --verify-no-changes` is a CI gate

It passes clean. It used to fail with 8 whitespace violations, which turned out — checked byte-for-byte,
before vs. after, rather than assumed — to be two unrelated things, not a style the tooling didn't
understand:

- **4 files** (`LaunchOptionsService.cs`, `LuaEditor.cs`, `ManageViewModel.cs`, `PagedListViewModel.cs`)
  had a lone `\n` on specific lines inside an otherwise `\r\n` file — invisible in an editor, and not a
  content change at all once normalized: the fixed file is byte-identical to the original except for that
  one character per line.
- **4 files** had a real, narrow formatting slip each: a missing space after `[assembly:` in the WPF
  template boilerplate (`AssemblyInfo.cs`), an attribute that belongs on its own line above a block-bodied
  property, not inline (`ApiModels.cs`), a couple of stray alignment spaces that didn't actually line up
  with anything (`HomeViewModel.cs`), and a compact one-line object initializer where every sibling of the
  same shape elsewhere in the test suite (`LaunchOptionsServiceTests.cs`, `AppInfoCodecTests.cs`) already
  puts each property on its own line (`LaunchModStoreTests.cs`).

None of it was a deliberate style the formatter was fighting — it was accidental drift. `.editorconfig`
now exists, but deliberately encodes only what already matches 100% of the codebase (charset, indent
style/size, final newline, trailing whitespace) — verified with `--verify-no-changes` before and after
adding it, zero new violations. It does **not** set `end_of_line`: the repo has a genuine per-file split
(CRLF in most `.cs` files, LF in `Resources/*.resx` and a minority of `.cs` files), and forcing one would
flag every file on the losing side. `dotnet format`'s default — detect and preserve each file's own line
ending — is what already lets both conventions coexist, and stays that way.

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
