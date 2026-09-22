# Installing Anaglyfin

Install the plugin from one repository URL, get three FFmpeg runtime files onto the server,
and point Jellyfin's FFmpeg at the wrapper. On Docker one script run inside the running
container does the middle step. Target: Jellyfin **12.0.0** — Debian/Ubuntu package or
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

- `anaglyfin-ffmpeg` — the Anaglyfin wrapper: compiled from the Anaglyfin source by
  `docs/docker-install-runtime.sh` on Docker, a [release asset](https://github.com/tthayer93/Anaglyfin/releases)
  on bare metal.
- `ffmpeg-mvc` — a Jellyfin-compatible FFmpeg-mvc build; target `n8.1.2-mvc7-jf4`.
- `ffprobe` — the `ffprobe` from that **same** build.

Jellyfin finds `ffprobe` **beside the FFmpeg path it was given** — beside the wrapper — never on `PATH`.
The two server shapes differ only in who fills that directory:

- **Docker:** `docs/docker-install-runtime.sh`, run inside the container with `docker compose exec`,
  publishes the wrapper and compiles FFmpeg-mvc into `/config/anaglyfin/ffmpeg`. The Docker host
  places nothing.
- **Bare metal:** you install all three yourself (§4).

## 3. Docker server (recommended)

Nothing is installed on the Docker host and nothing extra is mounted for the runtime: the
installer writes into the `/config` volume the official image already uses. It needs the container
to be **running**, and it needs to be **root**, which is what `docker compose exec` hands you on
the official image. This flow targets `linux-x64`, because the wrapper publish targets that RID.

### 3.1 Initial install

With Jellyfin already running and `./jellyfin/config` mounted at `/config`, fetch the installer
into the volume and run it there:

```sh
docker compose exec jellyfin sh -eux -c '
  mkdir -p /config/anaglyfin
  curl -fsSL https://raw.githubusercontent.com/tthayer93/Anaglyfin/v0.1.0/docs/docker-install-runtime.sh \
    -o /config/anaglyfin/install-runtime.sh
  chmod 0755 /config/anaglyfin/install-runtime.sh
  /config/anaglyfin/install-runtime.sh
'
```

That one run installs the apt packages both builds need, publishes `anaglyfin-ffmpeg` from the
Anaglyfin source, compiles FFmpeg-mvc, and lays the three files down in
`/config/anaglyfin/ffmpeg`, with `lock` and `wrapper` beside them. Both halves are compiled, not
downloaded, so the first pass takes a while. The versions it installs are `ANAGLYFIN_REF=v0.1.0`
and `FFMPEG_MVC_TAG=n8.1.2-mvc7-jf4`; §3.2 is how either one changes. The script keeps itself at
`/config/anaglyfin/install-runtime.sh`, which is why the fetch above is the last time anyone
downloads it.

Then add the environment fragment:

```yaml
environment:
  JELLYFIN_FFMPEG: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ANAGLYFIN_REAL_FFMPEG: /config/anaglyfin/ffmpeg/ffmpeg-mvc
  ANAGLYFIN_LOCK_DIR: /config/anaglyfin/lock
  ANAGLYFIN_WRAPPER_SETTINGS: /config/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```

Recreate:

```sh
docker compose up -d --force-recreate jellyfin
```

Then rerun the persisted installer to restore the apt/runtime dependencies in the new container:

```sh
docker compose exec jellyfin /config/anaglyfin/install-runtime.sh
```

Then restart Jellyfin:

```sh
docker compose restart jellyfin
```

That restart is also what loads the plugin from §1, if it was waiting to install or update.

**Why the rerun.** `/config` is a volume, so the three files, the `lock` and `wrapper`
directories, and the version record are all still exactly where the first run left them. The
apt packages that run installed are not: they belong to the container it ran in, and the
recreate above just replaced that container. The rerun puts them back and skips both rebuilds,
because `/config/anaglyfin/runtime-state` still names the versions already on disk — seconds,
not minutes.

**What is durable and what is not.** Everything the runtime needs lives under `/config` and
outlives the container; everything apt installed is durable **within that container only**. A
`docker compose restart` keeps the same container and its filesystem, so those packages are
still there afterwards and nothing has to be reinstalled. A recreate — `up -d --force-recreate`,
an image pull, `down` and `up` — does not keep them, and that is when the rerun is required.
Neither the installer nor this guide moves apt libraries into `/config`: a container's libraries
are not portable, so they are reinstalled instead.

### 3.2 Upgrade

Both halves are versioned by the same script, so an upgrade is that script again against the
running container. Omit any variable you are not changing (`v0.2.0` is an illustrative future
version):

```sh
docker compose exec \
  -e ANAGLYFIN_REF=v0.2.0 \
  -e FFMPEG_MVC_TAG=n8.1.2-mvc7-jf4 \
  jellyfin /config/anaglyfin/install-runtime.sh

docker compose restart jellyfin
```

Only the half whose requested version differs from `/config/anaglyfin/runtime-state` is
rebuilt; the other is left where it is. `ANAGLYFIN_REF` and `FFMPEG_MVC_TAG` are tags
(`v0.2.0`, not `main`) — the sources arrive as tag archives, so a branch name has no archive
to fetch.

### 3.3 Uninstall

```sh
docker compose exec jellyfin rm -rf /config/anaglyfin
```

Remove the environment variables from the compose file and recreate with
`jellyfin/jellyfin:latest`:

```sh
docker compose up -d --force-recreate jellyfin
```

That restores the stock container: its own FFmpeg, none of the apt packages the installer
added, and no Anaglyfin runtime left behind. Take the plugin out of Dashboard → Plugins too if
it should go with it.

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

- **Docker:** run the installer again with the version you want (§3.2) and restart.
- **Bare metal:** swap the file under `/opt/anaglyfin/ffmpeg/` by hand and restart.
