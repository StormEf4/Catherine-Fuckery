"""Load and cross-check the design sheets in sheets/*.json.

Every row x column crossing is a checkbox:
  ERROR       the cell is empty (and the column isn't nullable), has the wrong type,
              or names a row that doesn't exist in the referenced sheet.
  UNVERIFIED  the cell is filled but listed in the row's "_unverified": it is a best
              guess that has to be checked against a real install (see `recon`).
"""
import json
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parent.parent
SHEETS_DIR = ROOT / "sheets"
ID_RE = re.compile(r"^[a-z0-9_]+$")


def load_sheets(sheets_dir=SHEETS_DIR):
    sheets = {}
    for path in sorted(pathlib.Path(sheets_dir).glob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        name = data["sheet"]
        if name != path.stem:
            raise ValueError(f"{path.name}: sheet name '{name}' must match the file name")
        sheets[name] = data
    return sheets


def _type_ok(col, value):
    t = col["type"]
    if t in ("id", "string", "ref"):
        return isinstance(value, str) and value != ""
    if t == "int":
        return isinstance(value, int) and not isinstance(value, bool)
    if t == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if t == "bool":
        return isinstance(value, bool)
    if t == "enum":
        return value in col["values"]
    if t == "refs":
        return isinstance(value, list) and len(value) > 0 and all(isinstance(v, str) for v in value)
    raise ValueError(f"unknown column type {t}")


def preflight(sheets):
    """Return (errors, unverified) as lists of human-readable strings."""
    errors, unverified = [], []
    ids = {name: {r.get("id") for r in s["rows"]} for name, s in sheets.items()}

    for name, sheet in sheets.items():
        cols = sheet["columns"]
        id_cols = [c for c, d in cols.items() if d["type"] == "id"]
        if id_cols != ["id"]:
            errors.append(f"{name}: needs exactly one id column named 'id'")
        for c, d in cols.items():
            if d["type"] in ("ref", "refs") and d.get("sheet") not in sheets:
                errors.append(f"{name}.{c}: references missing sheet '{d.get('sheet')}'")
        seen = set()
        for i, row in enumerate(sheet["rows"]):
            rid = row.get("id", f"#{i}")
            where = f"{name}[{rid}]"
            if rid in seen:
                errors.append(f"{where}: duplicate id")
            seen.add(rid)
            if isinstance(rid, str) and not ID_RE.match(rid):
                errors.append(f"{where}: id must be lower_snake_case")
            for key in row:
                if not key.startswith("_") and key not in cols:
                    errors.append(f"{where}.{key}: not a column of {name}")
            for u in row.get("_unverified", []):
                if u not in cols:
                    errors.append(f"{where}: _unverified names unknown column '{u}'")
            for c, d in cols.items():
                cell = f"{where}.{c}"
                if c not in row:
                    errors.append(f"{cell}: missing")
                    continue
                v = row[c]
                if v is None:
                    if not d.get("nullable"):
                        errors.append(f"{cell}: empty")
                    elif c in row.get("_unverified", []):
                        unverified.append(f"{cell}: empty, still to be found")
                    continue
                if not _type_ok(d, v):
                    errors.append(f"{cell}: {v!r} is not a valid {d['type']}")
                    continue
                if d["type"] == "ref" and v not in ids.get(d["sheet"], ()):
                    errors.append(f"{cell}: '{v}' is not a row of {d['sheet']}")
                if d["type"] == "refs":
                    for ref in v:
                        if ref not in ids.get(d["sheet"], ()):
                            errors.append(f"{cell}: '{ref}' is not a row of {d['sheet']}")
                if c in row.get("_unverified", []):
                    unverified.append(f"{cell} = {v!r}")
                if d["type"] == "string" and c.endswith("regex"):
                    try:
                        re.compile(v)
                    except re.error as e:
                        errors.append(f"{cell}: bad regex ({e})")
    return errors, unverified
