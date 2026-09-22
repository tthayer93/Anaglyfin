# Installing Anaglyfin

Install the plugin from one repository URL, get three FFmpeg runtime files onto the server,
and point Jellyfin's FFmpeg at the wrapper. On Docker the commands in §3 run inside the running
container and build both halves there. Target: Jellyfin **12.0.0** — Debian/Ubuntu package or
`jellyfin/jellyfin:latest`.

## 1. Install the plugin

In **Dashboard → Plugins → Catalogs → Repositories → Add plugin repository**, enter:

| Field | Value |
| --- | --- |
| Name | `Anaglyfin` |
| Url | `https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json` |

Install **Anaglyfin** from the catalog, then **restart Jellyfin**: a plugin is loaded at startup.

That installs the plugin only — no runtime binaries. The server wants those in place before it next
starts, and §2 says where each shape gets them.

**No catalog?** Unpack `Anaglyfin_<version>.zip` from the
[GitHub release](https://github.com/tthayer93/Anaglyfin/releases) into
`/var/lib/jellyfin/plugins/Anaglyfin` or `./jellyfin/config/plugins/Anaglyfin`; on a package
install, run `sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/Anaglyfin`, then restart.

## 2. Runtime files

All three live in **one** directory — `/config/anaglyfin/ffmpeg` on Docker, `/opt/anaglyfin/ffmpeg`
on bare metal — named exactly:

- `anaglyfin-ffmpeg` — the Anaglyfin wrapper: published from the Anaglyfin source by the Docker
  commands below, a [release asset](https://github.com/tthayer93/Anaglyfin/releases) on bare metal.
- `ffmpeg-mvc` — a Jellyfin-compatible FFmpeg-mvc build; target `n8.1.2-mvc7-jf4`.
- `ffprobe` — the `ffprobe` from that **same** build.

Jellyfin finds `ffprobe` **beside the FFmpeg path it was given** — beside the wrapper — never on `PATH`.
The two server shapes differ only in who fills that directory:

- **Docker:** a series of `docker compose exec` commands against the running container installs the
  packages, compiles FFmpeg-mvc, and publishes the wrapper, into `/config/anaglyfin/ffmpeg`. The
  Docker host places nothing.
- **Bare metal:** you install all three yourself (§4).

## 3. Docker server (recommended)

Every command below runs **inside the running container**. Nothing is installed on the Docker host
and nothing extra is mounted for the runtime. Assumes the service is named `jellyfin`, the image is
`jellyfin/jellyfin:latest`, and `./jellyfin/config` is mounted at `/config`. On the official image
`docker compose exec` hands you root, which is what the package install needs. This flow targets
`linux-x64`, because the wrapper publish targets that RID.

Two things get installed, in two different places, and the sequence turns on that:

- the three runtime files, plus the `lock` and `wrapper` directories, go under `/config` — the
  persisted volume, so they outlive the container;
- the apt packages go into the container's own filesystem — so they last until it is recreated,
  which is why step 11 installs them a second time.

### 3.1 Initial install

**1. Dependencies** — the build tools both halves need, the shared libraries the encoder links
against when it runs, and the ICU the wrapper globalises with. This block is run again later, so
keep it.

```sh
docker compose exec jellyfin sh -c '
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
    libicu-dev \
    libnuma-dev \
    libva-dev \
    libvpl-dev \
    libx264-dev \
    zlib1g-dev
'
```

**2. Directories** — one for the three files, two for what the wrapper writes into.

```sh
docker compose exec jellyfin install -d \
  /config/anaglyfin/ffmpeg \
  /config/anaglyfin/lock \
  /config/anaglyfin/wrapper
```

**3. FFmpeg-mvc: fetch and unpack.** `/tmp` is build space inside the container; none of it is
needed once the three files are in place, and none of it survives a recreate.

```sh
docker compose exec jellyfin curl -fsSL \
  https://github.com/tthayer93/FFmpeg-mvc/archive/refs/tags/n8.1.2-mvc7-jf4.tar.gz \
  -o /tmp/ffmpeg-mvc.tar.gz

docker compose exec jellyfin sh -c '
  rm -rf /tmp/ffmpeg-mvc-src
  mkdir -p /tmp/ffmpeg-mvc-src
  tar -xzf /tmp/ffmpeg-mvc.tar.gz -C /tmp/ffmpeg-mvc-src --strip-components=1
'
```

**4. FFmpeg-mvc: compile and install.** Both binaries come out of this one compile, and both go
into the directory the wrapper lives in — that is where Jellyfin looks for `ffprobe`. The compile
is the slow part of this sequence.

```sh
docker compose exec jellyfin sh -c '
  cd /tmp/ffmpeg-mvc-src
  ./configure \
    --disable-doc \
    --enable-gpl \
    --enable-libx264 \
    --enable-libass \
    --enable-vaapi \
    --enable-libvpl
'

docker compose exec jellyfin sh -c '
  cd /tmp/ffmpeg-mvc-src
  make -j"$(nproc)"
'

docker compose exec jellyfin sh -c '
  install -m 0755 /tmp/ffmpeg-mvc-src/ffmpeg  /config/anaglyfin/ffmpeg/ffmpeg-mvc
  install -m 0755 /tmp/ffmpeg-mvc-src/ffprobe /config/anaglyfin/ffmpeg/ffprobe
'
```

**5. The .NET SDK**, into `/tmp` for the same reason. The wrapper publishes self-contained, so the
server itself never needs a .NET runtime.

```sh
docker compose exec jellyfin curl -fsSL https://dot.net/v1/dotnet-install.sh \
  -o /tmp/dotnet-install.sh

docker compose exec jellyfin bash /tmp/dotnet-install.sh \
  --channel 10.0 \
  --install-dir /tmp/dotnet
```

**6. The wrapper: fetch and unpack.** `v0.1.0` is a tag, not a branch — the source arrives as a tag
archive.

```sh
docker compose exec jellyfin curl -fsSL \
  https://github.com/tthayer93/Anaglyfin/archive/refs/tags/v0.1.0.tar.gz \
  -o /tmp/anaglyfin.tar.gz

docker compose exec jellyfin sh -c '
  rm -rf /tmp/anaglyfin-src
  mkdir -p /tmp/anaglyfin-src
  tar -xzf /tmp/anaglyfin.tar.gz -C /tmp/anaglyfin-src --strip-components=1
'
```

**7. The wrapper: publish and install.** The SDK on `PATH` and the NuGet cache both live inside this
one `sh -c`, because neither survives into a separate `exec`.

```sh
docker compose exec jellyfin sh -c '
  cd /tmp/anaglyfin-src
  export PATH=/tmp/dotnet:"$PATH"
  export DOTNET_CLI_HOME=/tmp
  export NUGET_PACKAGES=/tmp/anaglyfin-nuget
  dotnet publish src/Anaglyfin.FFmpegWrapper/Anaglyfin.FFmpegWrapper.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    --output /tmp/anaglyfin-wrapper
'

docker compose exec jellyfin install -m 0755 \
  /tmp/anaglyfin-wrapper/Anaglyfin.FFmpegWrapper \
  /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
```

**8. Ownership.** Everything above ran as root and left a root-owned tree, but the server writes the
concurrency slots and the settings document into it — and so does whoever owns the rest of `/config`
from the host side. Hand the tree to the owner of `/config`: on the official image that is root and
this is a no-op, and on a `./jellyfin/config` owned by a host user it is what lets the server write
its locks.

```sh
docker compose exec jellyfin sh -c \
  'chown -R "$(stat -c "%u:%g" /config)" /config/anaglyfin'
```

**9. Environment** — add to the service:

```yaml
environment:
  JELLYFIN_FFMPEG: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ANAGLYFIN_REAL_FFMPEG: /config/anaglyfin/ffmpeg/ffmpeg-mvc
  ANAGLYFIN_LOCK_DIR: /config/anaglyfin/lock
  ANAGLYFIN_WRAPPER_SETTINGS: /config/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```

**10. Recreate** the service so it starts with that environment:

```sh
docker compose up -d --force-recreate jellyfin
```

**11. The dependencies again — only that command.** The recreate assembled a new container out of
the stock image, so the packages from step 1 went out with the old one, while everything steps 2–8
put under `/config` is exactly where it was left. Without them the encoder cannot start: a stock
container carries no `libass`, `libva`, `libva-drm` or `libvpl`, and `ffmpeg-mvc` is linked against
those — as well as libdrm, fontconfig, freetype, fribidi and libx264, which the stock image does
ship. The wrapper is published self-contained and the image already carries the ICU it globalises
with, so this block is for the encoder, not for the wrapper.

```sh
docker compose exec jellyfin sh -c '
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
    libicu-dev \
    libnuma-dev \
    libva-dev \
    libvpl-dev \
    libx264-dev \
    zlib1g-dev
'
```

Nothing under `/config` needs rebuilding, and neither the encoder nor the wrapper moves. A
recreate is `up -d --force-recreate`, an image pull, or `down` and `up`; a plain
`docker compose restart` is **not** one — it keeps this container and its filesystem, so the
packages installed here are still in place after it and nothing has to be reinstalled.

**12. Restart Jellyfin:**

```sh
docker compose restart jellyfin
```

which is also what loads the plugin from §1, if it was waiting to install or update.

### 3.2 Upgrade

The same commands, against the running container, one component at a time. Both install over the
top of the same three filenames, so nothing has to be uninstalled or removed first.

- **The wrapper** — repeat steps 5–7 with the new tag in the source URL (step 5 is only needed if
  `/tmp/dotnet` is gone, which a recreate does to it).
- **FFmpeg-mvc** — repeat steps 3–4 with the new tag in the source URL; `ffmpeg-mvc` and `ffprobe`
  come out of one compile and are replaced together.
- Either way, **if the container has been recreated since the build you are replacing**, run the
  dependency block (step 1) first — the new container does not have the packages.

Then:

```sh
docker compose restart jellyfin
```

### 3.3 Uninstall

```sh
docker compose exec jellyfin rm -rf /config/anaglyfin
```

Remove the four environment variables from the compose file and recreate with
`jellyfin/jellyfin:latest`:

```sh
docker compose up -d --force-recreate jellyfin
```

That is the stock container again: its own FFmpeg, the runtime files gone with `/config/anaglyfin`,
and the apt packages this guide added discarded with the container they were installed into.
Take the plugin out of Dashboard → Plugins too if it should go with it.

## 4. Bare-metal server

Same grouping under `/opt/anaglyfin/ffmpeg`; `lock` and `wrapper` under the server data directory,
everything owned by the `jellyfin` user:

```sh
sudo install -d -m 0755 /opt/anaglyfin/ffmpeg
sudo install -m 0755 -o jellyfin -g jellyfin ./anaglyfin-ffmpeg  /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
sudo install -m 0755 -o jellyfin -g jellyfin /path/to/ffmpeg-mvc /opt/anaglyfin/ffmpeg/ffmpeg-mvc
sudo install -m 0755 -o jellyfin -g jellyfin /path/to/ffprobe    /opt/anaglyfin/ffmpeg/ffprobe
sudo install -d -m 0755 -o jellyfin -g jellyfin /var/lib/jellyfin/anaglyfin/lock /var/lib/jellyfin/anaglyfin/wrapper
```

Pass those four variables in a systemd drop-in. Create the drop-in directory first:

```sh
sudo install -d /etc/systemd/system/jellyfin.service.d
```

```ini
# /etc/systemd/system/jellyfin.service.d/anaglyfin.conf
[Service]
Environment=JELLYFIN_FFMPEG=/opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
Environment=ANAGLYFIN_REAL_FFMPEG=/opt/anaglyfin/ffmpeg/ffmpeg-mvc
Environment=ANAGLYFIN_LOCK_DIR=/var/lib/jellyfin/anaglyfin/lock
Environment=ANAGLYFIN_WRAPPER_SETTINGS=/var/lib/jellyfin/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```

Apply it and restart:

```sh
sudo systemctl daemon-reload
sudo systemctl restart jellyfin
```

## 5. Verify and update

- `Dashboard → Plugins` lists **Anaglyfin** and its settings page opens.
- The server log names the wrapper as Jellyfin's FFmpeg, at the path the shape installed it to.
  Docker:

  ```text
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: Found ffmpeg version 8.1.2
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ```

  Bare metal:

  ```text
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: Found ffmpeg version 8.1.2
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ```

- The wrapper answers `-version` straight through to the real encoder. Docker:

  ```sh
  docker compose exec jellyfin \
    env ANAGLYFIN_REAL_FFMPEG=/config/anaglyfin/ffmpeg/ffmpeg-mvc \
    /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version
  ```

  Bare metal:

  ```sh
  sudo -u jellyfin env ANAGLYFIN_REAL_FFMPEG=/opt/anaglyfin/ffmpeg/ffmpeg-mvc \
    /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version
  ```

A server whose `ANAGLYFIN_REAL_FFMPEG` names a missing file does not start; full checks: `docs/validation.md`.

Update the plugin from the catalog and restart Jellyfin. The three runtime files are the server's
own, and the plugin tracks none of them:

- **Docker:** repeat the commands for the half you are moving (§3.2) and restart.
- **Bare metal:** swap the file under `/opt/anaglyfin/ffmpeg/` by hand and restart.
