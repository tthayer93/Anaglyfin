#!/bin/sh
#
# Installs the Anaglyfin FFmpeg runtime into a running Jellyfin container.
#
# Run it INSIDE the official `jellyfin/jellyfin` container, as root, with the server's
# data directory mounted at /config:
#
#   docker compose exec jellyfin /config/anaglyfin/install-runtime.sh
#
# It fills one directory with the three files the plugin needs, and creates the two
# directories the wrapper writes into:
#
#   /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg   Jellyfin's FFmpeg path (JELLYFIN_FFMPEG)
#   /config/anaglyfin/ffmpeg/ffmpeg-mvc         the encoder the wrapper runs (ANAGLYFIN_REAL_FFMPEG)
#   /config/anaglyfin/ffmpeg/ffprobe            ffprobe from that same FFmpeg-mvc build
#   /config/anaglyfin/lock                      the wrapper's concurrency slots
#   /config/anaglyfin/wrapper                   the settings document the plugin publishes
#
# /config is the persisted volume, so the files above outlive the container. The apt
# packages the build needs - and the shared libraries `ffmpeg-mvc` and `ffprobe` link
# against when they run - are part of the container's own filesystem instead, and a
# recreate throws them away. That is why the apt step runs on every invocation and never
# skips: re-running this script against a freshly recreated container is what puts the
# runtime libraries back.
#
# Re-running is also the upgrade path. A version that matches what is recorded in
# /config/anaglyfin/runtime-state, with its binaries in place, is not rebuilt:
#
#   docker compose exec -e ANAGLYFIN_REF=v0.2.0 jellyfin /config/anaglyfin/install-runtime.sh
#
# Versions accepted, and their defaults:
#
#   ANAGLYFIN_REF     Anaglyfin git tag to publish the wrapper from   (default v0.1.0)
#   FFMPEG_MVC_TAG    FFmpeg-mvc git tag to compile                   (default n8.1.2-mvc7-jf4)
#
# Uninstall is `rm -rf /config/anaglyfin` - nothing here is written anywhere else, and
# the SDK and the compile trees this script needs are thrown out of /tmp when it is done.
#
# linux-x64 only: the wrapper publish below targets that RID explicitly, and the encoder
# is compiled here rather than downloaded.

set -eu

ANAGLYFIN_REF="${ANAGLYFIN_REF:-v0.1.0}"
FFMPEG_MVC_TAG="${FFMPEG_MVC_TAG:-n8.1.2-mvc7-jf4}"

# What lands in /config, and therefore survives.
ANAGLYFIN_DIR=/config/anaglyfin
FFMPEG_DIR="$ANAGLYFIN_DIR/ffmpeg"
WRAPPER="$FFMPEG_DIR/anaglyfin-ffmpeg"
ENCODER="$FFMPEG_DIR/ffmpeg-mvc"
PROBE="$FFMPEG_DIR/ffprobe"
STATE="$ANAGLYFIN_DIR/runtime-state"

# What stays in /tmp, and is deleted on success: the .NET SDK, the two source trees, the
# publish output, and the NuGet cache the publish restores into.
DOTNET_DIR=/tmp/anaglyfin-dotnet
BUILD_DIR=/tmp/anaglyfin-build
WRAPPER_OUT=/tmp/anaglyfin-wrapper
NUGET_DIR=/tmp/anaglyfin-nuget
DOTNET_INSTALL_SH=/tmp/dotnet-install.sh

die() {
    echo "anaglyfin-install: $1" >&2
    exit 1
}

# The server's own uid must be able to write the lock slots and the settings document,
# so this tree follows the owner /config already has. The installer itself runs as root.
chown_to_config_owner() {
    # Best effort: an unusual owner on /config is the administrator's business, not a
    # reason to throw away a finished build.
    chown -R "$(stat -c '%u:%g' /config)" "$ANAGLYFIN_DIR" ||
        echo "anaglyfin-install: warning: could not chown $ANAGLYFIN_DIR to the owner of /config" >&2
}

# The value a previous run left in the state file; empty when it recorded none.
recorded() {
    if [ -f "$STATE" ]; then
        sed -n "s/^$1=//p" "$STATE" | tail -n 1
    fi
}

# Records what is installed, so the next run can skip it. Each half is recorded as soon
# as it is in place, because the file carries the value for the other half too.
write_state() {
    {
        echo "ANAGLYFIN_REF_INSTALLED=$installed_ref"
        echo "FFMPEG_MVC_TAG_INSTALLED=$installed_mvc_tag"
    } >"$STATE"
}

# Build tools for both halves, plus the shared libraries the encoder links against at
# run time (libass, libdrm, fontconfig, freetype, fribidi, libnuma, VA-API, VPL, libx264,
# zlib) and the ICU the self-contained wrapper globalises with. Nothing here is moved
# into /config: a container's apt state is not portable, so it is reinstalled instead.
ensure_packages() {
    apt-get update
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        build-essential \
        ca-certificates \
        curl \
        make \
        nasm \
        pkg-config \
        libass-dev \
        libdrm-dev \
        libfontconfig-dev \
        libfreetype-dev \
        libfribidi-dev \
        libnuma-dev \
        libva-dev \
        libvpl-dev \
        libx264-dev \
        zlib1g-dev \
        libicu-dev
}

