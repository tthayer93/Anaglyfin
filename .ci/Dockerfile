# Anaglyfin CI toolchain image.
#
# Build context is the repository root (see .ci/test.yml), so nothing in this
# file may reference a host path. Sources are never COPYed: the CI job mounts
# the repository into /src at run time, which keeps one image usable for every
# worktree/branch.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# Run as a non-root user whose UID/GID matches the host workspace owner
# (1000:1000 in this environment) so that bind-mounted sources stay writable
# and build output (obj/, bin/) is not root-owned on the host. The base image may
# already own those ids, so creation is conditional and the numeric ids are what
# actually matters.
ARG BUILD_UID=1000
ARG BUILD_GID=1000
RUN set -eux; \
    if ! getent group "${BUILD_GID}" > /dev/null; then groupadd --gid "${BUILD_GID}" anaglyfin-ci; fi; \
    if ! getent passwd "${BUILD_UID}" > /dev/null; then \
        useradd --uid "${BUILD_UID}" --gid "${BUILD_GID}" --no-create-home --shell /bin/sh anaglyfin-ci; \
    fi

# /tmp is writable by any UID, so NuGet restore and MSBuild node state always
# have a usable home even if the job is forced onto a different UID.
ENV HOME=/tmp \
    DOTNET_CLI_HOME=/tmp \
    NUGET_PACKAGES=/tmp/nuget-packages \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

WORKDIR /src

USER ${BUILD_UID}:${BUILD_GID}
