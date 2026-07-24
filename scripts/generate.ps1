# Regenerate the C# code that comes from the firmware's YAML schema. Windows counterpart
# of generate.sh.
#
#   generate.py            -> Messages.cs, SysConfigCatalog.cs  (the wire contract)
#   error_msg_generate.py  -> ErrorCatalog.cs                   (every fault + its description)
#
# Both get the same arguments; each skips a --target it does not own.
# Usage: Scripts\generate.ps1 [generator args]
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
    foreach ($generator in @('generate.py', 'error_msg_generate.py')) {
        .venv\Scripts\python $generator @args
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
    exit 0
}
finally {
    Pop-Location
}
