#!/usr/bin/env bash
# Preflight the sheets, regenerate code, test, then package the Reloaded-II mod and the recon tool.
# Works on Linux/macOS/Git Bash with python3 and the .NET 8 SDK. Output: dist/
set -euo pipefail
cd "$(dirname "$0")"
VERSION=$(python3 -c 'import json;print(json.load(open("src/InvestigationNightmares.Mod/ModConfig.json"))["ModVersion"])')

python3 tools/preflight.py
python3 tools/gen_code.py
(cd tools && python3 -m unittest -q test_sheets)
dotnet test tests/InvestigationNightmares.Tests -c Release

rm -rf dist && mkdir -p dist/stage
dotnet publish src/InvestigationNightmares.Mod -c Release -o dist/stage/investigation.team.nightmares -p:DebugType=none
dotnet publish src/InvestigationNightmares.Recon -c Release -r win-x64 --self-contained false -o dist/stage/recon -p:DebugType=none
cp README.md dist/stage/investigation.team.nightmares/README.md

(cd dist/stage && python3 - "$VERSION" <<'EOF'
import sys, zipfile, os
v = sys.argv[1]
for folder, name in [("investigation.team.nightmares", f"InvestigationTeamNightmares-{v}-reloaded.zip"), ("recon", f"InvestigationTeamNightmares-recon-{v}-win-x64.zip")]:
    with zipfile.ZipFile(os.path.join("..", name), "w", zipfile.ZIP_DEFLATED) as z:
        for root, _, files in os.walk(folder):
            for f in files:
                p = os.path.join(root, f)
                z.write(p, p)
    print("dist/" + name)
EOF
)
rm -rf dist/stage
python3 tools/preflight.py --strict >/dev/null 2>&1 || echo "NOTE: unverified sheet cells remain (python3 tools/preflight.py); this build is untested in game."
