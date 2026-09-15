#!/usr/bin/env python3
"""Tests for scripts/check-encoding.py.

Run from the repository root:
    py -m unittest discover -s scripts/tests

stdlib `unittest`, for the same reason as test_check_i18n.py: the workflow has no pip install step.
Cases call `check_bytes()` on in-memory bytes, except the last group, which builds a throwaway git repo
to prove the tracked-file walk and the exit code.
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

_SCRIPT = Path(__file__).resolve().parent.parent / "check-encoding.py"
_spec = importlib.util.spec_from_file_location("check_encoding", _SCRIPT)
assert _spec is not None and _spec.loader is not None
check_encoding = importlib.util.module_from_spec(_spec)
sys.modules["check_encoding"] = check_encoding
_spec.loader.exec_module(check_encoding)

check_bytes = check_encoding.check_bytes


class CleanFilesPass(unittest.TestCase):
    def test_ascii(self) -> None:
        self.assertEqual(check_bytes("a.cs", b"class A { }\r\n\tint x;\n"), [])

    def test_utf8_with_and_without_bom(self) -> None:
        text = "reads the “v1.5.4 · …” label — keep it bumped".encode("utf-8")
        self.assertEqual(check_bytes("a.csproj", text), [])
        self.assertEqual(check_bytes("a.csproj", b"\xef\xbb\xbf" + text), [])

    def test_accented_latin_is_not_mojibake(self) -> None:
        for sample in ["Corrige binário empacotado com versão antiga", "Não é possível", "Größe ändern",
                       "Ça marche, à côté", "Întreținere în România", "Łódź, Žluťoučký kůň"]:
            with self.subTest(sample=sample):
                self.assertEqual(check_bytes("a.md", sample.encode("utf-8")), [])

    def test_non_latin_scripts_are_not_mojibake(self) -> None:
        for sample in ["إصدار ليلي من BetterSteamTools", "ビルドを確認", "Нощна компилация", "เวอร์ชัน",
                       "版本 · 发布", "Λειτουργία"]:
            with self.subTest(sample=sample):
                self.assertEqual(check_bytes("a.resx", sample.encode("utf-8")), [])


class BrokenFilesFail(unittest.TestCase):
    def test_the_1_7_4_csproj_bytes(self) -> None:
        # The exact failure: "·" and "…" saved as single Windows-1252 bytes, line 19 column 64.
        line = '         reads InformationalVersion back to render the "v1.5.4 \xb7 \x85" label'
        data = b"\n" * 18 + line.encode("latin-1")
        problems = check_bytes("LuaToolsGui.csproj", data)
        self.assertEqual(len(problems), 1)
        self.assertIn("LuaToolsGui.csproj:19:64: not valid UTF-8", problems[0])
        self.assertIn("b7", problems[0])

    def test_bom_does_not_shift_the_reported_column(self) -> None:
        problems = check_bytes("a.xaml", b"\xef\xbb\xbfab\x97")
        self.assertIn("a.xaml:1:6:", problems[0])

    def test_control_character(self) -> None:
        problems = check_bytes("a.md", b"line one\nbell \x07 here\n")
        self.assertEqual(problems, ["a.md:2:6: control character 0x07"])

    def test_delete_character(self) -> None:
        self.assertIn("control character 0x7F", check_bytes("a.cs", b"x\x7f")[0])

    def test_replacement_character(self) -> None:
        problems = check_bytes("a.md", f"ok\nlost {chr(0xFFFD)} here".encode("utf-8"))
        self.assertEqual(len(problems), 1)
        self.assertIn("a.md:2: U+FFFD", problems[0])

    def test_double_encoded_text(self) -> None:
        # What 1.7.4 did to docs/CHANGELOG.md: UTF-8 read as Windows-1252, written back as UTF-8.
        good = "## 1.7.3 — 2026-09-09\nNão portado: migração"
        bad = good.encode("utf-8").decode("cp1252").encode("utf-8")
        problems = check_bytes("CHANGELOG.md", bad)
        self.assertEqual(len(problems), 1)
        # The mojibake is built at runtime, never written here, so this file passes the check it tests.
        dash = "—".encode("utf-8").decode("cp1252")
        self.assertIn(f"CHANGELOG.md:1: double-encoded text {dash!r} (should read '—')", problems[0])
        self.assertIn("4 occurrence(s)", problems[0])  # —, ã, ç, ã

    def test_double_encoded_text_beside_a_correct_accent(self) -> None:
        bad = "é" + "—".encode("utf-8").decode("cp1252")
        self.assertEqual(len(check_bytes("a.md", bad.encode("utf-8"))), 1)

    def test_cp1252_undefined_bytes_round_trip(self) -> None:
        # "Á" is C3 81; 0x81 is undefined in Windows-1252 and survives as U+0081.
        bad = chr(0xC3) + chr(0x81) + "rvore"
        # U+0081 is also a C1 control, so that is reported too; the mojibake line still names the fix.
        problems = check_bytes("a.md", bad.encode("utf-8"))
        self.assertTrue(any("should read 'Á'" in p for p in problems), problems)


class WideEncodingsAndBoms(unittest.TestCase):
    SAMPLE = '<Project Sdk="Microsoft.NET.Sdk">\r\n  <!-- v1.5.4 · … — -->\r\n</Project>\r\n'

    def test_empty_file_passes(self) -> None:
        self.assertEqual(check_bytes("a.md", b""), [])

    def test_bom_only_file_passes(self) -> None:
        self.assertEqual(check_bytes("a.md", b"\xef\xbb\xbf"), [])

    def test_utf16_and_utf32_with_bom_are_named(self) -> None:
        cases = [("utf-16-le", b"\xff\xfe", "UTF-16 LE"),
                 ("utf-16-be", b"\xfe\xff", "UTF-16 BE"),
                 ("utf-32-le", b"\xff\xfe\x00\x00", "UTF-32 LE"),
                 ("utf-32-be", b"\x00\x00\xfe\xff", "UTF-32 BE")]
        for codec, bom, label in cases:
            with self.subTest(codec=codec):
                problems = check_bytes("a.csproj", bom + self.SAMPLE.encode(codec))
                self.assertEqual(len(problems), 1, problems)
                self.assertIn(f"saved as {label} (BOM", problems[0])

    def test_utf16_without_bom_is_named(self) -> None:
        cases = [("utf-16-le", "UTF-16 LE without a BOM"), ("utf-16-be", "UTF-16 BE without a BOM")]
        for codec, label in cases:
            with self.subTest(codec=codec):
                problems = check_bytes("a.xaml", self.SAMPLE.encode(codec))
                self.assertEqual(problems, [f"a.xaml: saved as {label} — this repository is UTF-8"])

    def test_double_bom(self) -> None:
        problems = check_bytes("a.csproj", b"\xef\xbb\xbf\xef\xbb\xbf<Project />\n")
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("a.csproj:1: second BOM right after the first", problems[0])

    def test_orphan_bom_mid_file(self) -> None:
        # Two files concatenated, the second keeping its BOM.
        problems = check_bytes("a.md", b"# One\n" + b"\xef\xbb\xbf# Two\n")
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("a.md:2: BOM (U+FEFF) inside the file", problems[0])

    def test_double_encoded_bom(self) -> None:
        # The BOM itself read as Windows-1252 and written back as UTF-8, behind a fresh BOM.
        bad = b"\xef\xbb\xbf" + b"\xef\xbb\xbf".decode("cp1252").encode("utf-8") + b"<Project />\n"
        problems = check_bytes("a.csproj", bad)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("double-encoded text", problems[0])

    def test_c1_control_from_latin1_reading(self) -> None:
        # A Windows-1252 ellipsis (0x85) read as ISO-8859-1 and written back as UTF-8.
        problems = check_bytes("a.md", "wait\x85".encode("utf-8"))
        self.assertEqual(
            problems, ["a.md:1: C1 control character U+0085 — Windows-1252 text read as ISO-8859-1?"])

    def test_double_encoding_through_latin1(self) -> None:
        # The cp1252 detector cannot see this one: U+0080 has no Windows-1252 byte.
        bad = "—".encode("utf-8").decode("latin-1").encode("utf-8")
        problems = check_bytes("a.md", bad)
        self.assertEqual(len(problems), 1, problems)
        self.assertIn("C1 control character U+0080", problems[0])

    def test_truncated_bom_is_invalid_utf8(self) -> None:
        self.assertIn("not valid UTF-8", check_bytes("a.md", b"\xef\xbbok\n")[0])


@unittest.skipUnless(shutil.which("git"), "git is required for the tracked-file walk")
class TrackedFileWalk(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name)
        subprocess.run(["git", "init", "-q", str(self.root)], check=True)

    def track(self, name: str, data: bytes) -> None:
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        subprocess.run(["git", "-C", str(self.root), "-c", "core.autocrlf=false", "add", name], check=True)

    def run_main(self) -> tuple[int, str]:
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = check_encoding.main(["--root", str(self.root)])
        return code, out.getvalue()

    def test_clean_repo_exits_zero(self) -> None:
        self.track("src/A.xaml", "<Grid />\n".encode("utf-8"))
        code, out = self.run_main()
        self.assertEqual(code, 0, out)
        self.assertIn("encoding check OK: 1 tracked", out)

    def test_broken_tracked_file_exits_one(self) -> None:
        self.track("src/A.csproj", b"<Project>\x97</Project>\n")
        code, out = self.run_main()
        self.assertEqual(code, 1)
        self.assertIn("src/A.csproj:1:10: not valid UTF-8", out)

    def test_untracked_and_binary_files_are_ignored(self) -> None:
        self.track("README.md", b"fine\n")
        (self.root / "scratch.md").write_bytes(b"\x97 untracked\n")
        self.track("icon.ico", b"\x00\x01\x97")
        code, out = self.run_main()
        self.assertEqual(code, 0, out)

    def test_diagnostic_outside_the_console_code_page_does_not_crash(self) -> None:
        # The Windows runner pipes stdout through cp1252. "版" double-encoded is cp1252-safe, but the
        # "should read" half of the message is not.
        self.track("a.md", "版".encode("utf-8").decode("cp1252").encode("utf-8"))
        raw = io.BytesIO()
        stream = io.TextIOWrapper(raw, encoding="cp1252")
        with contextlib.redirect_stdout(stream):
            code = check_encoding.main(["--root", str(self.root)])
        stream.flush()
        self.assertEqual(code, 1)
        self.assertIn(b"should read '\\u7248'", raw.getvalue())

    def test_no_tracked_text_files_fails_closed(self) -> None:
        code, out = self.run_main()
        self.assertEqual(code, 1)
        self.assertIn("no tracked text files", out)


if __name__ == "__main__":
    unittest.main()
