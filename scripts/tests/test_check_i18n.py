#!/usr/bin/env python3
"""Tests for scripts/check-i18n.py.

Run from the repository root:
    py -m unittest discover -s scripts/tests

stdlib `unittest` rather than pytest on purpose: the i18n workflow (.github/workflows/i18n-check.yml)
runs a bare `python scripts/check-i18n.py` with no `pip install` step, and a test suite that needs one
would either bit-rot or force a dependency into CI for one file.

Every case builds a throwaway RESX tree in a temp directory, so nothing here reads or writes the real
resources. `check()` is called directly instead of shelling out — the checker's job is to decide, and
asserting on the decision is more useful than asserting on stdout formatting.
"""
from __future__ import annotations

import contextlib
import importlib.util
import io
import sys
import tempfile
import unittest
from pathlib import Path

# The script is `check-i18n.py`: a hyphen is not a legal module name, so it cannot be imported normally.
_SCRIPT = Path(__file__).resolve().parent.parent / "check-i18n.py"
_spec = importlib.util.spec_from_file_location("check_i18n", _SCRIPT)
assert _spec is not None and _spec.loader is not None
check_i18n = importlib.util.module_from_spec(_spec)
sys.modules["check_i18n"] = check_i18n
_spec.loader.exec_module(check_i18n)


def resx(entries: list[tuple[str, str]]) -> str:
    """A minimal but well-formed RESX carrying the given (name, value) pairs."""
    body = "\n".join(
        f'  <data name="{name}" xml:space="preserve"><value>{value}</value></data>'
        for name, value in entries
    )
    return f'<?xml version="1.0" encoding="utf-8"?>\n<root>\n{body}\n</root>\n'


def designer(keys: list[str]) -> str:
    body = "\n".join(
        f"        public static string {k} => Get(nameof({k}));" for k in keys
    )
    return f"namespace LuaToolsGui.Resources;\npublic static class Strings\n{{\n{body}\n}}\n"


