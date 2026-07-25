# Redraw src\Dyno.App\Assets\dyno.ico -- the window, taskbar and .exe icon -- from the same
# spec as the "D" badge in the title bar. Windows counterpart of generate-icon.sh.
#
# Run after changing that badge, so the two keep matching; the icon is committed, so there
# is no need to run it to build. Needs the nuget cache populated (dotnet restore).
# Usage: scripts\generate-icon.ps1 [--preview]
$ErrorActionPreference = 'Stop'

Push-Location (Join-Path $PSScriptRoot '..\tools\icon_gen')
try {
    if (-not (Test-Path .venv)) {
        python -m venv .venv
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
    .venv\Scripts\python -m pip install --quiet --upgrade pip
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    .venv\Scripts\python -m pip install --quiet -r requirements.txt
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    .venv\Scripts\python generate_icon.py @args
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
