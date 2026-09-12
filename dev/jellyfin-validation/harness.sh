#!/bin/sh
# Convenience front door for the Anaglyfin runtime-validation harness.
#
# Every subcommand is a `docker compose` invocation over compose.jellyfin.yml, plus the
# preflight that harness makes obvious. Nothing here is part of the CI gate, nothing here
# reaches a server that is not on this machine, and everything here is throwaway: the
# state it writes lives in this directory and git ignores it.
#
#   sh harness.sh preflight              what will be mounted, and is it there
#   sh harness.sh up                     start the stack in the background
#   sh harness.sh check                  one-shot wrapper check (no server needed)
#   sh harness.sh status | logs | down   the usual
#   sh harness.sh collect                bundle the evidence docs/validation.md asks for
#
# Two switches, as environment variables, because they change what compose is asked to
# create rather than what the stack does:
#
#   HARNESS_QSV=1            add compose.qsv.yml (/dev/dri into the container)
#   HARNESS_HOST_NETWORK=1   add compose.host-network.yml (no published port)
#
# Everything else is a plain variable in .env; see .env.example.

set -u

cd -- "$(dirname -- "$0")" || exit 1

BASE_FILE=compose.jellyfin.yml

# The defaults below are the same ones the compose file interpolates, repeated here only
# so that preflight can report and prepare them. Keep the two lists in step.
PLUGIN_DLL="${HARNESS_PLUGIN_DLL:-../../src/Anaglyfin/bin/Release/net10.0/Anaglyfin.dll}"
WRAPPER_BIN="${HARNESS_WRAPPER_BIN:-../../artifacts/anaglyfin-ffmpeg}"
CONFIG_DIR="${HARNESS_CONFIG_DIR:-./jellyfin/config}"
CACHE_DIR="${HARNESS_CACHE_DIR:-./jellyfin/cache}"
MEDIA_DIR="${HARNESS_MEDIA_DIR:-./media}"
REAL_FFMPEG="${HARNESS_REAL_FFMPEG:-/usr/lib/jellyfin-ffmpeg/ffmpeg}"
LOCK_DIR="${HARNESS_LOCK_DIR:-/tmp/anaglyfin/ffmpeg-wrapper}"
MAX_JOBS="${HARNESS_MAX_CONCURRENT_TRANSCODES:-1}"
WEB_PORT="${HARNESS_WEB_PORT:-8096}"
IMAGE="${HARNESS_IMAGE:-jellyfin/jellyfin:latest}"

compose_files="-f $BASE_FILE"
[ "${HARNESS_QSV:-0}" = "1" ] && compose_files="$compose_files -f compose.qsv.yml"
[ "${HARNESS_HOST_NETWORK:-0}" = "1" ] && compose_files="$compose_files -f compose.host-network.yml"

# `set -u` and a word-splitted list of -f arguments; quoting them would make compose see
# one argument out of four.
# shellcheck disable=SC2086
compose() { docker compose $compose_files "$@"; }

# The paths in .env are the ones the Docker DAEMON reads, and a daemon running on another
# host - an agent container over the host's daemon, which is why .ci/test.yml carries
# BUILD_CONTEXT at all - sees this checkout under a different mount point than this shell
# does. Set the two prefixes when that is the case and the checks below stop reporting a
# file the daemon can read as missing. Leave them unset anywhere else.
DAEMON_PREFIX="${HARNESS_DAEMON_PREFIX:-}"
LOCAL_PREFIX="${HARNESS_LOCAL_PREFIX:-}"
local_view() {
    path="$1"
    if [ -n "$DAEMON_PREFIX" ]; then
        case "$path" in
            "$DAEMON_PREFIX"*) path="$LOCAL_PREFIX${path#"$DAEMON_PREFIX"}" ;;
        esac
    fi
    printf '%s' "$path"
}

