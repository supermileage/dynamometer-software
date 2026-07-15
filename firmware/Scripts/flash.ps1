<#
.SYNOPSIS
    Flash the built firmware to the STM32H743 on Windows. You pick the method AND
    the tool explicitly — no auto-detect, no fallback — so the same command
    always uses the same tool (important when several probes/boards are attached).

.DESCRIPTION
    Methods and the tool each accepts:
        swd    SWD via ST-Link probe        -Tool cubeprog | st-flash | openocd
        dfu    USB DFU via ROM bootloader    -Tool cubeprog | dfu-util
        uart   UART via ROM bootloader       -Tool cubeprog | stm32flash
    (cubeprog = STM32CubeProgrammer's STM32_Programmer_CLI.)

    dfu and uart use the chip's built-in bootloader: hold BOOT0 high and reset
    the board first.

    Selecting among several connected devices:
        swd  -Serial <sn>   (find with: -Method swd  -Tool cubeprog -List)
        dfu  -Serial <sn>   (find with: -Method dfu  -Tool dfu-util -List)  or -Index <n>
        uart -Port <COMx>   (each adapter is a distinct COM port)

    Run on the host (needs USB/serial access; not via Docker). Build first:
        cmake --build --preset Release      # or .\Scripts\build-docker.ps1

.EXAMPLE
    .\Scripts\flash.ps1 -Method swd -Tool st-flash
    .\Scripts\flash.ps1 -Method swd -Tool cubeprog -Serial 0670FF...
    .\Scripts\flash.ps1 -Method dfu -Tool dfu-util -List
    .\Scripts\flash.ps1 -Method uart -Tool stm32flash -Port COM5
#>
param(
    [ValidateSet('Debug', 'Release')]                 [string]$Config = 'Release',
    [Parameter(Mandatory)][ValidateSet('swd','dfu','uart')] [string]$Method,
    [ValidateSet('cubeprog','st-flash','openocd','dfu-util','stm32flash')] [string]$Tool,
    [string]$Serial = '',
    [string]$Index  = '1',
    [string]$Port   = 'COM3',
    [string]$Baud   = '115200',
    [switch]$List
)

$ErrorActionPreference = 'Stop'

# Bundled tools (Scripts\tools\windows-x86_64\, populated by get-tools.ps1) are preferred over PATH,
# so a machine with nothing installed still flashes. A bundled .exe finds the DLLs shipped beside it
# automatically (Windows searches the app directory first), so there is nothing like LD_LIBRARY_PATH
# to set. cubeprog is never bundled (proprietary), so it always comes from PATH or its install dir.
$script:VendorDir = Join-Path $PSScriptRoot 'tools\windows-x86_64'

# Map a tool keyword to its executable name and resolve it: bundled, then PATH.
function Resolve-Tool($tool) {
    $exe = if ($tool -eq 'cubeprog') { 'STM32_Programmer_CLI' } else { $tool }
    $bundled = Join-Path $script:VendorDir "$exe.exe"
    if (Test-Path $bundled) { return $bundled }
    $cmd = Get-Command $exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    # STM32CubeProgrammer usually isn't on PATH on Windows; check its default dir.
    if ($tool -eq 'cubeprog') {
        $def = Join-Path ${env:ProgramFiles} `
            'STMicroelectronics\STM32Cube\STM32CubeProgrammer\bin\STM32_Programmer_CLI.exe'
        if (Test-Path $def) { return $def }
    }
    throw "'$exe' not found — not bundled and not on PATH. Run .\Scripts\get-tools.ps1, or install it (see README)."
}

$valid = @{ swd = @('cubeprog','st-flash','openocd'); dfu = @('cubeprog','dfu-util'); uart = @('cubeprog','stm32flash') }

# --- list mode ---------------------------------------------------------------
if ($List) {
    switch ($Method) {
        'swd' {
            switch ($(if ($Tool) { $Tool } else { 'cubeprog' })) {
                'cubeprog' { & (Resolve-Tool 'cubeprog') -l }
                'st-flash' {
                    # st-info (ships with stlink) is the universal probe lister — bundled first.
                    $si = Join-Path $script:VendorDir 'st-info.exe'
                    if (-not (Test-Path $si)) { $si = (Get-Command st-info -ErrorAction SilentlyContinue).Source }
                    if ($si) { & $si --probe } else { & (Resolve-Tool 'st-flash') --list }
                }
                default    { Write-Host "openocd has no list mode; use -Tool st-flash -List" }
            }
        }
        'dfu' {
            switch ($(if ($Tool) { $Tool } else { 'dfu-util' })) {
                'cubeprog' { & (Resolve-Tool 'cubeprog') -l usb }
                default    { & (Resolve-Tool 'dfu-util') -l }
            }
        }
        'uart' {
            Write-Host 'Serial ports:'
            [System.IO.Ports.SerialPort]::GetPortNames()
        }
    }
    exit $LASTEXITCODE
}

# --- flash mode --------------------------------------------------------------
if (-not $Tool) { throw "-Tool is required for $Method (one of: $($valid[$Method] -join ', '))" }
if ($valid[$Method] -notcontains $Tool) { throw "tool '$Tool' is not valid for $Method (use one of: $($valid[$Method] -join ', '))" }
$exe = Resolve-Tool $Tool

$ProjectPath = Split-Path -Parent $PSScriptRoot
$Name = 'stm32_dyno_firmware_v2'

# The firmware may come from either tree: build-docker/ (Scripts/build-docker.sh)
# or build/ (native or IDE build). Flash whichever image is newer, so "build,
# then flash" does the obvious thing no matter which builder produced it.
$BuildDir = @("build-docker/$Config", "build/$Config") |
    ForEach-Object { Join-Path $ProjectPath $_ } |
    Where-Object { Test-Path (Join-Path $_ "$Name.elf") } |
    Sort-Object { (Get-Item (Join-Path $_ "$Name.elf")).LastWriteTime } -Descending |
    Select-Object -First 1

if (-not $BuildDir) {
    throw "No $Config firmware found in build-docker/$Config/ or build/$Config/. Build first: .\Scripts\build-docker.ps1 -Config $Config"
}

$Elf = Join-Path $BuildDir "$Name.elf"
$Bin = Join-Path $BuildDir "$Name.bin"
Write-Host "Using $Elf"

if ($Method -ne 'swd') {
    Write-Host "$Method`: ensure the board is in bootloader mode (BOOT0 high, then reset)."
}

$cmdArgs = switch ("$Method`:$Tool") {
    'swd:cubeprog'   { $a = @('-c','port=SWD'); if ($Serial) { $a += "sn=$Serial" }; $a + @('-d',$Elf,'-rst') }
    'swd:st-flash'   { $a = @(); if ($Serial) { $a += @('--serial',$Serial) }; $a + @('--reset','write',$Bin,'0x08000000') }
    'swd:openocd'    { $a = @('-f','interface/stlink.cfg'); if ($Serial) { $a += @('-c',"adapter serial $Serial") }; $a + @('-f','target/stm32h7x.cfg','-c',"program $Elf verify reset exit") }
    'dfu:cubeprog'   { $a = @('-c',"port=USB$Index"); if ($Serial) { $a += "sn=$Serial" }; $a + @('-d',$Elf,'-rst') }
    'dfu:dfu-util'   { $a = @('-a','0'); if ($Serial) { $a += @('-S',$Serial) }; $a + @('-s','0x08000000:leave','-D',$Bin) }
    'uart:cubeprog'  { @('-c',"port=$Port","br=$Baud",'-d',$Elf,'-rst') }
    'uart:stm32flash'{ @('-b',$Baud,'-w',$Bin,'-v','-g','0x08000000',$Port) }
}

Write-Host "Flashing $Config via $Tool ($Method)..."
& $exe @cmdArgs
exit $LASTEXITCODE
