<#
.SYNOPSIS
    Download the open-source flashing tools into Scripts\tools\windows-x86_64\, so nobody has to
    hunt down installers. flash.ps1 prefers these bundled copies over anything on PATH.

.DESCRIPTION
    What this fetches (Windows x86_64):
        st-flash / st-info   SWD      from the stlink release zip     (BSD-3-Clause)
        dfu-util             USB DFU  from the upstream win64 binaries (GPL-2.0+)
        stm32flash           UART     from the upstream win64 zip      (GPL-2.0)

    NOT fetched: STM32CubeProgrammer (cubeprog) — proprietary, ST forbids redistribution, and every
    method already has an open-source tool above. Install it yourself if you want it; flash.ps1
    finds it on PATH or in its default install dir.

    Each download is pinned to a version and checked against a SHA-256, so a supplier swapping a
    file under the URL fails loudly instead of running. Linux has its own downloader: get-tools.sh.

.EXAMPLE
    .\Scripts\get-tools.ps1
    .\Scripts\get-tools.ps1 -Force      # re-download even if present
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$dest = Join-Path $PSScriptRoot 'tools\windows-x86_64'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Fetch a URL to a temp file and verify its SHA-256 before use.
function Get-Pinned($url, $sha256) {
    $tmp = New-TemporaryFile
    Write-Host "  downloading $(Split-Path $url -Leaf)"
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
    $got = (Get-FileHash $tmp -Algorithm SHA256).Hash
    if ($got -ne $sha256.ToUpper()) {
        Remove-Item $tmp -Force
        throw "checksum mismatch for $url`n  expected $sha256`n  got      $got"
    }
    return $tmp
}

# Copy named entries out of an archive that tar/Expand-Archive unpacked to $root.
function Copy-Out($root, $names) {
    foreach ($n in $names) {
        $f = Get-ChildItem -Path $root -Recurse -Filter $n | Select-Object -First 1
        if (-not $f) { throw "expected '$n' in the download but it wasn't there" }
        Copy-Item $f.FullName -Destination $dest -Force
    }
}

# ---- st-flash + st-info + libstlink.dll (stlink 1.8.0 win32 zip) -------------
if ($Force -or -not (Test-Path (Join-Path $dest 'st-flash.exe'))) {
    Write-Host 'st-flash / st-info (stlink 1.8.0):'
    $zip = Get-Pinned `
        'https://github.com/stlink-org/stlink/releases/download/v1.8.0/stlink-1.8.0-win32.zip' `
        '134e479b4039d52376378f2eb5b97e6028ab5b36b178ea32e49f75693e51aae3'
    $out = Join-Path ([System.IO.Path]::GetTempPath()) ('stlink-' + [guid]::NewGuid())
    Expand-Archive -Path $zip -DestinationPath $out -Force
    Copy-Out $out @('st-flash.exe', 'st-info.exe', 'libstlink.dll')
    Remove-Item $zip, $out -Recurse -Force
}

# ---- dfu-util + libusb-1.0.dll (upstream win64 binaries) --------------------
if ($Force -or -not (Test-Path (Join-Path $dest 'dfu-util.exe'))) {
    Write-Host 'dfu-util (0.11):'
    $txz = Get-Pinned `
        'https://dfu-util.sourceforge.net/releases/dfu-util-0.11-binaries.tar.xz' `
        '6450de30a7dcd8d8c1273f43f0b153f054fd24d85f7f38296b1ad8edbd2ddb25'
    $out = Join-Path ([System.IO.Path]::GetTempPath()) ('dfu-' + [guid]::NewGuid())
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    # tar.exe (bsdtar) ships with Windows 10+ and reads .tar.xz.
    tar -xf $txz -C $out
    Copy-Out $out @('dfu-util.exe', 'libusb-1.0.dll')   # from the win64\ folder
    Remove-Item $txz, $out -Recurse -Force
}

# ---- stm32flash (upstream win64 zip) ----------------------------------------
if ($Force -or -not (Test-Path (Join-Path $dest 'stm32flash.exe'))) {
    Write-Host 'stm32flash (0.5):'
    $zip = Get-Pinned `
        'https://sourceforge.net/projects/stm32flash/files/stm32flash-0.5-win64.zip/download' `
        '3f9191a4d7ca3281a3e80e4d057ab2bacdf6fb94ca64189eee9c0f1651230a5b'
    $out = Join-Path ([System.IO.Path]::GetTempPath()) ('stm32flash-' + [guid]::NewGuid())
    Expand-Archive -Path $zip -DestinationPath $out -Force
    Copy-Out $out @('stm32flash.exe')
    Remove-Item $zip, $out -Recurse -Force
}

Write-Host ''
Write-Host "Done. Bundled in tools\windows-x86_64\:"
foreach ($b in 'st-flash.exe', 'st-info.exe', 'dfu-util.exe', 'stm32flash.exe') {
    if (Test-Path (Join-Path $dest $b)) { Write-Host "  [ok] $b" } else { Write-Host "  [--] $b (missing)" }
}
Write-Host ''
Write-Host 'flash.ps1 now uses these before anything on PATH. cubeprog is not bundled (proprietary).'
