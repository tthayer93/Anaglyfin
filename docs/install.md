# Installing Anaglyfin

> **Scope:** this page installs Anaglyfin — the plugin plus its three FFmpeg runtime files — on a server where
> **Jellyfin 12 is already installed and running**. It is not a guide to installing Windows, Debian/Ubuntu, Docker, or
> Jellyfin itself; do those through their own documentation and come back with a server that runs.

Every command below does one of two things: puts a file where the server can read it, or names that file to the service.
Nothing here installs Jellyfin, replaces the FFmpeg it ships, or touches its database.

## 1. Scope and prerequisites

| Item | Current value |
| --- | --- |
| Anaglyfin | `v0.2.2`. Plugin and wrapper are published under one tag: they are a pair, and an update moves them together |
| Jellyfin | 12.x; the targeted container image is `jellyfin/jellyfin:12.4`. The manifest's `targetAbi 12.0.0` is a minimum-version floor rather than an exact match, so `v0.2.2` loads on 12.0 or later. Jellyfin 12.4 moved its own .NET baseline to `10.0.14`; Anaglyfin targets `net10.0` inside that band, so nothing is retargeted |
| FFmpeg-mvc | product tag `n8.1.3-mvc10-jf1` on the `jellyfin-8.1` branch, built to drop in against the parent `jellyfin-ffmpeg v8.1.3-1`. **FFmpeg-mvc publishes no binaries for any platform**, so every `ffmpeg-mvc` below is one you build with the commands given here |
| Wrapper release asset | `anaglyfin-ffmpeg`, a self-contained **linux-x64** binary, published with `SHA256SUMS.txt` in the same release. There is **no** `win-x64` asset |

**Before you start.** Jellyfin 12 running as one of the three shapes in §3 and reachable in a browser; permission to stop and start
that service, or recreate that container, **once** at the end of your platform section — that restart loads the plugin; and, on the
two bare-metal shapes, the build tools that section lists (only Windows needs a .NET SDK, because only there is a wrapper compiled).

**Runtime evidence.** Every result in `docs/validation.md` was observed on Linux — the official container and the Debian/Ubuntu
package. The Windows route in §6 is written from published installer and service behaviour and has never been executed by this
project, so its verify step is evidence *you* collect. The container and package shapes have recorded Linux validation; the
release-asset wrapper download itself is not separately runtime-validated.

### 1.1 The three runtime files

All three live in **one** directory and carry **exactly** these names (on Windows, with `.exe` on the end):

| File | What it is | Where you get it |
| --- | --- | --- |
| `anaglyfin-ffmpeg` | the wrapper — the file Jellyfin's FFmpeg path points at | Docker and Linux: the linux-x64 release asset. Windows: published from the same tag |
| `ffmpeg-mvc` | the FFmpeg-mvc build the wrapper hands Anaglyfin commands to | built from tag `n8.1.3-mvc10-jf1` on every platform |
| `ffprobe` | the probe the server runs beside the wrapper | the `ffprobe` of the **official** Jellyfin FFmpeg already on the server — **never** the one the FFmpeg-mvc build produces |

| Shape | Runtime files | `lock` and `wrapper` directories |
| --- | --- | --- |
| Docker | `/config/anaglyfin/ffmpeg` | `/config/anaglyfin/lock`, `/config/anaglyfin/wrapper` |
| Debian/Ubuntu package | `/opt/anaglyfin/ffmpeg` | `/var/lib/jellyfin/anaglyfin/lock`, `/var/lib/jellyfin/anaglyfin/wrapper` |
| Windows service | `C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg` | beside it: `…\anaglyfin\lock`, `…\anaglyfin\wrapper` |

None of those names is a preference:

1. **The sidecar `ffprobe` is the official one.** The server probes media through it during ordinary playback, so it must match the
   stock FFmpeg. The FFmpeg-mvc build does write its own `ffprobe`; installing that beside the wrapper is wrong on every platform.
2. **The sidecar name and directory are derived, not configured.** Jellyfin replaces the **whole final filename** of the FFmpeg path
   it was given with `ffprobe`, keeps the extension, and looks in that directory alone — never on `PATH`. So
   `…\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe` is probed with `…\anaglyfin\ffmpeg\ffprobe.exe`, never `anaglyfin-ffprobe.exe`.
3. **No quotes around a path.** The server hands the path to the operating system rather than to a shell, so a space is fine and a
   literal `"` is part of the name — a quoted path is a file that cannot be found. The wrapper trims its own variables, never
   de-quotes them.

## 2. Add the plugin — do not restart yet

In **Dashboard → Plugins → Catalogs → Repositories → Add plugin repository**, enter:

| Field | Value |
| --- | --- |
| Name | `Anaglyfin` |
| Url | `https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json` |

Install **Anaglyfin** from the catalog, then **leave the server alone**: a plugin is loaded when the server starts, and the
last step of your platform section performs exactly one restart — that one loads it. Restarting now only buys a second one.

