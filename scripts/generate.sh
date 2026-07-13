#!/usr/bin/env bash
# Regenerate the C# message types from the firmware's YAML schema (writes
# src/Dyno.Core/Messages/Generated/Messages.cs). Run after changing the schema.
# Usage: ./Scripts/generate.sh [generate.py args]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT/tools/message_gen"

[ -d .venv ] || python3 -m venv .venv
.venv/bin/pip install --quiet --upgrade pip
.venv/bin/pip install --quiet -r requirements.txt
exec .venv/bin/python generate.py "$@"
