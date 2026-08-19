#!/usr/bin/env python3
"""Validate the localization RESX files.

Checks every Strings.<tag>.resx against the English Strings.resx:
  - is well-formed XML
  - declares no key twice          (a duplicate silently overwrote the first value before this check)
  - has EXACTLY the same set of <data> keys (no missing, no extra)
  - every value keeps the same set of {0}/{1}... placeholders as English (catches broken interpolation)

Also checks the hand-maintained Strings.Designer.cs against the English RESX, in both directions:
  - every RESX key has an accessor      (otherwise the key is unreachable from C#/XAML)
  - every accessor has a RESX key       (otherwise Strings.Get falls back to returning the key NAME,
                                         so the UI silently displays e.g. "Settings_Title")
CONTRIBUTING lists updating Strings.Designer.cs as a required manual step, but nothing verified it.

Exit code 1 if any problem is found. Run locally or in CI.
Usage: python scripts/check-i18n.py [--root PATH]

The repository root defaults to this script's parent directory, so the check no longer depends on the
current working directory — running it from anywhere gives the same answer as running it from the root.
"""
from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

# `public static string Foo => Get(nameof(Foo));`
ACCESSOR_RE = re.compile(r"public\s+static\s+string\s+(\w+)\s*=>\s*Get\(nameof\(")
PLACEHOLDER_RE = re.compile(r"\{(\d+)\}")

# How many offending keys to name before collapsing the rest into a count. The list used to be truncated
# silently, so "missing 40 key(s): A, B, ..." showed ten names and left the reader unsure whether the
# other thirty were reported anywhere.
SAMPLE_SIZE = 10

