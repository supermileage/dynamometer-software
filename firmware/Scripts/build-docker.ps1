<#
.SYNOPSIS
    Build the firmware inside the pinned Docker toolchain image on Windows —
    byte-for-byte the same environment CI uses. No host toolchain required.

.DESCRIPTION
    The repo is bind-mounted at /work in the container; output lands in
    build-docker/<Config>/ on the host.

    The toolchain image is Linux-based, so Docker Desktop must be in Linux
    container mode (the default).

    The image is built only when missing, or when -Rebuild is passed. The
    Dockerfile bakes in just the toolchain (the repo is mounted, never COPYed),
    so it rarely needs refreshing — only after the Dockerfile itself changes.

.EXAMPLE
    .\Scripts\build-docker.ps1                     # Debug
    .\Scripts\build-docker.ps1 -Config Release
    .\Scripts\build-docker.ps1 -Rebuild            # refresh the toolchain image
#>
param(
    [ValidateSet('Debug', 'Release')] [string]$Config = 'Debug',
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

$Image = 'stm32-dyno-builder'
$ProjectPath = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "docker not found. Install Docker Desktop first."
}

# `docker info` fails outright when the daemon is down, and reports the container
# platform when it's up. The ubuntu-based toolchain image can't run under Windows
# containers, which would otherwise fail with an opaque 'no matching manifest'.
try { $osType = docker info --format '{{.OSType}}' 2>$null } catch { $osType = '' }
if (-not $osType) {
    throw "Docker isn't responding. Is Docker Desktop running?"
}
if ($osType -ne 'linux') {
    throw "Docker is in $osType-container mode; the toolchain image is Linux. Right-click the Docker Desktop tray icon -> 'Switch to Linux containers...'."
}

if ($Rebuild -or -not (docker images -q $Image)) {
    Write-Host "Building toolchain image ($Image)..."
    docker build -t $Image $ProjectPath
    if ($LASTEXITCODE -ne 0) { throw "Toolchain image build failed." }
} else {
    Write-Host "Reusing existing image $Image (pass -Rebuild to refresh it)."
}

# Docker builds get their own tree, overriding the preset's binaryDir (build/).
# A CMake cache records the absolute source dir it was configured with: the
# container sees the repo at /work, a native/IDE build sees its real host path.
# Sharing one tree makes whichever build runs second abort with "the current
# CMakeCache.txt directory ... is different than the directory ... where
# CMakeCache.txt was created". Separate trees let both stay warm side by side.
#
# -B overrides binaryDir for the configure step; the build step then takes the
# directory directly, since --build --preset would resolve back to build/.
$BuildDir = "build-docker/$Config"

# Docker Desktop maps ownership of bind-mounted files automatically, so unlike
# the Linux path in build-docker.sh there's no --user flag to pass here.
Write-Host "Building firmware ($Config)..."
docker run --rm `
    -v "${ProjectPath}:/work" -w /work `
    $Image `
    bash -lc "python3 tools/message_gen/generate.py && cmake --preset $Config -B /work/$BuildDir && cmake --build /work/$BuildDir"

if ($LASTEXITCODE -ne 0) { throw "Firmware build failed." }

Write-Host ""
Write-Host "Build succeeded! Artifacts in $BuildDir/"