# Preflight accepts either view for filesystem checks: the raw daemon-visible path or the
# translated local path. Compose still receives the raw daemon-visible paths.
path_pair() {
    raw="$1"
    translated=$(local_view "$raw")
    if [ "$translated" = "$raw" ]; then
        printf '%s' "$raw"
    else
        printf '%s (this shell: %s)' "$raw" "$translated"
    fi
}

first_nonempty_view() {
    raw="$1"
    translated=$(local_view "$raw")
    if [ -n "$translated" ] && [ "$translated" != "$raw" ] && [ -s "$translated" ]; then
        printf '%s' "$translated"
        return 0
    fi
    if [ -s "$raw" ]; then
        printf '%s' "$raw"
        return 0
    fi
    return 1
}

first_existing_dir_view() {
    raw="$1"
    translated=$(local_view "$raw")
    if [ -n "$translated" ] && [ "$translated" != "$raw" ] && [ -d "$translated" ]; then
        printf '%s' "$translated"
        return 0
    fi
    if [ -d "$raw" ]; then
        printf '%s' "$raw"
        return 0
    fi
    return 1
}

create_dir_view() {
    raw="$1"
    translated=$(local_view "$raw")
    if [ -n "$translated" ] && [ "$translated" != "$raw" ]; then
        if mkdir -p -- "$translated" 2>/dev/null; then
            printf '%s' "$translated"
            return 0
        fi
    else
        if mkdir -p -- "$raw" 2>/dev/null; then
            printf '%s' "$raw"
            return 0
        fi
    fi
    return 1
}

say() { printf '%s\n' "$*"; }
die() { printf 'harness: %s\n' "$*" >&2; exit 1; }

preflight() {
    missing=0

    docker info >/dev/null 2>&1 || die "docker is not reachable from this shell"

    say "== validation harness preflight =="
    say "image          $IMAGE"
    say "web ui         http://localhost:${WEB_PORT}"
    say "plugin dll     $(path_pair "$PLUGIN_DLL")"
    say "wrapper        $(path_pair "$WRAPPER_BIN")"
    say "config         $(path_pair "$CONFIG_DIR") -> /config"
    say "cache          $(path_pair "$CACHE_DIR")  -> /cache"
    say "media          $(path_pair "$MEDIA_DIR")  -> /media"
    say "ffmpeg target  ANAGLYFIN_FFMPEG (in container) = ${HARNESS_JELLYFIN_FFMPEG:-/config/anaglyfin/ffmpeg/anaglyfin-ffmpeg}"
    say "real ffmpeg    ANAGLYFIN_REAL_FFMPEG (in container) = $REAL_FFMPEG"
    say "lock dir       ANAGLYFIN_LOCK_DIR (in container) = $LOCK_DIR"
    say "max transcodes ANAGLYFIN_MAX_CONCURRENT_TRANSCODES = $MAX_JOBS"
    [ -n "${HARNESS_QSV:-}" ] && [ "$HARNESS_QSV" != "0" ] && say "qsv overlay    on (/dev/dri)"
    [ -n "${HARNESS_HOST_NETWORK:-}" ] && [ "$HARNESS_HOST_NETWORK" != "0" ] \
        && say "network        host (the port mapping above is then ignored)"
    say ''

    # A bind mount whose source does not exist is created for you, as an empty root-owned
    # directory. For a plugin assembly and a wrapper binary that is the worst possible
    # mistake - the server would find a directory where a file belongs - so the two
    # artifacts are checked before anything starts. The checks accept either the raw
    # daemon path or its local view when `HARNESS_DAEMON_PREFIX` / `HARNESS_LOCAL_PREFIX`
    # name the split.
    plugin_dll_found=$(first_nonempty_view "$PLUGIN_DLL")
    if [ -z "$plugin_dll_found" ]; then
        say "MISSING  plugin dll: $(path_pair "$PLUGIN_DLL")"
        say '         run the CI job (README.md step 1), or point HARNESS_PLUGIN_DLL at'
        say '         the Anaglyfin.dll extracted from artifacts/Anaglyfin_<version>.zip'
        missing=$((missing + 1))
    else
        say "found    plugin dll ($plugin_dll_found)"
    fi

    wrapper_found=$(first_nonempty_view "$WRAPPER_BIN")
    if [ -z "$wrapper_found" ]; then
        say "MISSING  wrapper: $(path_pair "$WRAPPER_BIN")"
        say '         run the CI job (README.md step 1), or point HARNESS_WRAPPER_BIN at'
        say '         an anaglyfin-ffmpeg artifact'
        missing=$((missing + 1))
    else
        if [ -x "$wrapper_found" ]; then
            say "found    wrapper, executable ($wrapper_found)"
        else
            chmod 0755 "$wrapper_found" 2>/dev/null \
                && say "found    wrapper; executable bit restored ($wrapper_found)" \
                || { say "MISSING  executable bit on $(path_pair "$WRAPPER_BIN") and chmod failed"; missing=$((missing + 1)); }
        fi
    fi

    # These three are written to by the container, so they exist as directories before
    # Docker creates them as root. In split-prefix setups the local view is the path this
    # shell can create; compose still gets the raw daemon path.
    for dir in "$CONFIG_DIR" "$CACHE_DIR" "$MEDIA_DIR"; do
        dir_found=$(first_existing_dir_view "$dir")
        if [ -n "$dir_found" ]; then
            say "found    state dir ($dir_found)"
        else
            created=$(create_dir_view "$dir") || created=""
            if [ -n "$created" ]; then
                say "created  state dir ($created)"
            else
                say "MISSING  state dir $(path_pair "$dir") could not be created"
                missing=$((missing + 1))
            fi
        fi
    done

    media_found=$(first_existing_dir_view "$MEDIA_DIR")
    if [ -n "$media_found" ] && [ -z "$(ls -A -- "$media_found" 2>/dev/null)" ]; then
        say ''
        say "note:    $media_found is empty. The scan will find nothing to offer versions"
        say '         for. Copy or link a sample in, e.g.'
        say '           cp "/path/to/Movie.2010.3D.1080p.MVC.mkv" '"$media_found/"
    fi

    [ "$missing" -eq 0 ] || die "$missing required input(s) missing; nothing was started"
    say ''
    say 'ready.'
}

