#!/usr/bin/env bash
# Run the Avalonia desktop app natively. Needs a display (X11/Wayland on Linux) and,
# to do anything useful, a connected board. On Linux the user must be in the `dialout`
# group to open /dev/ttyACM*. Usage: ./Scripts/run.sh [-- <app args>]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_DLL="$ROOT/src/Dyno.App/bin/Debug/net10.0/Dyno.App.dll"

# Heal a build that was interrupted (Ctrl+C) partway through.
#
# Avalonia does not read .axaml at runtime: Roslyn emits Dyno.App.dll, then an Avalonia MSBuild task
# rewrites that same assembly to bake the XAML into it. Kill the build between those two steps and
# bin/ is left holding a DLL with no XAML in it — one whose timestamp is newer than every source
# file, so MSBuild judges it up to date and skips *both* steps on every later build. The app then
# dies at startup with "No precompiled XAML found for Dyno.App.App" forever. Worse, it is a
# GUI-subsystem process (WinExe), so the exception is written to a console that isn't there: what
# you actually see is a silent exit and a bare prompt. git will not undo it — bin/ and obj/ are not
# tracked.
#
# So check for the marker Avalonia bakes into a properly built assembly. Missing ⇒ torn build ⇒
# discard the artifacts, so the next build really rebuilds rather than skipping.
if [[ -f "$APP_DLL" ]] && ! grep -qa 'CompiledAvaloniaXaml' "$APP_DLL"; then
  echo "run.sh: last build was interrupted (Dyno.App.dll has no compiled XAML) — rebuilding clean" >&2
  rm -rf "$ROOT"/src/*/bin "$ROOT"/src/*/obj "$ROOT"/tests/*/bin "$ROOT"/tests/*/obj
fi

exec dotnet run --project "$ROOT/src/Dyno.App/Dyno.App.csproj" "$@"