class CheckI18nTestCase(unittest.TestCase):
    """Builds an isolated <root>/src/LuaToolsGui/Resources tree per test."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.res = self.root / "src" / "LuaToolsGui" / "Resources"
        self.res.mkdir(parents=True)
        self.addCleanup(self._tmp.cleanup)

    def write_english(self, entries: list[tuple[str, str]]) -> None:
        (self.res / "Strings.resx").write_text(resx(entries), encoding="utf-8")

    def write_translation(self, tag: str, entries: list[tuple[str, str]]) -> None:
        (self.res / f"Strings.{tag}.resx").write_text(resx(entries), encoding="utf-8")

    def write_designer(self, keys: list[str]) -> None:
        (self.res / "Strings.Designer.cs").write_text(designer(keys), encoding="utf-8")

    def write_reference_set(self) -> None:
        """English + two translations + a matching designer: the shape that must pass cleanly."""
        entries = [("App_Title", "LuaTools"), ("Greet", "Hello {0}")]
        self.write_english(entries)
        self.write_translation("pt-BR", [("App_Title", "LuaTools"), ("Greet", "Olá {0}")])
        self.write_translation("fr", [("App_Title", "LuaTools"), ("Greet", "Bonjour {0}")])
        self.write_designer(["App_Title", "Greet"])

    def problems(self) -> list[str]:
        found, _ = check_i18n.check(self.res)
        return found

    @staticmethod
    def run_main(argv: list[str]) -> int:
        """Call main() with its report swallowed — these tests assert on the exit code, and letting the
        real-repository case print its 40-line PENDING_TRANSLATION list buries the actual results."""
        with contextlib.redirect_stdout(io.StringIO()):
            return check_i18n.main(argv)

    def assertClean(self) -> None:
        found = self.problems()
        self.assertEqual(found, [], f"expected a clean run, got: {found}")

    def assertProblem(self, needle: str) -> None:
        found = self.problems()
        self.assertTrue(
            any(needle in p for p in found),
            f"expected a problem mentioning {needle!r}, got: {found}",
        )


class TestHappyPath(CheckI18nTestCase):
    def test_a_consistent_reference_set_passes(self) -> None:
        self.write_reference_set()
        self.assertClean()

    def test_notes_report_the_counts(self) -> None:
        self.write_reference_set()
        _, notes = check_i18n.check(self.res)
        self.assertIn("2 language files", notes[0])
        self.assertIn("2 keys each", notes[0])

    def test_a_pending_translation_key_may_be_missing_everywhere(self) -> None:
        # PENDING_TRANSLATION is the sanctioned English-only escape hatch; a key listed there must not be
        # reported as missing, or the whole list would be useless.
        pending = next(iter(check_i18n.PENDING_TRANSLATION))
        self.write_english([("App_Title", "LuaTools"), (pending, "English only")])
        self.write_translation("pt-BR", [("App_Title", "LuaTools")])
        self.write_designer(["App_Title", pending])
        self.assertClean()


class TestDuplicateKeys(CheckI18nTestCase):
    def test_a_duplicate_key_in_a_translation_is_reported(self) -> None:
        # The false negative this check exists for: a dict assignment silently kept the LAST value, so a
        # translator could fix a string, watch the check pass, and ship the other copy.
        self.write_english([("App_Title", "LuaTools")])
        self.write_translation("pt-BR", [("App_Title", "primeiro"), ("App_Title", "segundo")])
        self.write_designer(["App_Title"])
        self.assertProblem("duplicate key")

    def test_a_duplicate_key_in_english_is_reported(self) -> None:
        self.write_english([("App_Title", "one"), ("App_Title", "two")])
        self.write_designer(["App_Title"])
        self.assertProblem("duplicate key")


class TestKeyParity(CheckI18nTestCase):
    def test_a_key_missing_from_one_language_is_reported(self) -> None:
        self.write_english([("App_Title", "LuaTools"), ("Only_In_English", "hi")])
        self.write_translation("pt-BR", [("App_Title", "LuaTools")])
        self.write_designer(["App_Title", "Only_In_English"])
        self.assertProblem("missing 1 key")

    def test_an_unknown_key_in_a_language_is_reported(self) -> None:
        self.write_english([("App_Title", "LuaTools")])
        self.write_translation("pt-BR", [("App_Title", "LuaTools"), ("Ghost", "?")])
        self.write_designer(["App_Title"])
        self.assertProblem("unknown key")

    def test_a_placeholder_mismatch_is_reported(self) -> None:
        self.write_english([("Greet", "Hello {0}")])
        self.write_translation("pt-BR", [("Greet", "Olá {0} {1}")])
        self.write_designer(["Greet"])
        self.assertProblem("placeholder mismatch")

    def test_invalid_xml_is_reported_and_does_not_abort_the_run(self) -> None:
        self.write_english([("App_Title", "LuaTools")])
        self.write_designer(["App_Title"])
        (self.res / "Strings.pt-BR.resx").write_text("<root><data", encoding="utf-8")
        self.assertProblem("INVALID XML")


class TestDesignerParity(CheckI18nTestCase):
    def test_a_resx_key_without_an_accessor_is_reported(self) -> None:
        self.write_english([("App_Title", "LuaTools"), ("Unreachable", "x")])
        self.write_designer(["App_Title"])
        self.assertProblem("have no accessor")

    def test_an_accessor_without_a_resx_key_is_reported(self) -> None:
        # Strings.Get falls back to returning the key NAME, so the UI would render "Ghost_Key" to users.
        self.write_english([("App_Title", "LuaTools")])
        self.write_designer(["App_Title", "Ghost_Key"])
        self.assertProblem("have no RESX key")

    def test_a_missing_designer_is_reported(self) -> None:
        self.write_english([("App_Title", "LuaTools")])
        self.assertProblem("not found")


class TestRootResolution(CheckI18nTestCase):
    def test_main_accepts_an_explicit_root(self) -> None:
        self.write_reference_set()
        self.assertEqual(self.run_main(["--root", str(self.root)]), 0)

    def test_main_reports_failure_through_the_exit_code(self) -> None:
        self.write_english([("App_Title", "LuaTools"), ("Unreachable", "x")])
        self.write_designer(["App_Title"])
        self.assertEqual(self.run_main(["--root", str(self.root)]), 1)

    def test_a_root_without_resources_is_an_error_not_a_crash(self) -> None:
        with tempfile.TemporaryDirectory() as empty:
            self.assertEqual(self.run_main(["--root", empty]), 1)

    def test_the_default_root_finds_the_real_repository(self) -> None:
        # The script resolves the repo from its own location, so it works from any working directory.
        # This also runs the checker against the REAL resource tree — a second line of defence.
        self.assertEqual(self.run_main([]), 0)


class TestSampleFormatting(CheckI18nTestCase):
    def test_a_long_list_reports_the_remainder_rather_than_truncating_silently(self) -> None:
        keys = [f"Key_{i:02d}" for i in range(25)]
        self.write_english([(k, "v") for k in keys])
        self.write_designer(keys)
        self.write_translation("pt-BR", [("Key_00", "v")])
        found = self.problems()
        self.assertTrue(any("+14 more" in p for p in found), found)


if __name__ == "__main__":
    unittest.main()
