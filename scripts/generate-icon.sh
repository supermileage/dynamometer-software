#!/usr/bin/env bash
# Redraw src/Dyno.App/Assets/dyno.ico -- the window, taskbar and .exe icon -- from the
# same spec as the "D" badge in the title bar. Run after changing that badge, so the two
# keep matching; the icon is committed, so there is no need to run it to build.
#
# Needs the nuget cache populated (dotnet restore): the letter is drawn with the Inter
# Bold that Avalonia itself lays the badge out in, read out of the Avalonia.Fonts.Inter
# package.
# Usage: ./scripts/generate-icon.sh [--preview]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT/tools/icon_gen"

[ -d .venv ] || python3 -m venv .venv
.venv/bin/pip install --quiet --upgrade pip
.venv/bin/pip install --quiet -r requirements.txt

.venv/bin/python generate_icon.py "$@"
