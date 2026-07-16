# PRIVATE image for the .ioc drift-check CI job (.github/workflows/firmware.yml,
# job "ioc-drift"). It bundles STM32CubeMX only.
#
# ============================ LICENSING — READ THIS ==========================
# STM32CubeMX is ST-licensed and MUST NOT be redistributed publicly. Build this
# image and push it to a PRIVATE registry only. An ST/myST account is needed to
# obtain CubeMX; those credentials are used here, at image-build time — NEVER in
# CI. The firmware *pack* is deliberately NOT baked in: it comes from the public
# STM32CubeH7 submodule at run time, so CubeMX is the only licensed thing here.
# =============================================================================
#
# The CubeMX version MUST match the .ioc's MxCube.Version. If it doesn't, CubeMX
# raises a migration prompt that nothing can answer headlessly, and every run
# stalls silently at 'config load'. Tag the image with the version it contains.
#
# CubeMX bundles its own JRE, so no Java is installed here — only the native X
# libraries its SWT UI links against, a virtual display (xvfb), and git (for the
# drift check and to populate the pack submodule).
#
# Build context must contain:
#   ./STM32CubeMX/    a full STM32CubeMX install   (your ~/STM32CubeMX)
#
# Build & push (example — GitHub Container Registry, kept PRIVATE):
#   ctx="$(mktemp -d)"
#   cp firmware/cubemx.Dockerfile "$ctx/Dockerfile"
#   cp -r ~/STM32CubeMX          "$ctx/STM32CubeMX"
#   docker build -t ghcr.io/<org>/cubemx:6.15.0 "$ctx"
#   docker push  ghcr.io/<org>/cubemx:6.15.0
#   # keep the package PRIVATE, then wire it up (see firmware/README.md):
#   #   repo variable CUBEMX_IMAGE       = ghcr.io/<org>/cubemx:6.15.0
#   #   repo secret   CUBEMX_IMAGE_TOKEN = a token that can pull that package

FROM ubuntu:24.04
ARG DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends \
        xvfb \
        git \
        ca-certificates \
        fontconfig \
        libfreetype6 \
        libgtk-3-0 \
        libxtst6 \
        libxrender1 \
        libxi6 \
    && rm -rf /var/lib/apt/lists/*

# ST-licensed payload, supplied via the build context (see header).
COPY STM32CubeMX/ /opt/STM32CubeMX/

# regen-cube.sh reads this to find CubeMX. It links the pack in from the
# submodule itself, so no repository is baked into the image.
ENV STM32CUBEMX=/opt/STM32CubeMX/STM32CubeMX

# Fail the build early (not silently in CI) if CubeMX didn't come through.
RUN test -x "$STM32CUBEMX" \
      || (echo "ERROR: $STM32CUBEMX missing/not executable in build context" >&2; exit 1)
