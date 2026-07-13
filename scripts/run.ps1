# Run the Avalonia desktop app natively. Windows counterpart of run.sh; to do anything
# useful you need a connected board (shows up as COMx, no driver needed on Win10+).
# Usage: Scripts\run.ps1 [<app args>]
$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..'
$appDll = Join-Path $root 'src\Dyno.App\bin\Debug\net10.0\Dyno.App.dll'

# Heal a build that was interrupted (Ctrl+C) partway through.
#
# Avalonia does not read .axaml at runtime: Roslyn emits Dyno.App.dll, then an Avalonia MSBuild task
# rewrites that same assembly to bake the XAML into it. Kill the build between those two steps and
# bin\ is left holding a DLL with no XAML in it -- one whose timestamp is newer than every source
# file, so MSBuild judges it up to date and skips *both* steps on every later build. The app then
# dies at startup with "No precompiled XAML found for Dyno.App.App" forever. Worse, it is a
# GUI-subsystem process (WinExe), so the exception is written to a console that isn't there: what
# you actually see is a silent exit and a bare prompt. git will not undo it -- bin\ and obj\ are not
# tracked, so `git reset --hard` changes nothing.
#
# So check for the marker Avalonia bakes into a properly built assembly. Missing => torn build =>
# discard the artifacts, so the next build really rebuilds rather than skipping.
if (Test-Path $appDll) {
    $dllText = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($appDll))
    if (-not $dllText.Contains('CompiledAvaloniaXaml')) {
        Write-Warning 'Last build was interrupted (Dyno.App.dll has no compiled XAML) - rebuilding clean.'
        dotnet build-server shutdown | Out-Null
        $stale = @(
            (Join-Path $root 'src\*\bin'),
            (Join-Path $root 'src\*\obj'),
            (Join-Path $root 'tests\*\bin'),
            (Join-Path $root 'tests\*\obj')
        )
        Remove-Item -Path $stale -Recurse -Force -ErrorAction SilentlyContinue
    }
}

dotnet run --project (Join-Path $root 'src\Dyno.App\Dyno.App.csproj') @args
exit $LASTEXITCODE
