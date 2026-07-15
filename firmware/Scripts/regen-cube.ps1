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
    git -C $ProjectPath diff --quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "ERROR: regeneration changed tracked files — the committed generated"
        Write-Host "       code is out of date with the .ioc. Regenerate and commit:"
        git -C $ProjectPath --no-pager diff --stat
        exit 1
    }
    Write-Host "Drift check passed: generated code matches the .ioc."
} else {
    Write-Host "Done. Review changes with: git diff"
}
