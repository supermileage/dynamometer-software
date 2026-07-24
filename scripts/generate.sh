#!/usr/bin/env bash
# Regenerate the C# code that comes from the firmware's YAML schema. Run after changing it.
#
#   generate.py            -> Messages.cs, SysConfigCatalog.cs  (the wire contract)
#   error_msg_generate.py  -> ErrorCatalog.cs                   (every fault + its description)
#
# Both get the same arguments; each skips a --target it does not own, so
# `generate.sh --target error_catalog --stdout` reaches the one generator that has it.
# Usage: ./Scripts/generate.sh [generator args]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT/tools/message_gen"

[ -d .venv ] || python3 -m venv .venv
.venv/bin/pip install --quiet --upgrade pip
.venv/bin/pip install --quiet -r requirements.txt

for generator in generate.py error_msg_generate.py; do
  .venv/bin/python "$generator" "$@"
done
