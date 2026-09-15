#!/usr/bin/env python3
"""Check that committed text files are clean UTF-8.

For every tracked file with a text extension (see TEXT_EXTENSIONS), fail on:
  - UTF-16 or UTF-32, with or without a BOM (named as such, not as a generic decode error)
  - bytes that are not valid UTF-8        (ONE leading UTF-8 BOM is allowed; many files here carry one)
  - a second BOM, or a BOM anywhere but the first byte (U+FEFF left behind by a concatenation or a
    tool that added its own BOM in front of an existing one — an XML parser rejects both)
  - C0 control characters other than TAB, LF and CR, and DEL
  - C1 control characters, U+0080..U+009F (what Windows-1252 bytes become when read as ISO-8859-1 — e.g. an
    ellipsis turned into U+0085, or an em dash double-encoded through Latin-1 — and never real text)
  - U+FFFD, the replacement character     (what a lossy conversion leaves behind)
  - double-encoded text ("mojibake")      (UTF-8 read as Windows-1252 and written back as UTF-8)

An empty file and a file holding only a BOM are valid UTF-8 and pass.

Why this exists: 1.7.4 re-saved LuaToolsGui.csproj through Windows PowerShell 5.1, whose Set-Content
writes the ANSI code page by default. Its em dashes and ellipses became single Windows-1252 bytes, the
build still passed on the maintainer's machine (the incremental build skipped the markup pass), and CI
failed with "Invalid character in the given encoding. Line 19, position 64." pointing at no file anyone
had meant to change. The same release turned docs/CHANGELOG.md into mojibake the other way round. Neither
is caught by the compiler in a useful form, so this runs before it.

Exit code 1 if any problem is found. Run locally or in CI.
Usage: python scripts/check-encoding.py [--root PATH]

stdlib only, like check-i18n.py: the workflow runs it with no pip install step.
"""
from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

TEXT_EXTENSIONS: frozenset[str] = frozenset({
    ".cs", ".xaml", ".resx", ".csproj", ".props", ".targets", ".sln", ".manifest",
    ".md", ".json", ".xml", ".yml", ".yaml", ".py", ".ps1", ".txt", ".editorconfig", ".gitignore",
})

BOM = b"\xef\xbb\xbf"
ALLOWED_CONTROLS = frozenset({0x09, 0x0A, 0x0D})
# Built with chr() so neither character ever appears literally in this file.
REPLACEMENT_CHARACTER = chr(0xFFFD)
BOM_CHARACTER = chr(0xFEFF)

# Checked longest first: a UTF-32 LE BOM begins with the UTF-16 LE one.
_WIDE_BOMS: tuple[tuple[bytes, str], ...] = (
    (b"\xff\xfe\x00\x00", "UTF-32 LE"),
    (b"\x00\x00\xfe\xff", "UTF-32 BE"),
    (b"\xff\xfe", "UTF-16 LE"),
    (b"\xfe\xff", "UTF-16 BE"),
)

# Share of NUL bytes, at the odd or even positions, above which BOM-less content is taken to be UTF-16.
# Mostly-ASCII UTF-16 is close to 100% on one side and 0% on the other; UTF-8 text has no NUL at all.
_UTF16_NUL_SHARE = 0.3

# The five byte values Windows-1252 leaves undefined. Tools that decode with it (PowerShell 5.1 among them)
# pass them through as the same code point, so they have to round-trip here too.
_CP1252_UNDEFINED = frozenset({0x81, 0x8D, 0x8F, 0x90, 0x9D})


def _to_cp1252(run: str) -> bytes | None:
    """Encode `run` as Windows-1252, or None if any character is outside it."""
    out = bytearray()
    for ch in run:
        if ord(ch) in _CP1252_UNDEFINED:
            out.append(ord(ch))
            continue
        try:
            out += ch.encode("cp1252")
        except UnicodeEncodeError:
            return None
    return bytes(out)


def _utf8_sequence_length(lead: int) -> int:
    """How many bytes a UTF-8 sequence starting with `lead` takes, or 0 if it cannot start one."""
    if 0xC2 <= lead <= 0xDF:
        return 2
    if 0xE0 <= lead <= 0xEF:
        return 3
    if 0xF0 <= lead <= 0xF4:
        return 4
    return 0


def mojibake_runs(text: str) -> list[str]:
    """Return character sequences that are really UTF-8 bytes shown as Windows-1252.

    An em dash (E2 80 94) read one byte per character becomes U+00E2 U+20AC U+201D. The characters are
    spelled out rather than written here, so this file passes its own check. A sequence qualifies when a
    character whose Windows-1252 byte is a UTF-8 lead byte (Ã, â, ð…) is followed by exactly the
    characters that complete a valid sequence. Real text does not produce that: Arabic, Thai, CJK and the
    rest fall outside Windows-1252 entirely, and the Latin scripts here do not follow a capital with
    continuation-range punctuation. Each sequence is tested on its own, so a correctly accented letter
    beside one does not hide it.
    """
    found: list[str] = []
    i = 0
    while i < len(text):
        lead = _to_cp1252(text[i]) if ord(text[i]) >= 0x80 else None
        length = _utf8_sequence_length(lead[0]) if lead else 0
        if length:
            candidate = text[i:i + length]
            raw = _to_cp1252(candidate) if len(candidate) == length else None
            if raw is not None:
                try:
                    raw.decode("utf-8")
                except UnicodeDecodeError:
                    pass
                else:
                    found.append(candidate)
                    i += length
                    continue
        i += 1
    return found