**If that repository cannot be added** — no route to `raw.githubusercontent.com`, say — unpack `Anaglyfin_<version>.zip` from the
[GitHub release](https://github.com/tthayer93/Anaglyfin/releases) into the server's own plugin directory: `/config/plugins/Anaglyfin`
for a container mounting `./jellyfin/config` at `/config`; `/var/lib/jellyfin/plugins/Anaglyfin` on the package, then
`sudo chown -R jellyfin:jellyfin` on it; `C:\ProgramData\Jellyfin\Server\plugins\Anaglyfin` on the Windows service, which is the
service's own data directory. The catalog installs this same archive, so the plugin is identical either way — and it never bundles
or installs the runtime files; the platform section you now follow does.

## 3. Choose one platform and follow that section

| Your server, already installed and running | The service | The five variables go in | Follow |
| --- | --- | --- | --- |
| Official container `jellyfin/jellyfin:12.4` | the container, recreated by compose | compose `environment:` | §4 Docker |
| Debian/Ubuntu package | `jellyfin`, a systemd unit | a systemd drop-in of `Environment=` lines | §5 Linux package |
| Official Windows installer | `JellyfinServer`, hosted by the bundled NSSM | the service's `AppEnvironmentExtra` value | §6 Windows |

Pick **one** and follow it end to end; none of them installs Jellyfin. The five variables are the same five everywhere —
`JELLYFIN_FFMPEG`, `ANAGLYFIN_REAL_FFMPEG`, `ANAGLYFIN_SERVER_FFMPEG`, `ANAGLYFIN_LOCK_DIR`, `ANAGLYFIN_WRAPPER_SETTINGS` — and only
who hands them to the service differs. **Every platform section uses one lifecycle order;** do not read an update step into an initial
install, or the reverse.

- **Initial install:** prerequisites → directories → wrapper → FFmpeg-mvc → official `ffprobe` → ownership → the five variables →
  **one** restart or recreate, which also loads the plugin → verify.
- **Update** — plugin and wrapper ship under one tag, so move them as a pair: (1) take the plugin update from the catalog, which waits
  for the restart in step 4; (2) stop the service or container; (3) replace the runtime file you are updating — wrapper, `ffmpeg-mvc`,
  or both; (4) start, recreate, or `daemon-reload` + start, **once**, which loads the plugin update too; (5) verify.
- **Uninstall:** (1) take the plugin out of **Dashboard → Plugins** first — either order survives, this one is clearer; (2) stop the
  service or container; (3) remove **every** Anaglyfin environment entry, from every place you added one; (4) delete the Anaglyfin
  runtime directory and the `lock` / `wrapper` state; (5) start or recreate, which puts the server back on its own FFmpeg; (6) clear or
  correct any path saved in **Dashboard → Playback → FFmpeg path**, which is used in preference to the FFmpeg in the server's own
  program directory. Uninstalling Anaglyfin **does not uninstall Jellyfin** and modifies no stock Jellyfin FFmpeg file.

## 4. Docker server

Targets **Linux x86-64** hosts, a service named `jellyfin`, the image `jellyfin/jellyfin:12.4`, and `./jellyfin/config` mounted at
`/config`. Every command execs as root with `-u root` rather than trusting whichever user the image runs its own process as. Two places
are involved: the runtime files and the `lock` / `wrapper` directories go under `/config`, the persisted volume, so they outlive the
container, while the apt packages are **build-time only** and live in the container's own filesystem, so a recreate discards them and
nothing at run time needs them — the libraries the binaries load come from the image itself, mostly the
`/usr/lib/jellyfin-ffmpeg/lib` set its own FFmpeg uses, which the two `-rpath` lines below point a compiled binary at.

### 4.1 Initial install

**1. Build dependencies**, inside the running container: the compiler, `curl`, `make`, and the headers the FFmpeg compile reads. Run it
again only when a rebuild has to happen in a container recreated since the build it replaces. The ICU development package this page used to
install is gone — it served an in-container wrapper publish, and the wrapper is downloaded now.
```sh
docker compose exec -u root jellyfin sh -c '
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    build-essential ca-certificates curl make nasm pkg-config \
    libass-dev libdrm-dev libfontconfig-dev libfreetype-dev libfribidi-dev \
    libnuma-dev libva-dev libvpl-dev libx264-dev zlib1g-dev
'
```

**2. Directories** — one for the three files, two for what the wrapper writes into while it runs.
```sh
docker compose exec -u root jellyfin install -d \
  /config/anaglyfin/ffmpeg /config/anaglyfin/lock /config/anaglyfin/wrapper
```
**3. The wrapper**, from the released linux-x64 asset, verified before it is installed:
```sh
docker compose exec -u root jellyfin sh -c '
  curl -fsSL https://github.com/tthayer93/Anaglyfin/releases/download/v0.2.2/anaglyfin-ffmpeg -o /tmp/anaglyfin-ffmpeg
  curl -fsSL https://github.com/tthayer93/Anaglyfin/releases/download/v0.2.2/SHA256SUMS.txt -o /tmp/SHA256SUMS.txt
  cd /tmp && grep " anaglyfin-ffmpeg$" SHA256SUMS.txt | sha256sum -c -
  install -m 0755 /tmp/anaglyfin-ffmpeg /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
'
```
The same `SHA256SUMS.txt` names both release assets, so check the wrapper line rather than the whole file unless you also downloaded the
plugin archive.

**4. FFmpeg-mvc: fetch the tag and compile it** in `/tmp`, build space nothing needs later. The last two `configure` lines are what makes
the result outlive the container that built it: they bake `/usr/lib/jellyfin-ffmpeg/lib` — where the official image keeps its FFmpeg runtime
libraries and the VA-API drivers — into the binary as its RUNPATH, so it loads them from there instead of from anything apt put in the
container. The compile is the slow part.
```sh
docker compose exec -u root jellyfin sh -c '
  set -e
  curl -fsSL https://github.com/tthayer93/FFmpeg-mvc/archive/refs/tags/n8.1.3-mvc10-jf1.tar.gz \
    -o /tmp/ffmpeg-mvc.tar.gz
  rm -rf /tmp/ffmpeg-mvc-src
  mkdir -p /tmp/ffmpeg-mvc-src
  tar -xzf /tmp/ffmpeg-mvc.tar.gz -C /tmp/ffmpeg-mvc-src --strip-components=1
  cd /tmp/ffmpeg-mvc-src
  ./configure \
    --disable-doc --enable-gpl --enable-libx264 --enable-libass --enable-vaapi --enable-libvpl \
    --extra-ldflags=-Wl,-rpath,/usr/lib/jellyfin-ffmpeg/lib \
    --extra-ldexeflags=-Wl,-rpath,/usr/lib/jellyfin-ffmpeg/lib
  make -j"$(nproc)"
'
```
**5. Install the two FFmpeg files, and hand the tree to the server.** Only the `ffmpeg` half of that build is installed, as `ffmpeg-mvc`;
the build's own `ffprobe` is deliberately left unused and the `ffprobe` this directory needs is the image's **official** one. The `chown`
matters when `./jellyfin/config` belongs to a host user, because the server writes its concurrency slots and settings document into that
tree; on the official image, where that owner is root, it is a no-op.
```sh
docker compose exec -u root jellyfin sh -c '
  install -m 0755 /tmp/ffmpeg-mvc-src/ffmpeg /config/anaglyfin/ffmpeg/ffmpeg-mvc
  install -m 0755 /usr/lib/jellyfin-ffmpeg/ffprobe /config/anaglyfin/ffmpeg/ffprobe
  chown -R "$(stat -c "%u:%g" /config)" /config/anaglyfin
'
```
**6. Environment** — add to the service:
```yaml
environment:
  JELLYFIN_FFMPEG: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ANAGLYFIN_REAL_FFMPEG: /config/anaglyfin/ffmpeg/ffmpeg-mvc
  ANAGLYFIN_SERVER_FFMPEG: /usr/lib/jellyfin-ffmpeg/ffmpeg
  ANAGLYFIN_LOCK_DIR: /config/anaglyfin/lock
  ANAGLYFIN_WRAPPER_SETTINGS: /config/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```
The two FFmpeg variables split the work: `ANAGLYFIN_REAL_FFMPEG` receives every Anaglyfin marker/profile command, and
`ANAGLYFIN_SERVER_FFMPEG` — the stock FFmpeg the image ships, the one the server would have run — receives every ordinary command and
Jellyfin's startup capability probes, so a codec or tone-mapping tool the server read off its own FFmpeg is genuinely there for the commands
built around it. Unset or blank keeps the older single-binary behaviour, where every command goes to `ANAGLYFIN_REAL_FFMPEG`. Nothing is
quoted: these strings reach the process as written.

**7. Optional — Intel GPU.** The image ships the `iHD`/`i965` VA-API drivers and the `libva`/`libvpl` runtime, but Docker
hands the container the host GPU only if you map it:
```yaml
devices:
  - /dev/dri
```
Without the mapping the install still works and so does software encoding; `h264_vaapi` and `h264_qsv` hardware encoding does
not.

**8. Recreate the service once**, so it starts with that environment — and so the plugin from §2 loads:
```sh
docker compose up -d --force-recreate jellyfin
```
Nothing from steps 1–5 repeats afterwards: the recreate throws away packages that only ever fed the compiler, while what the server runs is
the three files under `/config`, left where they were, on the FFmpeg libraries that come back with the image. A recreate is
`up -d --force-recreate`, an image pull, or `down` and `up`; a plain `docker compose restart` is not one and needs nothing. The one thing
that can move underneath this is a new image carrying different FFmpeg libraries: if
`docker compose exec -u root jellyfin ldd /config/anaglyfin/ffmpeg/ffmpeg-mvc` reports `not found`, redo steps 4–5.

### 4.2 Update

Take the plugin update from the catalog first (§2), then move the runtime file you are updating. Both install over the same filename, so
nothing is removed first — but a running binary cannot be replaced, so every swap happens with the container stopped.

**The wrapper** — download the new tag's asset on the host, verify it, copy it in while stopped:
```sh
curl -fsSL https://github.com/tthayer93/Anaglyfin/releases/download/<new-tag>/anaglyfin-ffmpeg -o /tmp/anaglyfin-ffmpeg
curl -fsSL https://github.com/tthayer93/Anaglyfin/releases/download/<new-tag>/SHA256SUMS.txt -o /tmp/SHA256SUMS.txt
( cd /tmp && grep ' anaglyfin-ffmpeg$' SHA256SUMS.txt | sha256sum -c - )
chmod 0755 /tmp/anaglyfin-ffmpeg
docker compose stop jellyfin
docker compose cp /tmp/anaglyfin-ffmpeg jellyfin:/config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
docker compose start jellyfin
```
`docker compose cp` works against the stopped container and preserves the mode; if your compose is too old for it, the same
directory is on the host at `./jellyfin/config/anaglyfin/ffmpeg/`.

**FFmpeg-mvc** — repeat step 4 with the new tag in the download URL (the source arrives as a tarball, so moving to a new tag means a new
tarball, not a `git` command in `/tmp`), and run step 1's dependency block first if the container has been recreated since the last build —
the only case in which that block ever runs twice. Keep both `-rpath` lines, and keep the sidecar the image's official `ffprobe`. Then
install with the container stopped:
```sh
docker compose stop jellyfin
docker compose cp jellyfin:/tmp/ffmpeg-mvc-src/ffmpeg /tmp/ffmpeg-mvc-new
docker compose cp /tmp/ffmpeg-mvc-new jellyfin:/config/anaglyfin/ffmpeg/ffmpeg-mvc
docker compose start jellyfin
```
**Ownership is not part of a routine update:** it overwrites executable files at mode `0755` and leaves the `lock` and `wrapper` directories
alone. Re-run step 5's `chown` only for a reason you can point at — the UID/GID arrangement changed, something outside this flow recreated
those directories root-owned, or the log reports permission errors writing the slots or the settings document.

### 4.3 Uninstall

1. Take **Anaglyfin** out of **Dashboard → Plugins**, then `docker compose stop jellyfin`.
2. Take **every** Anaglyfin entry out of the compose file — all five of step 6 plus any optional one added beside them, such
   as `ANAGLYFIN_SLOT_WAIT_MS` or the `ANAGLYFIN_OFFICIAL_FFMPEG` alias. `JELLYFIN_FFMPEG` is the one that bites: left naming
   a wrapper that no longer exists, the server looks for its FFmpeg at a path that is gone.
3. Delete the runtime and the state beside it, from the host directory compose mounts at `/config`, and recreate with
   `jellyfin/jellyfin:12.4` — the stock container again, its own FFmpeg, its build packages discarded with the container that
   held them:

   ```sh
   sudo rm -rf ./jellyfin/config/anaglyfin
   docker compose up -d --force-recreate jellyfin
   ```

4. Clear any path saved in **Dashboard → Playback → FFmpeg path**.

This does not uninstall Jellyfin and leaves the image's own `/usr/lib/jellyfin-ffmpeg` files alone.

## 5. Linux package

Assumes the Debian/Ubuntu `jellyfin` package is installed and its service running: this section hands Anaglyfin's runtime
files to that service, it does not install the package. Run as a user with `sudo`.

### 5.1 Initial install

**1. Confirm the service and its stock FFmpeg pair** — that pair supplies both the `ANAGLYFIN_SERVER_FFMPEG` binary and the
sidecar `ffprobe`:
```sh
systemctl status jellyfin --no-pager
ls -l /usr/lib/jellyfin-ffmpeg/ffmpeg /usr/lib/jellyfin-ffmpeg/ffprobe
```
**2. Directories**, owned by the service account from the start:
```sh
sudo install -d -m 0755 /opt/anaglyfin/ffmpeg
sudo install -d -m 0755 -o jellyfin -g jellyfin \
  /var/lib/jellyfin/anaglyfin/lock /var/lib/jellyfin/anaglyfin/wrapper
```
**3. The wrapper**, from the released linux-x64 asset, verified before it is installed. `SHA256SUMS.txt` names both release
assets, so check the wrapper line rather than the file unless you also downloaded the plugin archive:
```sh
curl -fsSL https://github.com/tthayer93/Anaglyfin/releases/download/v0.2.2/anaglyfin-ffmpeg -o /tmp/anaglyfin-ffmpeg
curl -fsSL https://github.com/tthayer93/Anaglyfin/releases/download/v0.2.2/SHA256SUMS.txt -o /tmp/SHA256SUMS.txt
( cd /tmp && grep ' anaglyfin-ffmpeg$' SHA256SUMS.txt | sha256sum -c - )
sudo install -m 0755 -o jellyfin -g jellyfin /tmp/anaglyfin-ffmpeg /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
```

**4. Build dependencies** for FFmpeg-mvc. No .NET SDK and no ICU development package appears here: the wrapper above was
downloaded, not compiled.
```sh
sudo apt-get update
sudo apt-get install -y --no-install-recommends \
  build-essential ca-certificates curl make nasm pkg-config \
  libass-dev libdrm-dev libfontconfig-dev libfreetype-dev libfribidi-dev \
  libnuma-dev libva-dev libvpl-dev libx264-dev zlib1g-dev
```

**5. Build FFmpeg-mvc** in `/tmp`, from the tag the Docker section compiles. The two `-rpath` lines point the result at the FFmpeg runtime
libraries the package installs beside the stock FFmpeg, which is what lets it run on a host carrying no build packages.
```sh
curl -fsSL https://github.com/tthayer93/FFmpeg-mvc/archive/refs/tags/n8.1.3-mvc10-jf1.tar.gz -o /tmp/ffmpeg-mvc.tar.gz
rm -rf /tmp/ffmpeg-mvc-src
mkdir -p /tmp/ffmpeg-mvc-src
tar -xzf /tmp/ffmpeg-mvc.tar.gz -C /tmp/ffmpeg-mvc-src --strip-components=1
cd /tmp/ffmpeg-mvc-src
./configure \
  --disable-doc --enable-gpl --enable-libx264 --enable-libass --enable-vaapi --enable-libvpl \
  --extra-ldflags=-Wl,-rpath,/usr/lib/jellyfin-ffmpeg/lib \
  --extra-ldexeflags=-Wl,-rpath,/usr/lib/jellyfin-ffmpeg/lib
make -j"$(nproc)"
```

**6. Install `ffmpeg-mvc`, and the official `ffprobe` beside it** — the stock one, never the `ffprobe` that build also wrote:
```sh
sudo install -m 0755 -o jellyfin -g jellyfin /tmp/ffmpeg-mvc-src/ffmpeg /opt/anaglyfin/ffmpeg/ffmpeg-mvc
sudo install -m 0755 -o jellyfin -g jellyfin /usr/lib/jellyfin-ffmpeg/ffprobe /opt/anaglyfin/ffmpeg/ffprobe
```

**7. The five variables**, in a systemd drop-in; create the drop-in directory first with
`sudo install -d /etc/systemd/system/jellyfin.service.d`. The **service** reads these — your shell's environment reaches a running service
nowhere — and nothing is quoted.
```ini
# /etc/systemd/system/jellyfin.service.d/anaglyfin.conf
[Service]
Environment=JELLYFIN_FFMPEG=/opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
Environment=ANAGLYFIN_REAL_FFMPEG=/opt/anaglyfin/ffmpeg/ffmpeg-mvc
# The stock FFmpeg this server would otherwise run. If the server was pointed at a
# different FFmpeg before Anaglyfin, name that one instead of guessing.
Environment=ANAGLYFIN_SERVER_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg
Environment=ANAGLYFIN_LOCK_DIR=/var/lib/jellyfin/anaglyfin/lock
Environment=ANAGLYFIN_WRAPPER_SETTINGS=/var/lib/jellyfin/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```

**8. Apply it and restart once** — which also loads the plugin from §2:
```sh
sudo systemctl daemon-reload
sudo systemctl restart jellyfin
```

### 5.2 Update

Take the plugin update from the catalog first (§2); plugin and wrapper are one release and move together. Stop the service
before replacing either file, and start it once at the end.
```sh
sudo systemctl stop jellyfin
curl -fsSL https://github.com/tthayer93/Anaglyfin/releases/download/<new-tag>/anaglyfin-ffmpeg -o /tmp/anaglyfin-ffmpeg
curl -fsSL https://github.com/tthayer93/Anaglyfin/releases/download/<new-tag>/SHA256SUMS.txt -o /tmp/SHA256SUMS.txt
( cd /tmp && grep ' anaglyfin-ffmpeg$' SHA256SUMS.txt | sha256sum -c - )
sudo install -m 0755 -o jellyfin -g jellyfin /tmp/anaglyfin-ffmpeg /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
sudo systemctl start jellyfin
```
For FFmpeg-mvc, download the new tag's tarball and rebuild it exactly as in step 5, both `-rpath` lines included, then swap the compiled
`ffmpeg` over `/opt/anaglyfin/ffmpeg/ffmpeg-mvc` with the service stopped and start again. Never install that build's `ffprobe`: the sidecar
stays the stock one. The drop-in needs no change unless a path inside it moved; if it did, `daemon-reload` before the start.

### 5.3 Uninstall

1. Take **Anaglyfin** out of **Dashboard → Plugins**, then `sudo systemctl stop jellyfin`.
2. Delete `/etc/systemd/system/jellyfin.service.d/anaglyfin.conf` **and every other Anaglyfin environment entry, wherever you
   put it** — an optional `ANAGLYFIN_SLOT_WAIT_MS` or the `ANAGLYFIN_OFFICIAL_FFMPEG` alias may sit in another drop-in.
3. Remove the runtime files and the state beside them, then reload and start, which puts the server back on the stock FFmpeg:

   ```sh
   sudo rm -rf /opt/anaglyfin /var/lib/jellyfin/anaglyfin
   sudo systemctl daemon-reload
   sudo systemctl start jellyfin
   ```

4. Clear any path saved in **Dashboard → Playback → FFmpeg path**.

Jellyfin and its `/usr/lib/jellyfin-ffmpeg` files are untouched.

## 6. Windows official service

What §5 does on Linux, on Windows: the same three runtime files under `C:\ProgramData\Jellyfin\Server\anaglyfin`, the same
five variables handed to the `JellyfinServer` service instead of to systemd. Everything below is PowerShell (5.1 or 7)
**elevated to Administrator** except step 4, which runs in **MSYS2 CLANG64**.

> **Status: documented, not runtime-validated.** No Windows host has run this wrapper or an FFmpeg-mvc build in this
> project's CI or validation runs, so nothing below is evidence — your own verify pass is. Two facts force local builds:
> there is no `win-x64` wrapper asset, and FFmpeg-mvc publishes no binaries for any platform.

### 6.1 Initial install

**1. Prerequisites** — checked before anything is built.
```powershell
Get-Service JellyfinServer
Get-ChildItem "C:\Program Files\Jellyfin\Server\ffmpeg.exe", "C:\Program Files\Jellyfin\Server\ffprobe.exe"
git --version
dotnet --version
```
- **`JellyfinServer` exists and starts.** Only the official installer creates it; if it is missing, Jellyfin's own documentation is the next
  step, not this page — a portable zip or a hand-run `jellyfin.exe` is not the shape anything here configures.
- **Both stock FFmpeg files are present.** `ffmpeg.exe` becomes `ANAGLYFIN_SERVER_FFMPEG`, `ffprobe.exe` the sidecar. Get a missing one from
  Jellyfin, and never point `ANAGLYFIN_SERVER_FFMPEG` at the FFmpeg-mvc build.
- **Git, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the SDK, not the runtime) and MSYS2 with its
  [CLANG64](https://www.msys2.org/) environment** serve steps 3 and 4, all three build-time only: the wrapper publishes self-contained, so
  the server needs no .NET runtime to run it.

**2. Directories, and the right to write them.**
```powershell
$anaglyfinRoot = "C:\ProgramData\Jellyfin\Server\anaglyfin"
New-Item -ItemType Directory -Force -Path `
  "$anaglyfinRoot\ffmpeg", "$anaglyfinRoot\lock", "$anaglyfinRoot\wrapper" | Out-Null
icacls $anaglyfinRoot /grant "NT AUTHORITY\NETWORK SERVICE:(OI)(CI)M" /T
```
`ffmpeg` holds the three runtime files; `lock` and `wrapper` are written **while the service runs** — the wrapper's concurrency slots, the
plugin's settings document — which is what the grant is for. The installer runs the service as **Network Service**; if this service got
another account, grant to that one, reading it with
`Get-CimInstance Win32_Service -Filter "Name='JellyfinServer'" | Select-Object -ExpandProperty StartName`.

**3. Build the wrapper for `win-x64`** — there is no Windows asset to download, so the tag is published locally. Clone the tag the catalog
installed rather than a branch tip: `v0.2.2` is what §2 installs, and a release bumps plugin and wrapper together.
```powershell
git clone --depth 1 --branch v0.2.2 https://github.com/tthayer93/Anaglyfin.git C:\build\anaglyfin-wrapper-src
cd C:\build\anaglyfin-wrapper-src
dotnet publish .\src\Anaglyfin.FFmpegWrapper\Anaglyfin.FFmpegWrapper.csproj `
  --configuration Release --runtime win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded `
  --output C:\build\anaglyfin-wrapper
Rename-Item C:\build\anaglyfin-wrapper\Anaglyfin.FFmpegWrapper.exe anaglyfin-ffmpeg.exe
```
Rename **only** that `.exe`: the apphost locates its managed entry point from build time rather than from its filename, so Jellyfin may see
any name. If the output is a `.dll` plus a `.runtimeconfig.json` instead of one `.exe`, single-file did not apply — those two are loaded by
name and must not be renamed.

**4. Build FFmpeg-mvc** *(MSYS2 CLANG64; advanced; never run by this project).* FFmpeg-mvc carries no Windows build scripts, so the parent
project's are copied in beside the source and run there. Open **MSYS2 CLANG64** — not plain `MSYS` and not `MINGW64`: the toolchain installs
into CLANG64 and the other two shells cannot see it. The two tags belong together, and the parent checkout supplies only its `msys2/` scripts.
```bash
pacman -Syu --noconfirm
pacman -S --needed --noconfirm \
  git curl wget zip unzip \
  quilt diffstat \
  mingw-w64-clang-x86_64-toolchain \
  mingw-w64-clang-x86_64-nasm
mkdir -p /c/build/anaglyfin
cd /c/build/anaglyfin
git clone --depth 1 --branch v8.1.3-1 https://github.com/jellyfin/jellyfin-ffmpeg.git
git clone --depth 1 --branch n8.1.3-mvc10-jf1 https://github.com/tthayer93/FFmpeg-mvc.git
cp -r jellyfin-ffmpeg/msys2 FFmpeg-mvc/
cd FFmpeg-mvc
bash msys2/build.sh
```
**The artifact is `/c/build/anaglyfin/FFmpeg-mvc/ffmpeg.exe`** — step 5 spells it `C:\build\anaglyfin\FFmpeg-mvc\ffmpeg.exe`. Everything
`build.sh` does after `make` is the parent's packaging: it reads `debian/changelog`, which an FFmpeg-mvc product tag does not have, so the
script can exit non-zero **after a build that succeeded** over a file already built in the source root; only a script that ran to its end has
moved that file under `msys2\`. If it fails, do not invent `configure` flags — start again from the parent's published script and its
dependencies, or take the question to the FFmpeg-mvc maintainer, because a hand-rolled flag set is how you get a binary advertising an
encoder it cannot run.

**5. Copy the three runtime files.**
```powershell
$ffmpegDir = "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg"
Copy-Item C:\build\anaglyfin-wrapper\anaglyfin-ffmpeg.exe "$ffmpegDir\anaglyfin-ffmpeg.exe" -Force
Copy-Item C:\build\anaglyfin\FFmpeg-mvc\ffmpeg.exe        "$ffmpegDir\ffmpeg-mvc.exe"       -Force
# the STOCK ffprobe, from the installer — never the one step 4 produced
Copy-Item "C:\Program Files\Jellyfin\Server\ffprobe.exe"  "$ffmpegDir\ffprobe.exe"          -Force
```
They inherit the modify right step 2's grant put on those directories; `Get-ChildItem $ffmpegDir` now shows three `.exe` files.

**6. Service environment — the service's own, not your shell's.** A started helper inherits the environment **the service** was started with,
so your session variables and `setx` reach nothing. NSSM keeps that environment under the service's own key; this block reads what is there,
keeps everything neither empty nor an Anaglyfin name, adds the five, and writes the union back:
```powershell
$parameters = "HKLM:\SYSTEM\CurrentControlSet\Services\JellyfinServer\Parameters"
$anaglyfinRoot = "C:\ProgramData\Jellyfin\Server\anaglyfin"
$ffmpegDir = "$anaglyfinRoot\ffmpeg"
$anaglyfinEnvironment = @(
  "JELLYFIN_FFMPEG=$ffmpegDir\anaglyfin-ffmpeg.exe"
  "ANAGLYFIN_REAL_FFMPEG=$ffmpegDir\ffmpeg-mvc.exe"
  "ANAGLYFIN_SERVER_FFMPEG=C:\Program Files\Jellyfin\Server\ffmpeg.exe"
  "ANAGLYFIN_LOCK_DIR=$anaglyfinRoot\lock"
  "ANAGLYFIN_WRAPPER_SETTINGS=$anaglyfinRoot\wrapper\anaglyfin-wrapper-settings.json"
)
# On a server never configured this way the value does not exist yet, the read reports
# nothing, and $kept is empty.
$existing = @(
  (Get-ItemProperty -Path $parameters -Name AppEnvironmentExtra `
     -ErrorAction SilentlyContinue).AppEnvironmentExtra
)
$kept = @($existing | Where-Object { $_ -and $_ -notmatch '^(JELLYFIN_FFMPEG=|ANAGLYFIN_)' })
Set-ItemProperty -Path $parameters -Name AppEnvironmentExtra `
  -Value ($kept + $anaglyfinEnvironment) -Type MultiString
```
Three rules about that value, each load-bearing: **`AppEnvironmentExtra`, never `AppEnvironment`** — the first is *appended* to the
environment the service starts with, the second **replaces** it, leaving Jellyfin no `PATH` and no `SystemRoot`; **`REG_MULTI_SZ`, one
`NAME=VALUE` per entry**, exactly what `nssm.exe set JellyfinServer AppEnvironmentExtra NAME=VALUE …` writes, so either tool is fine and
neither is a second configuration to keep in sync, and several assignments never share an entry; and **no quotes around a path** —
`ANAGLYFIN_SERVER_FFMPEG` above points into `C:\Program Files\...` and is correct as written, while the same path in literal `"` characters
names a file that cannot be found.

`ANAGLYFIN_WRAPPER_SETTINGS` is not one to skip: the plugin writes its subtitle-depth and concurrency document to exactly that
path, and a wrapper can only read a document it is *told* about. Unset, the file sits under `plugins\configurations` where no
wrapper looks, and subtitle depth quietly stays flat with nothing logged as an error.

**7. Restart once:**
```powershell
Restart-Service JellyfinServer
```
NSSM reads `Parameters` when it launches the server, so the new environment belongs only to processes started after this command — and this is
also the restart that loads the plugin from §2. Then run §7: on this shape your own verify pass is the only evidence that exists.

### 6.2 Update

Take the plugin update from the catalog first (§2), then swap the file with the service stopped — a running `.exe` cannot be overwritten — and
start it again.

**The wrapper:** the tag changes, the clone does not, because `git clone` refuses an existing directory and the old tag leaves
a detached HEAD that `--detach` moves off cleanly.
```powershell
cd C:\build\anaglyfin-wrapper-src
git fetch --depth 1 origin "<new-tag>"
git checkout --detach FETCH_HEAD
```
Re-run step 3's publish block unchanged, rename its output as step 3 does, then swap it with the service stopped:
```powershell
Stop-Service JellyfinServer
Copy-Item C:\build\anaglyfin-wrapper\anaglyfin-ffmpeg.exe `
  "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe" -Force
Start-Service JellyfinServer
```
**FFmpeg-mvc:** the same two git commands in its clone at `C:\build\anaglyfin\FFmpeg-mvc`, rebuild, and swap `ffmpeg-mvc.exe` the same way
(use the build's source-root `ffmpeg.exe` unless the copied `msys2\` packaging moved it). The copied `msys2\` belongs to neither tag, so it
stays where it is. Do **not** replace `ffprobe.exe` from that build.

### 6.3 Uninstall

1. Take **Anaglyfin** out of **Dashboard → Plugins**, then `Stop-Service JellyfinServer`.
2. Take Anaglyfin's entries out of `AppEnvironmentExtra` by reading the current value, keeping entries that are not `JELLYFIN_FFMPEG=` or
   `ANAGLYFIN_`, writing the survivors back as `REG_MULTI_SZ` when any remain, and deleting the value only when it held entries but none
   survive; then remove the runtime directory — §6.1 step 2's `icacls` grant goes with the directory it was applied to — and restart the
   service:

   ```powershell
   $parameters = "HKLM:\SYSTEM\CurrentControlSet\Services\JellyfinServer\Parameters"
   $existing = @(
     (Get-ItemProperty -Path $parameters -Name AppEnvironmentExtra `
        -ErrorAction SilentlyContinue).AppEnvironmentExtra
   )
   $kept = @($existing | Where-Object { $_ -and $_ -notmatch '^(JELLYFIN_FFMPEG=|ANAGLYFIN_)' })
   if ($kept.Count -gt 0) {
     Set-ItemProperty -Path $parameters -Name AppEnvironmentExtra `
       -Value $kept -Type MultiString
   } elseif ($existing.Count -gt 0) {
     Remove-ItemProperty -Path $parameters -Name AppEnvironmentExtra
   }
   Remove-Item -Recurse -Force "C:\ProgramData\Jellyfin\Server\anaglyfin"
   Restart-Service JellyfinServer
   ```

3. Clear any path saved in **Dashboard → Playback → FFmpeg path**, which is used in preference to the server's own program directory and so is
   where that correction belongs.

`JELLYFIN_FFMPEG` left naming the deleted wrapper is what bites: the server looks for its FFmpeg where the wrapper used to be. This does not
uninstall Jellyfin — everything under `C:\Program Files\Jellyfin\Server`, its `ffmpeg.exe` and `ffprobe.exe` and its NSSM included, stays
exactly as Jellyfin installed it.

## 7. Verify

Run these once, on the shape you installed. `docs/validation.md` is the full checklist and the recorded results; every row recorded there is a
Linux result, and Windows has none.

**1. The plugin page.** `Dashboard → Plugins` lists **Anaglyfin** and its settings page opens.

**2. The server names the wrapper as its FFmpeg.** That startup line, naming the wrapper's path, is what identifies the
wrapper as Jellyfin's FFmpeg; no server-reported FFmpeg version is part of the contract.

| Shape | The line |
| --- | --- |
| Docker | `MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg` |
| Linux package | `MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg` |
| Windows service | `MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe` |

```sh
docker compose logs jellyfin 2>&1 | grep 'MediaEncoder: FFmpeg:'        # Docker
sudo journalctl -u jellyfin --no-pager | grep 'MediaEncoder: FFmpeg:'   # Linux package
# Windows service; its log directory is C:\ProgramData\Jellyfin\Server\log
Select-String -Path "C:\ProgramData\Jellyfin\Server\log\*.log" -Pattern 'MediaEncoder: FFmpeg:' | Select-Object -Last 3
```
The Windows startup environment dump echoes only `JELLYFIN_`, `DOTNET_` and `ASPNETCORE_` names, so it proves `JELLYFIN_FFMPEG` and nothing
else — the two checks below are the proof for the rest.

**3. The stock `ffprobe` is the sidecar.** Two matching first lines are the answer wanted, because an FFmpeg-mvc `ffprobe`
names the mvc tag in its banner.
```sh
# Docker
docker compose exec -u root jellyfin sh -c '
  /config/anaglyfin/ffmpeg/ffprobe -version | head -1
  /usr/lib/jellyfin-ffmpeg/ffprobe  -version | head -1'

# Linux package
/opt/anaglyfin/ffmpeg/ffprobe -version | head -1
/usr/lib/jellyfin-ffmpeg/ffprobe     -version | head -1

# Windows service
& "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\ffprobe.exe" -version | Select-Object -First 1
& "C:\Program Files\Jellyfin\Server\ffprobe.exe"                -version | Select-Object -First 1
```

**4. The wrapper dispatches to FFmpeg-mvc.** `-version` is an ordinary command, so it goes to `ANAGLYFIN_SERVER_FFMPEG` when that variable is
set; blanking it for one invocation (a blank value counts as unset) sends the command down the marker route, so the banner that comes back is
the FFmpeg-mvc build's.
```sh
# Docker
docker compose exec -u root jellyfin \
  env ANAGLYFIN_SERVER_FFMPEG= ANAGLYFIN_REAL_FFMPEG=/config/anaglyfin/ffmpeg/ffmpeg-mvc \
  /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version

# Linux package
sudo -u jellyfin env ANAGLYFIN_SERVER_FFMPEG= ANAGLYFIN_REAL_FFMPEG=/opt/anaglyfin/ffmpeg/ffmpeg-mvc \
  /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version

# Windows service
$env:ANAGLYFIN_REAL_FFMPEG = "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\ffmpeg-mvc.exe"
Remove-Item Env:ANAGLYFIN_SERVER_FFMPEG -ErrorAction SilentlyContinue
& "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe" -version
```
These run as **your** account, so they prove dispatch and the binaries, not the service account's right to write the `lock` and `wrapper`
directories.

**5. One real Anaglyfin transcode plays**, from a client, with an MVC item and a profile enabled. Its `transcode_<n>.log` in the server's log
directory shows the wrapper's command line carrying the marker URL and the `ffmpeg-mvc` child's carrying the real source path and the
profile's filters with no marker in it (`docs/validation.md` V6/V7).

**6. The encoder Jellyfin selects exists in the FFmpeg-mvc build** — a one-directional check. Read `-codec:v` off that transcode log and
confirm it is listed by `ffmpeg-mvc -encoders`. Once the startup probes answer from the official binary, the server can reach for an encoder
the minimal build does not carry, so the official list being a superset is expected and is not itself a fault; a *marker* job naming an
encoder the build lacks is. The documented build flags give parity for the QSV/VA-API H.264 flow, not for every encoder a 12.x server can
select.

**7. Stopping a transcode stops cleanly** — the caveat specific to Windows. There are no POSIX signals there and the wrapper registers no
stop-signal handler, so a stop rests on the `q` the server writes to FFmpeg's stdin, which reaches the FFmpeg-mvc child through the stdin the
wrapper inherits. `Get-Process ffmpeg-mvc` after a stopped playback tells you whether a child was left behind, and §8 says what an orphan does
and does not cost you.

## 8. Troubleshooting

- **Server starts, ordinary playback works, and every Anaglyfin version fails with** `anaglyfin-wrapper: refused: the FFmpeg binary named by
  ANAGLYFIN_REAL_FFMPEG ('ffmpeg-mvc') is not an existing file.` In a two-binary deployment the startup probes and ordinary playback reach the
  stock FFmpeg, so a missing `ffmpeg-mvc` stays invisible until an Anaglyfin job asks for it. Install the file, or fix the variable naming it.
- **The server will not start at all:** `FFmpeg validation: The process returned no result` and `Path set by command line or environment
  variable is invalid`. That message blames the `JELLYFIN_FFMPEG` path — the wrapper — but in a single-binary deployment the startup probe
  itself goes to `ANAGLYFIN_REAL_FFMPEG`, so the missing file is usually the other one. Check both binaries before changing either path.
- **`Running /config/anaglyfin/ffmpeg/ffprobe ... No such file or directory`**, and a library that never fills: no sidecar `ffprobe`. Copy the
  **official** one beside the wrapper — the derived name is always `ffprobe` in that same directory, never `anaglyfin-ffprobe`, and `PATH` is
  never consulted — then restart.
- **A path that exists and is still reported invalid** is quoted. Environment values reach the process as written and the server hands the path
  to the operating system rather than to a shell, so a literal `"` is part of the filename. Strip quotes from the environment entry, and from
  anything saved in **Dashboard → Playback → FFmpeg path**.
- **An encoder or filter the marker job needs is missing from `ffmpeg-mvc -encoders`.** The build lacks it: rebuild with build-flag parity
  against the official FFmpeg, or change what the server may select on its device profile. A wrapper change is not the fix — see check 6 above.
- **`Unrecognized option 'vbr:a'` and `Error splitting the argument list: Option not found`**, immediately after the child's banner with
  nothing after it, on an Anaglyfin version's child. That signature is the audio capability inversion: the official build's probe advertised
  `libfdk_aac`, so the server wrote `-codec:a… libfdk_aac` plus that encoder's private `-vbr:a <n>` into a marker command that runs on the
  minimal FFmpeg-mvc build, which carries neither and dies while parsing, before opening any input. The `v0.2.1` and later wrapper maps this
  one known selection onto native `aac` on the marker route — bitrate synthesized from a fixed table — and logs an `anaglyfin-wrapper:
  warning: the FFmpeg-mvc build …` line naming the fallback. On a `v0.2.0` wrapper the same job dies this way: turn off **Enable audio VBR** in
  the server's playback settings, or unset `ANAGLYFIN_SERVER_FFMPEG`. Any *other* capability gap stays the bullet above — the wrapper maps this
  one audio selection and nothing else.
- **Windows: `Get-Process ffmpeg-mvc` still shows a process after the playback stopped.** When a transcode ignores the `q`, the server waits
  five seconds and terminates the process it owns — the **wrapper** — and Windows kills no process tree with that call, so the FFmpeg-mvc child
  is orphaned. `Stop-Process` by that name, or another service restart, clears it. The concurrency slot does not go with the orphan: a slot
  file is opened `DeleteOnClose`, so the operating system releases it the moment the wrapper is gone and the next Anaglyfin playback is not
  locked out. A stop that *hangs* is worth writing up, because no run of this project has measured the `q` path on Windows.
- **Everything above passes and the picture is wrong**, or versions never appear for a file you expected: that is the product checklist rather
  than this page — `docs/validation.md` V4–V10.
