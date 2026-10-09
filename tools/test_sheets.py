#!/usr/bin/env python3
"""Checks that preflight catches what it should, and that the generated C# matches the sheets."""
import copy
import subprocess
import sys
import unittest

from sheets import ROOT, load_sheets, preflight


class PreflightTests(unittest.TestCase):
    def setUp(self):
        self.sheets = load_sheets()

    def test_current_sheets_have_no_errors(self):
        errors, _ = preflight(self.sheets)
        self.assertEqual(errors, [])

    def test_broken_reference_is_an_error(self):
        s = copy.deepcopy(self.sheets)
        s["powers"]["rows"][0]["owner"] = "kanji"
        errors, _ = preflight(s)
        self.assertTrue(any("'kanji' is not a row of characters" in e for e in errors), errors)

    def test_empty_cell_is_an_error(self):
        s = copy.deepcopy(self.sheets)
        s["dialogue"]["rows"][0]["text"] = None
        errors, _ = preflight(s)
        self.assertTrue(any(e.endswith(".text: empty") for e in errors), errors)

    def test_missing_cell_is_an_error(self):
        s = copy.deepcopy(self.sheets)
        del s["bosses"]["rows"][0]["taunt_interval_s"]
        errors, _ = preflight(s)
        self.assertTrue(any("taunt_interval_s: missing" in e for e in errors), errors)

    def test_unverified_cells_are_listed(self):
        _, unverified = preflight(self.sheets)
        self.assertTrue(any(u.startswith("triggers[night1_boss].file_regex") for u in unverified))

    def test_generated_code_is_up_to_date(self):
        out = ROOT / "src" / "InvestigationNightmares.Core" / "Generated" / "Sheets.g.cs"
        before = out.read_text(encoding="utf-8")
        subprocess.run([sys.executable, str(ROOT / "tools" / "gen_code.py")], check=True, capture_output=True)
        self.assertEqual(before, out.read_text(encoding="utf-8"), "run tools/gen_code.py and commit the result")


if __name__ == "__main__":
    unittest.main()