# Keys that are DELIBERATELY English-only for now: the feature's UI is still settling, so translating
# it would just be rework. They're exempt from the "missing key" check (the app falls back to English
# per-key at runtime) but are still reported as a reminder.
#
# This list IS the handoff to the translation pass. When a feature's UI is final: translate its keys
# across every Strings.<tag>.resx, clear them from here, and this check goes back to demanding full
# parity. Anything left here is untranslated in all 29 languages.
PENDING_TRANSLATION: frozenset[str] = frozenset({
    # Added by the security pass that replaced hardcoded English literals in AuthService with real
    # resource keys. They are localizable now (they weren't before) but not yet translated — clear
    # these two from the list as soon as the translations land.
    "Auth_Err_SignInTimedOut",
    "Auth_Err_CallbackPortBusy",
    # Consent prompt added for luatools://install/silent/<appid>, which any web page can trigger.
    "Protocol_SilentInstall_Title",
    "Protocol_SilentInstall_Body",
    # Consent prompt shown before LuaTools enables Steam's unauthenticated debug bridge (port 8080).
    "Cdp_Consent_Title",
    "Cdp_Consent_Body",
    # Privacy notice for the source-availability lookup, which is still cleartext because the manifest
    # backend serves no usable TLS (full probe on AppConfig.ManifestBackendUrl).
    "Privacy_HttpMetadata_Title",
    "Privacy_HttpMetadata_Body",
    # Startup guard that fires when a WPF-UI upgrade silently breaks the Amethyst accent override.
    "Theme_Guard_Title",
    "Theme_Guard_Body",
    # The product name. Deliberately English everywhere — a product name is not translated.
    "App_DisplayName",
    # The nav rail's Plugin entry. It was a hardcoded English literal in MainWindow.xaml while every
    # other rail item read from a resource — so a translated UI had exactly one English word in the
    # first thing the user looks at. It is a key now; the value is still English pending translation.
    "Nav_Plugin",
    # Refusal message from the fix/manifest safety screen (FixAnalyzer).
    "Fixes_Toast_Blocked",
    "Fixes_Toast_Blocked_Body",
    # Refusal shown when plugin.zip fails the same FixAnalyzer screen before extraction.
    "Plugin_Err_ArchiveRejected",
    # Pre-install disclosure of source, version, hash and which checks passed (Mode + Plugin).
    "Download_Notice_Title",
    "Download_Notice_Body",
    "Download_Notice_Check_Pinned",
    "Download_Notice_Check_Digest",
    "Download_Notice_Check_Archive",
    "Download_Notice_Cancel",
    "Download_Notice_Cancelled",
    # Startup warning when the running binary is not an Amethyst build.
    "Build_NotFork_Title",
    "Build_NotFork_Body",
    # Hubcap key validation, split out of the single Settings_HubcapKeyError that used to cover a rejected
    # key, a dead network and a rate limit alike — so an offline check no longer reads as "your key is bad".
    "Settings_HubcapKeyRejected",
    "Settings_HubcapKeyOffline",
    "Settings_HubcapKeyRateLimited",
    # Key-expiry warning surfaced on the Settings page. api_key_expires_at was already being
    # rendered as a date on the usage line; these are the wording for raising it on its own once the key
    # is within a week of dying.
    "Settings_HubcapExpiryExpired",
    "Settings_HubcapExpiryToday",
    "Settings_HubcapExpirySoon",
    # Removing a game from the Depots list. The list is the union of three on-disk sources, so nothing
    # short of purging all three took an accidentally-added game back off it; these are that flow's copy.
    "Builds_RemoveGame",
    "Builds_RemoveGame_Tip",
    "Builds_RemoveGame_Title",
    "Builds_RemoveGame_Body",
    "Builds_RemoveGame_Done",
    "Builds_RemoveGame_Gone",
    "Builds_RemoveGame_Failed",
    # Hubcap download failures, promoted out of hardcoded English literals in HubcapErrorText. The wait
    # units are separate keys so a language that inflects by count can translate them properly, rather
    # than having a number glued to a bare noun.
    "Hubcap_Err_KeyRejected",
    "Hubcap_Err_DailyLimit",
    "Hubcap_Err_DailyLimitRetry",
    "Hubcap_Err_NoManifest",
    "Hubcap_Err_Status",
    "Hubcap_Err_Unreachable",
    "Hubcap_Err_Generic",
    "Hubcap_Wait_Seconds",
    "Hubcap_Wait_Minutes",
    "Hubcap_Wait_Hours",
    # Accent-colour picker added in 1.5.0. "Amethyst" is a product name and stays English everywhere; the
    # other four are ordinary UI copy awaiting translation.
    "Settings_Accent",
    "Settings_Accent_Hint",
    "Settings_Accent_Amethyst",
    "Settings_Accent_Green",
    "Settings_Accent_Red",
    "Settings_Accent_Apply",
    # Startup Steam flow: close Steam, run setup only when there is setup to do, offer Steam back.
    # Replaces a launch that left Steam up and restarted it from inside whichever installer ran.
    "Startup_Title",
    "Startup_Steam_Closing",
    "Startup_Steam_Closed",
    "Startup_Steam_Stubborn_Title",
    "Startup_Steam_Stubborn_Body",
    "Startup_Steam_LeftRunning",
    "Startup_Steam_Reopen_Body",
    "Startup_Steam_Reopen_Action",
    "Startup_Steam_ReopenFailed",
    # In-app changelog on the About page, added in 1.5.0. The entry TEXT is not localised at all — it
    # lives in Resources/Changelog.cs, like release notes generally do; only the chrome is a resource.
    "About_Changelog_Title",
    "About_Changelog_Show",
    "About_Changelog_Hide",
    # The About page. English-only for now; translate once its wording settles.
    "About_Nav",
    "About_Title",
    "About_What_Header",
    "About_What_Body",
    "About_Version_Header",
    "About_Repo_Header",
    "About_Updates_Header",
    "About_Updates_Enabled",
    "About_Updates_Disabled",
    "About_Updates_Sources",
    "About_Check_Button",
    "About_Check_Checking",
    "About_Check_UpToDate",
    "About_Check_Found",
    "About_Check_Failed",
    "About_Check_Disabled",
    "About_OpenRepo",
    "About_OpenSettings",
    "About_Config_Header",
    "About_Config_Body",
})


def sample(keys: set[str]) -> str:
    """Comma-joined sample of `keys`, with an explicit remainder count rather than silent truncation."""
    ordered = sorted(keys)
    shown = ", ".join(ordered[:SAMPLE_SIZE])
    remaining = len(ordered) - SAMPLE_SIZE
    return f"{shown} (+{remaining} more)" if remaining > 0 else shown


def parse(path: Path) -> tuple[dict[str, str], list[str]]:
    """Return ({key: value}, duplicate_keys) for a RESX file.

    Raises ET.ParseError if the XML is malformed.

    Reads the parsed XML tree rather than regex-matching the raw text. The old regex required the exact
    one-line form `<data name="K" xml:space="preserve"><value>V</value></data>`; a contributor whose editor
    reformatted the file (or wrote the attributes in a different order) would have every key in it silently
    drop out of the comparison. Worst case that was silent rather than loud: reformatting the ENGLISH file
    shrank the baseline key set, so the parity check passed while checking almost nothing.

    Duplicates are returned rather than folded away. A RESX declaring the same name twice is valid XML and
    compiles, but only one value survives — so a translator could "fix" a string, see the check pass, and
    ship the other copy. Dict assignment hid that entirely.
    """
    root = ET.parse(path).getroot()
    out: dict[str, str] = {}
    duplicates: list[str] = []
    for data in root.findall("data"):
        name = data.get("name")
        if name is None:
            continue
        if name in out:
            duplicates.append(name)
        value = data.find("value")
        out[name] = (value.text or "") if value is not None else ""
    return out, duplicates


