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

# --- provide the firmware pack from the bundled submodule --------------------
# The HAL/firmware pack is vendored as the STM32CubeH7 submodule, pinned to the
# version the .ioc names, so regenerating needs no myST account. CubeMX only
# looks for packs in its own repository, so link the submodule in there under the
# exact directory name it expects (a junction needs no elevation). A real pack
# already installed there wins. With no pack at all CubeMX hangs in 'config
# load' rather than reporting anything, so bail out early instead.
$repo = if ($env:STM32CUBE_REPO) { $env:STM32CUBE_REPO } else { Join-Path $env:USERPROFILE 'STM32Cube\Repository' }
$packLine = Select-String -LiteralPath $Ioc -Pattern '^ProjectManager\.FirmwarePackage=(.+)$' | Select-Object -First 1
if ($packLine) {
    $pack = $packLine.Matches[0].Groups[1].Value.Trim()
    $packDir = Join-Path $repo ($pack -replace ' ', '_')
    $submod = Join-Path $ProjectPath 'third_party\STM32CubeH7'
    if ($pack -and -not (Test-Path -LiteralPath $packDir -PathType Container)) {
        if (Test-Path -LiteralPath (Join-Path $submod 'package.xml') -PathType Leaf) {
            # STM32CubeH7 is a meta-repo whose sources are nested submodules: a
            # plain clone leaves Drivers/ empty, which stalls CubeMX exactly like
            # a missing pack. Populate the two it needs.
            $halSrc = Join-Path $submod 'Drivers\STM32H7xx_HAL_Driver\Src'
            if (-not (Test-Path $halSrc) -or -not (Get-ChildItem $halSrc -ErrorAction SilentlyContinue)) {
                Write-Host "Populating STM32CubeH7 HAL/CMSIS sources..."
                git -C $submod submodule update --init --depth 1 -- `
                    Drivers/CMSIS/Device/ST/STM32H7xx Drivers/STM32H7xx_HAL_Driver
            }
            New-Item -ItemType Directory -Force -Path $repo | Out-Null
            New-Item -ItemType Junction -Path $packDir -Target $submod | Out-Null
            Write-Host "Using bundled pack: $packDir -> third_party\STM32CubeH7"
        } else {
            Write-Host "ERROR: the firmware pack this .ioc needs is not available:"
            Write-Host "         $pack"
            Write-Host ""
            Write-Host "It is vendored as a submodule — initialise it:"
            Write-Host "     git submodule update --init firmware/third_party/STM32CubeH7"
            Write-Host "  (or install the pack into $repo yourself, or set `$env:STM32CUBE_REPO.)"
            exit 1
        }
    }
}

# CubeMX shows a migration prompt when its version differs from the one that
# wrote the .ioc. Headless there is nobody to answer it, so the run just hangs at
# 'config load' with no output — surface the expected version up front.
$verLine = Select-String -LiteralPath $Ioc -Pattern '^MxCube\.Version=(.+)$' | Select-Object -First 1
if ($verLine) {
    Write-Host ("Note: this .ioc was written by STM32CubeMX {0}; a different version will stall on a migration prompt." -f $verLine.Matches[0].Groups[1].Value.Trim())
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
