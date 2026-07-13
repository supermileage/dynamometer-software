# Regenerate the C# message types from the firmware's YAML schema (writes
# src/Dyno.Core/Messages/Generated/Messages.cs). Windows counterpart of generate.sh.
# Usage: Scripts\generate.ps1 [generate.py args]
$ErrorActionPreference = 'Stop'

Push-Location (Join-Path $PSScriptRoot '..\tools\message_gen')
try {
    if (-not (Test-Path .venv)) {
        python -m venv .venv
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
    .venv\Scripts\python -m pip install --quiet --upgrade pip
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    .venv\Scripts\python -m pip install --quiet -r requirements.txt
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    .venv\Scripts\python generate.py @args
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