def parse_designer(path: Path) -> set[str]:
    """Return the set of resource keys exposed as accessors by Strings.Designer.cs."""
    return set(ACCESSOR_RE.findall(path.read_text(encoding="utf-8")))


def placeholders(value: str) -> set[str]:
    return set(PLACEHOLDER_RE.findall(value))


def check(res_dir: Path) -> tuple[list[str], list[str]]:
    """Run every check against `res_dir`. Returns (problems, notes); empty problems means a pass."""
    english = res_dir / "Strings.resx"
    designer = res_dir / "Strings.Designer.cs"

    if not english.is_file():
        return ([f"{english}: not found (pass --root, or run from the repo root)"], [])

    try:
        en, en_duplicates = parse(english)
    except ET.ParseError as e:
        return ([f"{english.name}: INVALID XML — {e}"], [])

    base_keys = set(en)
    problems: list[str] = []
    notes: list[str] = []

    if en_duplicates:
        problems.append(
            f"{english.name}: {len(en_duplicates)} duplicate key(s) — only the last value is used: "
            f"{sample(set(en_duplicates))}")

    # ── Strings.Designer.cs parity (the hand-maintained accessor list) ──
    if not designer.is_file():
        problems.append(f"{designer.name}: not found")
    else:
        accessors = parse_designer(designer)
        no_accessor = base_keys - accessors
        no_key = accessors - base_keys
        if no_accessor:
            problems.append(
                f"Strings.Designer.cs: {len(no_accessor)} RESX key(s) have no accessor "
                f"(unreachable from C#/XAML): {sample(no_accessor)}")
        if no_key:
            problems.append(
                f"Strings.Designer.cs: {len(no_key)} accessor(s) have no RESX key "
                f"(the UI will render the key name): {sample(no_key)}")

    translations = sorted(res_dir.glob("Strings.*.resx"))
    for path in translations:
        try:
            tr, duplicates = parse(path)
        except ET.ParseError as e:
            problems.append(f"{path.name}: INVALID XML — {e}")
            continue

        if duplicates:
            problems.append(
                f"{path.name}: {len(duplicates)} duplicate key(s) — only the last value is used: "
                f"{sample(set(duplicates))}")

        missing = base_keys - set(tr) - PENDING_TRANSLATION
        extra = set(tr) - base_keys
        if missing:
            problems.append(f"{path.name}: missing {len(missing)} key(s): {sample(missing)}")
        if extra:
            problems.append(f"{path.name}: {len(extra)} unknown key(s): {sample(extra)}")

        # Placeholder parity (only for keys present in both).
        for k in sorted(base_keys & set(tr)):
            if placeholders(en[k]) != placeholders(tr[k]):
                problems.append(
                    f"{path.name}: key '{k}' placeholder mismatch "
                    f"(en={sorted(placeholders(en[k]))} vs {sorted(placeholders(tr[k]))})")

    if problems:
        problems.insert(0, f"i18n check FAILED ({len(problems)} problem(s) across "
                           f"{len(translations)} language files):")
        return (problems, notes)

    notes.append(f"i18n check OK: {len(translations)} language files, {len(base_keys)} keys each, "
                 f"XML valid, no duplicates, placeholders consistent, Strings.Designer.cs in sync.")

    pending = PENDING_TRANSLATION & base_keys
    if pending:
        notes.append(f"\nNOTE: {len(pending)} key(s) are English-only by design and exempt from the parity "
                     f"check (PENDING_TRANSLATION in this script). Translate them once the UI is final:")
        notes.extend(f"  - {k}" for k in sorted(pending))
    stale = PENDING_TRANSLATION - base_keys
    if stale:
        notes.append(f"\nNOTE: {len(stale)} PENDING_TRANSLATION entr(ies) no longer exist in Strings.resx — "
                     f"drop them from the list: {', '.join(sorted(stale))}")
    return ([], notes)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--root", type=Path, default=Path(__file__).resolve().parent.parent,
        help="repository root (default: the directory containing scripts/)")
    args = parser.parse_args(argv)

    res_dir: Path = args.root / "src" / "LuaToolsGui" / "Resources"
    if not res_dir.is_dir():
        print(f"ERROR: {res_dir} not found (wrong --root?)")
        return 1

    problems, notes = check(res_dir)
    for line in problems:
        print(line if line.startswith("i18n check") else f"  - {line}")
    for line in notes:
        print(line)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
