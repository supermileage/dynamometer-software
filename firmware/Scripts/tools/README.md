# Bundled flashing tools

These are the open-source flashing tools, **committed so a clone can flash with nothing installed**.
`flash.sh` / `flash.ps1` prefer a binary here over anything on `PATH` (their `resolve()` /
`Resolve-Tool`); `PATH` is the fallback. One directory per platform:

- `linux-x86_64/`
- `windows-x86_64/`

macOS and other architectures are not bundled (upstream ships no prebuilt binaries) — there the
scripts fall back to `PATH`, so install a tool with your package manager (see `../README.md`).

## What's here, and where it came from

Every binary is an **unmodified upstream release**, pinned to a version. The URL is the *corresponding
source/download* — for the GPL tools that is the source offer the license requires.

| Tool | Version | License | Upstream (pinned source/binary) |
|------|---------|---------|---------------------------------|
| `st-flash`, `st-info`, `libstlink` | stlink 1.8.0 | BSD-3-Clause | https://github.com/stlink-org/stlink/releases/tag/v1.8.0 |
| `dfu-util` | 0.11 | GPL-2.0-or-later | https://dfu-util.sourceforge.net/ · Linux from Debian `dfu-util_0.11-3` |
| `stm32flash` | 0.7 (Linux) / 0.5 (Windows) | GPL-2.0-or-later | https://sourceforge.net/projects/stm32flash/ |
| `libusb-1.0.dll` (Windows) | bundled with dfu-util 0.11 | LGPL-2.1-or-later | https://libusb.info/ |

License texts are in `LICENSES/` (`GPL-2.0.txt`, `LGPL-2.1.txt`; stlink's BSD-3-Clause is reproduced
below). The Linux `st-flash`/`dfu-util` are dynamically linked against the system `libusb`/`libudev`
(as a distro package would be); the Linux `stm32flash` is built from the source tarball above and
needs no such library.

**Not here:** STM32CubeProgrammer (`cubeprog`). It is proprietary — ST's license forbids
redistribution — so it is never committed (the repo's `.gitignore` blocks it explicitly). Every
method has an open-source tool above, so it is never required; if you install it, the scripts find
it on `PATH`.

## Refreshing / updating

These are produced by `../get-tools.sh` (Linux) and `../get-tools.ps1` (Windows), which download the
pinned releases, verify each against a SHA-256, and lay out the platform directory. **End users
don't run those — the binaries are already here.** They are the maintainer's tool for bumping a
version: edit the pin (and its checksum) in the script, run it, and commit the refreshed binary.

## stlink — BSD-3-Clause

```
Copyright (c) 2011 Fabien Le Mentec and the stlink project contributors.
All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions
   and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions
   and the following disclaimer in the documentation and/or other materials provided with the
   distribution.
3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse
   or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT ARISING IN ANY WAY OUT OF
THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
