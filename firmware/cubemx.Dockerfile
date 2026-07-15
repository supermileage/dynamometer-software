# PRIVATE image for the .ioc drift-check CI job (.github/workflows/firmware.yml,
# job "ioc-drift"). It bundles STM32CubeMX and the STM32Cube FW_H7 pack.
#
# ============================ LICENSING — READ THIS ==========================
# STM32CubeMX and the STM32Cube firmware packs are ST-licensed and MUST NOT be
# redistributed publicly. Build this image and push it to a PRIVATE registry
# only. An ST/myST account is needed to obtain CubeMX + the pack; those
# credentials are used here, at image-build time — NEVER in CI.
# =============================================================================
#
# CubeMX bundles its own JRE, so no Java is installed here — only the native X
# libraries its SWT UI links against, a virtual display (xvfb), and git for the
# drift check.
#
# Build context must contain (they are ST-licensed, so they live outside the
# repo — assemble a scratch context, do not copy them into the source tree):
#   ./STM32CubeMX/    a full STM32CubeMX install   (your ~/STM32CubeMX)
#   ./Repository/     firmware packs               (your ~/STM32Cube/Repository,
#                     must include STM32Cube_FW_H7_V1.12.1)
#
# Build & push (example — GitHub Container Registry, kept PRIVATE):
#   ctx="$(mktemp -d)"
#   cp firmware/cubemx.Dockerfile "$ctx/Dockerfile"
#   cp -r ~/STM32CubeMX          "$ctx/STM32CubeMX"
#   cp -r ~/STM32Cube/Repository "$ctx/Repository"
#   docker build -t ghcr.io/<org>/cubemx-h7:6.18.0 "$ctx"
#   docker push  ghcr.io/<org>/cubemx-h7:6.18.0
#   # keep the package PRIVATE, then wire it up (see firmware/README.md):
#   #   repo variable CUBEMX_IMAGE      = ghcr.io/<org>/cubemx-h7:6.18.0
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
COPY Repository/  /opt/STM32Cube/Repository/

# regen-cube.sh reads these to find CubeMX and the pack repository.
ENV STM32CUBEMX=/opt/STM32CubeMX/STM32CubeMX \
    STM32CUBE_REPO=/opt/STM32Cube/Repository

# Fail the build early (not silently in CI) if the required pack is absent.
RUN test -x "$STM32CUBEMX" \
      || (echo "ERROR: $STM32CUBEMX missing/not executable in build context" >&2; exit 1) \
 && test -d "$STM32CUBE_REPO/STM32Cube_FW_H7_V1.12.1" \
      || (echo "ERROR: STM32Cube_FW_H7_V1.12.1 missing from Repository/ build context" >&2; exit 1)
