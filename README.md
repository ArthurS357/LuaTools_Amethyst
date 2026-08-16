<p align="center">
  <img height="336" alt="luatools" src="https://github.com/user-attachments/assets/54702ada-93a8-439b-ab3e-5cd73747ed46" />
</p>

# LuaTools — privacy fork

**This is an unofficial fork of [LuaTools](https://lua.tools), not the official build.**
It exists to remove the parts of the upstream client that sent data off the machine, and it is not
affiliated with or endorsed by the upstream project.

A Windows desktop client for managing Steam manifest/lua configurations, built with WPF on .NET 8.
LuaTools browses and installs manifest sources, edits `stplug-in` lua files (depot pinning, per-depot
enable/disable), manages unlocker modes, and injects a companion plugin into Steam's store pages. It
ships translated in 29 languages and auto-updates via Velopack.

Current version: **1.3.0**. The app identifies itself as `privacy fork` in the window title and in the
navigation footer next to the version, so you can always tell which build you are running.

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

## Auto-update: off by default

**This fork does not update itself unless you configure it to.** There is no compiled-in update feed.

Upstream's feed publishes the **official** build, so inheriting it meant the fork would eventually
download and silently install a version with Umami telemetry and the DonateKeys key upload — undoing the
whole point of the fork, in the background, without anyone choosing it. Rather than point the updater at
a fork repo that may not exist yet, the default is *no feed at all*: an unconfigured build makes **no
update request whatsoever**.

### Enabling it for your own fork

Publish Velopack releases to your own GitHub repo, then add to `settings.json`:

```json
{ "AppUpdateRepos": ["https://github.com/you/YourFork"] }
```

List more than one for fallback — they are tried in order, which covers a primary repo becoming
unreachable (banned, DMCA'd, account removed) rather than merely being out of date.

Entries are validated before use ([`AppUpdateSources.cs`](src/LuaToolsGui/Services/AppUpdateSources.cs)):

- `https://github.com/<owner>/<repo>` only. **`http://` is refused** — an update feed decides which
  executable replaces this one, so it is never accepted over a transport that can be rewritten.
- **The official LuaTools release repos are refused outright**, however they are spelled (casing,
  trailing slash, `.git`, `www.`). This is the backstop for the realistic mistake: someone pasting the
  upstream URL in to "get updates working" and quietly reinstating telemetry and the key upload.
- Rejected entries are logged to `plugin-backend.log` with the reason, so ignored config does not look
  like a broken app.

If you would rather never think about it, leave `AppUpdateRepos` unset and update manually.

### Still: do not install official releases over this build

The blocklist protects the *auto*-updater. It cannot stop you running an official installer by hand —
that will restore telemetry and DonateKeys. Check the window title and footer: this build says
`privacy fork`; the official one does not.

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

Downloads stay integrity-checked regardless of which mirror serves them — assets are pinned to
GitHub-owned HTTPS hosts and verified by SHA-256 before use, so a hostile mirror cannot substitute
content.

### Privacy and update keys

| Key | Default | Effect |
|---|---|---|
| `EnableSourceAvailabilityChecks` | `true` | `false` stops the cleartext lookup entirely (see above) |
| `InsecureMetadataNotice` | `"once"` | `"always"` / `"off"` — how often the lookup is disclosed |
| `AppUpdateRepos` | *(unset)* | Repos for the app's own updates. Unset = self-update off |

A full example:

```json
{
  "AppUpdateRepos": ["https://github.com/you/YourFork"],
  "EnableSourceAvailabilityChecks": true,
  "InsecureMetadataNotice": "always",
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
English baseline — key parity, valid XML, matching `{0}` placeholders, and that
`Strings.Designer.cs` exposes every key.

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
