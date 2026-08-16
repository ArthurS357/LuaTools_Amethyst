#!/usr/bin/env python3
"""Validate the localization RESX files.

Checks every Strings.<tag>.resx against the English Strings.resx:
  - is well-formed XML
  - has EXACTLY the same set of <data> keys (no missing, no extra)
  - every value keeps the same set of {0}/{1}... placeholders as English (catches broken interpolation)

Also checks the hand-maintained Strings.Designer.cs against the English RESX, in both directions:
  - every RESX key has an accessor      (otherwise the key is unreachable from C#/XAML)
  - every accessor has a RESX key       (otherwise Strings.Get falls back to returning the key NAME,
                                         so the UI silently displays e.g. "Settings_Title")
CONTRIBUTING lists updating Strings.Designer.cs as a required manual step, but nothing verified it.

Exit code 1 if any problem is found. Run locally or in CI.
Usage: python scripts/check-i18n.py
"""
import re
import sys
import glob
import os
import xml.etree.ElementTree as ET

RES_DIR = os.path.join("src", "LuaToolsGui", "Resources")
ENGLISH = os.path.join(RES_DIR, "Strings.resx")
DESIGNER = os.path.join(RES_DIR, "Strings.Designer.cs")

# `public static string Foo => Get(nameof(Foo));`
ACCESSOR_RE = re.compile(r'public\s+static\s+string\s+(\w+)\s*=>\s*Get\(nameof\(')
PLACEHOLDER_RE = re.compile(r'\{(\d+)\}')

# Keys that are DELIBERATELY English-only for now: the feature's UI is still settling, so translating
# it would just be rework. They're exempt from the "missing key" check (the app falls back to English
# per-key at runtime) but are still reported as a reminder.
#
# This list IS the handoff to the translation pass. When a feature's UI is final: translate its keys
# across every Strings.<tag>.resx, clear them from here, and this check goes back to demanding full
# parity. Anything left here is untranslated in all 29 languages.
PENDING_TRANSLATION: set[str] = {
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
    # Refusal message from the fix/manifest safety screen (FixAnalyzer).
    "Fixes_Toast_Blocked",
    "Fixes_Toast_Blocked_Body",
    # Startup warning when the running binary is not an Amethyst build.
    "Build_NotFork_Title",
    "Build_NotFork_Body",
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
}



def parse(path):
    """Return {key: value} for a RESX file (raises ET.ParseError if XML is malformed).

    Reads the parsed XML tree rather than regex-matching the raw text. The old regex required the exact
    one-line form `<data name="K" xml:space="preserve"><value>V</value></data>`; a contributor whose editor
    reformatted the file (or wrote the attributes in a different order) would have every key in it silently
    drop out of the comparison. Worst case that was silent rather than loud: reformatting the ENGLISH file
    shrank the baseline key set, so the parity check passed while checking almost nothing.
    """
    root = ET.parse(path).getroot()
    out = {}
    for data in root.findall("data"):
        name = data.get("name")
        if name is None:
            continue
        value = data.find("value")
        out[name] = (value.text or "") if value is not None else ""
    return out


def parse_designer(path):
    """Return the set of resource keys exposed as accessors by Strings.Designer.cs."""
    with open(path, encoding="utf-8") as fh:
        return set(ACCESSOR_RE.findall(fh.read()))


def placeholders(value):
    return set(PLACEHOLDER_RE.findall(value))


def main():
    if not os.path.exists(ENGLISH):
        print(f"ERROR: {ENGLISH} not found (run from repo root)")
        return 1

    en = parse(ENGLISH)
    base_keys = set(en)
    problems = []

    # ── Strings.Designer.cs parity (the hand-maintained accessor list) ──
    if not os.path.exists(DESIGNER):
        problems.append(f"{os.path.basename(DESIGNER)}: not found")
    else:
        accessors = parse_designer(DESIGNER)
        no_accessor = base_keys - accessors
        no_key = accessors - base_keys
        if no_accessor:
            problems.append(
                f"Strings.Designer.cs: {len(no_accessor)} RESX key(s) have no accessor "
                f"(unreachable from C#/XAML): {', '.join(sorted(no_accessor)[:10])}")
        if no_key:
            problems.append(
                f"Strings.Designer.cs: {len(no_key)} accessor(s) have no RESX key "
                f"(the UI will render the key name): {', '.join(sorted(no_key)[:10])}")

    for path in sorted(glob.glob(os.path.join(RES_DIR, "Strings.*.resx"))):
        name = os.path.basename(path)
        try:
            tr = parse(path)
        except ET.ParseError as e:
            problems.append(f"{name}: INVALID XML — {e}")
            continue

        missing = base_keys - set(tr) - PENDING_TRANSLATION
        extra = set(tr) - base_keys
        if missing:
            problems.append(f"{name}: missing {len(missing)} key(s): {', '.join(sorted(missing)[:10])}")
        if extra:
            problems.append(f"{name}: {len(extra)} unknown key(s): {', '.join(sorted(extra)[:10])}")

        # Placeholder parity (only for keys present in both).
        for k in base_keys & set(tr):
            if placeholders(en[k]) != placeholders(tr[k]):
                problems.append(
                    f"{name}: key '{k}' placeholder mismatch "
                    f"(en={sorted(placeholders(en[k]))} vs {sorted(placeholders(tr[k]))})")

    count = len(glob.glob(os.path.join(RES_DIR, "Strings.*.resx")))
    if problems:
        print(f"i18n check FAILED ({len(problems)} problem(s) across {count} language files):")
        for p in problems:
            print("  -", p)
        return 1

    print(f"i18n check OK: {count} language files, {len(base_keys)} keys each, "
          f"XML valid, placeholders consistent, Strings.Designer.cs in sync.")

    pending = PENDING_TRANSLATION & base_keys
    if pending:
        print(f"\nNOTE: {len(pending)} key(s) are English-only by design and exempt from the parity "
              f"check (PENDING_TRANSLATION in this script). Translate them once the UI is final:")
        for k in sorted(pending):
            print("  -", k)
    stale = PENDING_TRANSLATION - base_keys
    if stale:
        print(f"\nNOTE: {len(stale)} PENDING_TRANSLATION entr(ies) no longer exist in Strings.resx — "
              f"drop them from the list: {', '.join(sorted(stale))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