case "${1:-help}" in
    preflight)
        preflight
        ;;
    up)
        preflight || exit 1
        compose up -d
        say ''
        say "Jellyfin setup wizard: http://localhost:${WEB_PORT}"
        say 'Next: README.md step 4, which lists the log lines a healthy start writes.'
        say 'If media cannot be probed, the wrapper directory still needs an ffprobe:'
        say '  sh harness.sh ffprobe   and then restart the server'
        ;;
    check)
        compose run --rm wrapper-check
        ;;
    status)
        compose ps
        ;;
    logs)
        shift
        compose logs -f --tail 200 "$@"
        ;;
    ffprobe)
        # Jellyfin looks for ffprobe next to the FFmpeg it was given, which is the
        # wrapper's directory, so that directory has to hold one. Copy the image's own
        # - or the matching build's, when an FFmpeg-mvc build is mounted, by naming its
        # ffprobe inside the container:  sh harness.sh ffprobe /opt/anaglyfin/ffmpeg-mvc/ffprobe
        # The target is refused as a source, and the copy is staged through a temporary
        # file, so a bad source cannot leave the wrapper directory without an ffprobe.
        shift
        src="${1:-}"
        # Quoted for the container's shell, which is the one that reads these paths.
        compose exec -T jellyfin sh -eu -c '
            target_dir=$(dirname "${JELLYFIN_FFMPEG:?only reachable through JELLYFIN_FFMPEG}")
            target="$target_dir/ffprobe"
            src=${1:-}
            if [ -z "$src" ]; then
                for candidate in /usr/lib/jellyfin-ffmpeg/ffprobe /usr/bin/ffprobe /bin/ffprobe; do
                    if [ -x "$candidate" ]; then
                        src=$candidate
                        break
                    fi
                done
            fi
            if [ -z "$src" ]; then
                echo "no ffprobe to copy; name one inside the container" >&2
                exit 1
            fi
            if [ "$src" = "$target" ]; then
                echo "refusing to copy the ffprobe target onto itself: $src" >&2
                exit 1
            fi
            tmp="$target.tmp.$$"
            trap "rm -f \"$tmp\" 2>/dev/null || true" EXIT
            cp -f "$src" "$tmp"
            chmod 0755 "$tmp"
            mv -f "$tmp" "$target"
            "$target" -version | head -n 1
            echo "copied $src to $target; restart the server to pick it up"
        ' sh "$src"
        ;;
    collect)
        stamp=$(date -u +%Y%m%dT%H%M%SZ 2>/dev/null || printf 'manual')
        out="collect/$stamp"
        mkdir -p -- "$out" || die "could not create $out"
        compose logs --no-color --timestamps >"$out/jellyfin-container.log" 2>&1
        compose exec -T jellyfin sh -c \
            'ls -l /config/plugins/Anaglyfin /config/anaglyfin/ffmpeg; echo; ls -l /usr/lib/jellyfin-ffmpeg 2>/dev/null; echo; echo "JELLYFIN_FFMPEG=$JELLYFIN_FFMPEG"; echo "ANAGLYFIN_REAL_FFMPEG=$ANAGLYFIN_REAL_FFMPEG"; echo "ANAGLYFIN_LOCK_DIR=$ANAGLYFIN_LOCK_DIR"; echo "ANAGLYFIN_MAX_CONCURRENT_TRANSCODES=$ANAGLYFIN_MAX_CONCURRENT_TRANSCODES"' \
            >"$out/inside-container.txt" 2>&1
        compose exec -T jellyfin sh -c \
            'for p in /proc/[0-9]*; do printf "%s " "${p##*/}"; tr "\0" " " < "$p/cmdline" 2>/dev/null; echo; done' \
            >"$out/processes-in-container.txt" 2>&1
        ps -ww -eo pid,ppid,args 2>/dev/null \
            | grep -E 'jellyfin|ffmpeg|Anaglyfin' >"$out/processes-on-host.txt" 2>&1
        server_logs=$(local_view "$CONFIG_DIR")
        # The image points JELLYFIN_LOG_DIR at /config/log; transcode_N.log in there is
        # where the FFmpeg command lines are. Root-owned when the container ran as root,
        # so the copy may need sudo.
        if [ -d "$server_logs/log" ]; then
            cp -R -- "$server_logs/log" "$out/jellyfin-server-logs" 2>/dev/null \
                || say "note: could not copy $server_logs/log; rerun with sudo or copy it by hand"
        else
            say "note: no $server_logs/log directory yet - the server has not written a log"
        fi
        say "collected into $out"
        say 'paste back what README.md, "Testing the real server with these notes", lists.'
        ;;
    down)
        compose down
        say 'state dirs remain; delete them to make the run truly disposable.'
        ;;
    *)
        say 'usage: sh harness.sh preflight | up | check | status | logs | ffprobe | collect | down'
        say '  preflight  report what will be mounted and check that it is there'
        say '  up         preflight, then start the stack in the background'
        say '  check      one-shot wrapper check inside the Jellyfin image (no server)'
        say '  status     docker compose ps'
        say '  logs       follow the server log'
        say '  ffprobe    copy an ffprobe next to the wrapper, where Jellyfin looks for it'
        say '  collect    bundle the evidence README.md asks for'
        say '  down       stop the stack (state dirs stay, and are disposable)'
        say ''
        say 'switches: HARNESS_QSV=1, HARNESS_HOST_NETWORK=1; every path/port knob is in .env.example'
        exit 2
        ;;
esac