# The wrapper, published self-contained so that the server needs no .NET runtime. The SDK
# itself is a build tool and stays in /tmp.
build_wrapper() {
    echo "anaglyfin-install: publishing anaglyfin-ffmpeg from Anaglyfin $ANAGLYFIN_REF"

    curl -fsSL "https://dot.net/v1/dotnet-install.sh" -o "$DOTNET_INSTALL_SH"
    bash "$DOTNET_INSTALL_SH" --channel 10.0 --install-dir "$DOTNET_DIR"

    curl -fsSL "https://github.com/tthayer93/Anaglyfin/archive/refs/tags/${ANAGLYFIN_REF}.tar.gz" \
        -o "$BUILD_DIR/anaglyfin.tar.gz"
    mkdir -p "$BUILD_DIR/anaglyfin"
    tar -xzf "$BUILD_DIR/anaglyfin.tar.gz" -C "$BUILD_DIR/anaglyfin" --strip-components=1

    (
        cd "$BUILD_DIR/anaglyfin"
        PATH="$DOTNET_DIR:$PATH"
        export PATH
        DOTNET_CLI_HOME=/tmp
        export DOTNET_CLI_HOME
        NUGET_PACKAGES="$NUGET_DIR"
        export NUGET_PACKAGES
        dotnet publish src/Anaglyfin.FFmpegWrapper/Anaglyfin.FFmpegWrapper.csproj \
            --configuration Release \
            --runtime linux-x64 \
            --self-contained true \
            -p:PublishSingleFile=true \
            --output "$WRAPPER_OUT"
    )

    install -m 0755 "$WRAPPER_OUT/Anaglyfin.FFmpegWrapper" "$WRAPPER"
}

# The encoder. Jellyfin looks for `ffprobe` beside the FFmpeg path it was given, so both
# binaries come out of this one compile and go into $FFMPEG_DIR together.
build_ffmpeg_mvc() {
    echo "anaglyfin-install: compiling ffmpeg-mvc from FFmpeg-mvc $FFMPEG_MVC_TAG"

    curl -fsSL "https://github.com/tthayer93/FFmpeg-mvc/archive/refs/tags/${FFMPEG_MVC_TAG}.tar.gz" \
        -o "$BUILD_DIR/ffmpeg-mvc.tar.gz"
    mkdir -p "$BUILD_DIR/ffmpeg-mvc"
    tar -xzf "$BUILD_DIR/ffmpeg-mvc.tar.gz" -C "$BUILD_DIR/ffmpeg-mvc" --strip-components=1

    (
        cd "$BUILD_DIR/ffmpeg-mvc"
        ./configure \
            --disable-doc \
            --enable-gpl \
            --enable-libx264 \
            --enable-libass \
            --enable-vaapi \
            --enable-libvpl
        make -j"$(nproc)"
    )

    install -m 0755 "$BUILD_DIR/ffmpeg-mvc/ffmpeg"  "$ENCODER"
    install -m 0755 "$BUILD_DIR/ffmpeg-mvc/ffprobe" "$PROBE"
}

# ---------------------------------------------------------------- what this run installs

[ "$(id -u)" -eq 0 ] ||
    die "must run as root: docker compose exec -u root jellyfin /config/anaglyfin/install-runtime.sh"
[ "$(uname -s)" = "Linux" ] || die "supports Linux containers only (this is $(uname -s))"
[ "$(uname -m)" = "x86_64" ] || die "supports linux-x64 only (this is $(uname -m))"
[ -d /config ] || die "no /config: mount the Jellyfin data directory the way docs/install.md describes"

mkdir -p "$FFMPEG_DIR" "$ANAGLYFIN_DIR/lock" "$ANAGLYFIN_DIR/wrapper" "$BUILD_DIR"

ensure_packages

installed_ref="$(recorded ANAGLYFIN_REF_INSTALLED)"
installed_mvc_tag="$(recorded FFMPEG_MVC_TAG_INSTALLED)"

if [ -x "$WRAPPER" ] && [ "$installed_ref" = "$ANAGLYFIN_REF" ]; then
    echo "anaglyfin-install: anaglyfin-ffmpeg $ANAGLYFIN_REF already installed, not rebuilding it"
else
    build_wrapper
    installed_ref="$ANAGLYFIN_REF"
    write_state
fi

if [ -x "$ENCODER" ] && [ -x "$PROBE" ] && [ "$installed_mvc_tag" = "$FFMPEG_MVC_TAG" ]; then
    echo "anaglyfin-install: ffmpeg-mvc $FFMPEG_MVC_TAG already installed, not rebuilding it"
else
    build_ffmpeg_mvc
    installed_mvc_tag="$FFMPEG_MVC_TAG"
    write_state
fi

chmod 0755 "$WRAPPER" "$ENCODER" "$PROBE"
chown_to_config_owner

# Neither half counts as installed until the wrapper answers through the encoder it was
# given - the same version probe Jellyfin runs at start-up, with the logs sent away.
env ANAGLYFIN_REAL_FFMPEG="$ENCODER" "$WRAPPER" -version >/dev/null ||
    die "the installed wrapper cannot run the installed encoder; re-check the apt packages, then $ENCODER"

rm -rf "$BUILD_DIR" "$WRAPPER_OUT" "$DOTNET_DIR" "$NUGET_DIR" "$DOTNET_INSTALL_SH"

echo "anaglyfin-install: ready - point Jellyfin at $WRAPPER and give the wrapper $ENCODER"
