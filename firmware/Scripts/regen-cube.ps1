<#
.SYNOPSIS
    Regenerate the HAL/driver sources and cmake/stm32cubemx/CMakeLists.txt from
    the .ioc by driving STM32CubeMX headlessly on Windows — the same result as
    clicking "Generate Code" in the GUI.

.DESCRIPTION
    Your USER CODE BEGIN/END blocks and the top-level CMakeLists.txt are
    preserved. CubeMX is not installed by winget and the download needs a free
    ST account, but running it needs no account — point this at the binary you
    installed.

    The binary is found in this order: -Cubemx, $env:STM32CUBEMX, common install
    locations, then STM32CubeMX on PATH. Windows always has a display, so no
    virtual-display wrapper is needed (unlike the Linux xvfb-run path in
    regen-cube.sh).

.PARAMETER Check
    Regenerate, then fail if it changed any tracked file — a drift check that
    catches a .ioc edited without committing the regenerated output. Without it,
    changes are left in the working tree for you to review.

.PARAMETER Cubemx
    Path to the STM32CubeMX launcher (.exe) or .jar. Overrides $env:STM32CUBEMX
    and the search of common install dirs.

.PARAMETER Ioc
    The .ioc to generate from (default: the project's single .ioc).

.EXAMPLE
    .\Scripts\regen-cube.ps1
    .\Scripts\regen-cube.ps1 -Cubemx "C:\Program Files\STMicroelectronics\STM32Cube\STM32CubeMX\STM32CubeMX.exe"
    .\Scripts\regen-cube.ps1 -Check
#>
param(
    [switch]$Check,
    [string]$Cubemx = $env:STM32CUBEMX,
    [string]$Ioc
)

$ErrorActionPreference = 'Stop'
$ProjectPath = Split-Path -Parent $PSScriptRoot

# --- locate the .ioc ---------------------------------------------------------
if (-not $Ioc) {
    $iocs = @(Get-ChildItem -Path $ProjectPath -Filter '*.ioc' -File | Sort-Object Name)
    switch ($iocs.Count) {
        0       { throw "No .ioc found in $ProjectPath (pass -Ioc <path>)." }
        1       { $Ioc = $iocs[0].FullName }
        default { throw "Multiple .ioc files found; pick one with -Ioc:`n  $($iocs.FullName -join "`n  ")" }
    }
}
if (-not (Test-Path -LiteralPath $Ioc -PathType Leaf)) { throw ".ioc not found: $Ioc" }
$Ioc = (Resolve-Path -LiteralPath $Ioc).Path

# --- locate STM32CubeMX ------------------------------------------------------
if (-not $Cubemx) {
    $candidates = @(
        "$env:ProgramFiles\STMicroelectronics\STM32Cube\STM32CubeMX\STM32CubeMX.exe"
        "${env:ProgramFiles(x86)}\STMicroelectronics\STM32Cube\STM32CubeMX\STM32CubeMX.exe"
        "$env:USERPROFILE\STM32CubeMX\STM32CubeMX.exe"
    )
    $Cubemx = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if (-not $Cubemx) {
        $onPath = Get-Command STM32CubeMX -ErrorAction SilentlyContinue
        if ($onPath) { $Cubemx = $onPath.Source }
    }
}
if (-not $Cubemx -or -not (Test-Path -LiteralPath $Cubemx -PathType Leaf)) {
    throw @"
STM32CubeMX not found. Set `$env:STM32CUBEMX, pass -Cubemx <path>, or install it
(needs a free ST account): https://www.st.com/en/development-tools/stm32cubemx.html
"@
}

# A .jar is launched via java; an .exe launcher is run directly.
if ($Cubemx -like '*.jar') {
    if (-not (Get-Command java -ErrorAction SilentlyContinue)) { throw "java not found (needed for $Cubemx)." }
    $exe = 'java'; $preArgs = @('-jar', $Cubemx)
} else {
    $exe = $Cubemx; $preArgs = @()
}

# --- verify the firmware pack the .ioc needs is installed --------------------
# If the HAL/firmware package the .ioc references isn't in the local repository,
# 'config load' hangs for many minutes while CubeMX tries to fetch it. Fail fast.
$repo = if ($env:STM32CUBE_REPO) { $env:STM32CUBE_REPO } else { Join-Path $env:USERPROFILE 'STM32Cube\Repository' }
$packLine = Select-String -LiteralPath $Ioc -Pattern '^ProjectManager\.FirmwarePackage=(.+)$' | Select-Object -First 1
if ($packLine) {
    $pack = $packLine.Matches[0].Groups[1].Value.Trim()
    $packDir = Join-Path $repo ($pack -replace ' ', '_')
    if ($pack -and -not (Test-Path -LiteralPath $packDir -PathType Container)) {
        Write-Host "ERROR: the firmware package this .ioc needs is not installed:"
        Write-Host "         $pack"
        Write-Host "       expected at: $packDir"
        Write-Host ""
        Write-Host "Without it, CubeMX hangs in 'config load' trying to download it. Install it first:"
        Write-Host '  headless (downloads from ST — needs internet + a free myST login the first time):'
        Write-Host ('     ''swmgr install "{0}" ask'',''exit'' | Set-Content inst.txt' -f $pack)
        Write-Host ('     & "{0}" -q inst.txt' -f $Cubemx)
        Write-Host '  from a pre-downloaded pack .zip (no login):  swmgr install C:\path\to\pack.zip deny'
        Write-Host '  or GUI: Help -> Manage embedded software packages -> STM32H7 -> tick it -> Install'
        Write-Host '  (set $env:STM32CUBE_REPO if your repository lives elsewhere)'
        exit 1
    }
}

# --- generate ----------------------------------------------------------------
$script = New-TemporaryFile
try {
    "config load $Ioc`nproject generate`nexit" | Set-Content -LiteralPath $script -Encoding ascii

    Write-Host "Regenerating from $(Split-Path -Leaf $Ioc) using $exe..."
    & $exe @preArgs -q $script.FullName
    if ($LASTEXITCODE -ne 0) { throw "STM32CubeMX exited with code $LASTEXITCODE." }
} finally {
    Remove-Item -LiteralPath $script -ErrorAction SilentlyContinue
}

# --- optional drift check ----------------------------------------------------
if ($Check) {
    # Ignore .mxproject: CubeMX rewrites this bookkeeping file on every generate,
    # so it churns even when no source changed, and it isn't compiled.
    git -C $ProjectPath diff --quiet -- . ':(exclude).mxproject'
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "ERROR: regeneration changed committed files — the generated code is"
        Write-Host "       out of date with the .ioc. Regenerate and commit:"
        git -C $ProjectPath --no-pager diff --stat -- . ':(exclude).mxproject'
        exit 1
    }
    Write-Host "Drift check passed: generated code matches the .ioc."
} else {
    Write-Host "Done. Review changes with: git diff"
}
