# Pinned build environment for stm32_dyno_firmware_v2.
# Used by both CI and local builds (Scripts/build-docker.sh) so the toolchain
# is identical everywhere. The repo is mounted at /work at run time; nothing is
# copied into the image.

FROM ubuntu:24.04

ARG DEBIAN_FRONTEND=noninteractive
# Arm GNU toolchain release (same one CI pins). Override with --build-arg.
ARG ARM_TOOLCHAIN_VERSION=13.3.rel1
# SHA256 of arm-gnu-toolchain-${VERSION}-x86_64-arm-none-eabi.tar.xz.
# Update alongside ARM_TOOLCHAIN_VERSION.
ARG ARM_TOOLCHAIN_SHA256=95c011cee430e64dd6087c75c800f04b9c49832cc1000127a92a97f9c8d83af4

# Host build tools. python3 + jinja2 + pyyaml are for tools/message_gen, which
# regenerates the MessagePassing headers from their YAML schema before each build.
RUN apt-get update && apt-get install -y --no-install-recommends \
        cmake \
        ninja-build \
        make \
        git \
        ca-certificates \
        wget \
        xz-utils \
        libncurses-dev \
        python3 \
        python3-jinja2 \
        python3-yaml \
    && rm -rf /var/lib/apt/lists/*

# Arm GNU bare-metal toolchain (x86_64 host), pinned and installed to /opt
RUN wget -q \
        "https://developer.arm.com/-/media/Files/downloads/gnu/${ARM_TOOLCHAIN_VERSION}/binrel/arm-gnu-toolchain-${ARM_TOOLCHAIN_VERSION}-x86_64-arm-none-eabi.tar.xz" \
        -O /tmp/arm-toolchain.tar.xz \
    && echo "${ARM_TOOLCHAIN_SHA256}  /tmp/arm-toolchain.tar.xz" | sha256sum -c - \
    && mkdir -p /opt/arm-toolchain \
    && tar -xf /tmp/arm-toolchain.tar.xz -C /opt/arm-toolchain --strip-components=1 \
    && rm /tmp/arm-toolchain.tar.xz

ENV PATH="/opt/arm-toolchain/bin:${PATH}"

WORKDIR /work

# Sanity check at build time
RUN arm-none-eabi-gcc --version && cmake --version && ninja --version
