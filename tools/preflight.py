#!/usr/bin/env python3
"""Preflight: lay every sheet's rows and columns over each other and report what will fail.

  python3 tools/preflight.py            # errors fail the build; unverified cells are listed
  python3 tools/preflight.py --strict   # unverified cells fail too (release gate)
"""
import sys

from sheets import load_sheets, preflight


def main():
    strict = "--strict" in sys.argv
    sheets = load_sheets()
    errors, unverified = preflight(sheets)
    cells = sum(len(s["rows"]) * len(s["columns"]) for s in sheets.values())
    print(f"preflight: {len(sheets)} sheets, {cells} cells")
    for e in errors:
        print(f"  ERROR       {e}")
    for u in unverified:
        print(f"  UNVERIFIED  {u}")
    print(f"preflight: {len(errors)} errors, {len(unverified)} unverified cells")
    if errors or (strict and unverified):
        sys.exit(1)


if __name__ == "__main__":
    main()