def _line_col(data: bytes, offset: int) -> tuple[int, int]:
    line_start = data.rfind(b"\n", 0, offset) + 1
    return data.count(b"\n", 0, offset) + 1, offset - line_start + 1


def wide_encoding(data: bytes) -> str | None:
    """Name the UTF-16/UTF-32 encoding `data` is in, or None if it is not one of them."""
    for bom, label in _WIDE_BOMS:
        if data.startswith(bom):
            return f"{label} (BOM {bom.hex(' ')})"
    if len(data) < 2:
        return None
    half = len(data) // 2
    even_nul = data[0::2].count(0) / half
    odd_nul = data[1::2].count(0) / half
    if odd_nul > _UTF16_NUL_SHARE and odd_nul > even_nul:
        return "UTF-16 LE without a BOM"
    if even_nul > _UTF16_NUL_SHARE and even_nul > odd_nul:
        return "UTF-16 BE without a BOM"
    return None


def check_bytes(name: str, data: bytes) -> list[str]:
    """Every problem in one file's contents, as human-readable lines. Empty means clean."""
    wide = wide_encoding(data)
    if wide is not None:
        return [f"{name}: saved as {wide} — this repository is UTF-8"]

    body = data[len(BOM):] if data.startswith(BOM) else data
    offset_base = len(data) - len(body)
    problems: list[str] = []

    try:
        text = body.decode("utf-8")
    except UnicodeDecodeError as e:
        line, col = _line_col(data, offset_base + e.start)
        bad = body[e.start:e.end].hex(" ")
        problems.append(f"{name}:{line}:{col}: not valid UTF-8 (byte(s) {bad}) — saved in another encoding?")
        return problems

    for i, byte in enumerate(body):
        if (byte < 0x20 and byte not in ALLOWED_CONTROLS) or byte == 0x7F:
            line, col = _line_col(data, offset_base + i)
            problems.append(f"{name}:{line}:{col}: control character 0x{byte:02X}")
            break

    c1 = next((i for i, ch in enumerate(text) if 0x80 <= ord(ch) <= 0x9F), None)
    if c1 is not None:
        line = text.count("\n", 0, c1) + 1
        problems.append(f"{name}:{line}: C1 control character U+{ord(text[c1]):04X} — "
                        f"Windows-1252 text read as ISO-8859-1?")

    if BOM_CHARACTER in text:
        where = text.index(BOM_CHARACTER)
        line = text.count("\n", 0, where) + 1
        if where == 0 and offset_base:
            kind = "second BOM right after the first"
        else:
            kind = "BOM (U+FEFF) inside the file"
        problems.append(f"{name}:{line}: {kind} — only one, at the very start, is allowed")

    if REPLACEMENT_CHARACTER in text:
        line = text.count("\n", 0, text.index(REPLACEMENT_CHARACTER)) + 1
        problems.append(f"{name}:{line}: U+FFFD replacement character — text was lost in a conversion")

    runs = mojibake_runs(text)
    if runs:
        first = text.index(runs[0])
        line = text.count("\n", 0, first) + 1
        fixed = _to_cp1252(runs[0])
        assert fixed is not None
        problems.append(
            f"{name}:{line}: double-encoded text {runs[0]!r} (should read {fixed.decode('utf-8')!r}); "
            f"{len(runs)} occurrence(s) in this file")

    return problems


def tracked_text_files(root: Path) -> list[Path]:
    """Files git tracks under `root` with a text extension. Raises if git cannot list them."""
    out = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z"], capture_output=True, check=True).stdout
    paths = [Path(p) for p in out.decode("utf-8").split("\0") if p]
    return sorted(p for p in paths if (p.suffix or p.name).lower() in TEXT_EXTENSIONS)


def check(root: Path, files: list[Path]) -> list[str]:
    problems: list[str] = []
    for rel in files:
        path = root / rel
        if not path.is_file():  # deleted in the working tree but not yet from the index
            continue
        problems.extend(check_bytes(rel.as_posix(), path.read_bytes()))
    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--root", type=Path, default=Path(__file__).resolve().parent.parent,
        help="repository root (default: the directory containing scripts/)")
    args = parser.parse_args(argv)

    # A Windows runner's piped stdout is the ANSI code page, and a diagnostic can quote text that has no
    # place in it. Escaping beats a UnicodeEncodeError traceback standing in for the finding.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="backslashreplace")

    try:
        files = tracked_text_files(args.root)
    except (OSError, subprocess.CalledProcessError) as e:
        # Fail closed: a check that cannot see the files must not report them clean.
        print(f"ERROR: could not list tracked files with git: {e}")
        return 1
    if not files:
        print(f"ERROR: no tracked text files under {args.root} (wrong --root?)")
        return 1

    problems = check(args.root, files)
    if problems:
        print(f"encoding check FAILED ({len(problems)} problem(s)):")
        for line in problems:
            print(f"  - {line}")
        return 1
    print(f"encoding check OK: {len(files)} tracked text files are valid UTF-8, "
          f"no UTF-16/32, no stray BOM, no control characters, no replacement characters, "
          f"no double-encoded text.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
