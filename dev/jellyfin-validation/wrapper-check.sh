#!/bin/sh
# Optional, opt-in check for the Anaglyfin validation harness.
#
#   docker compose -f compose.jellyfin.yml run --rm wrapper-check
#
# It answers one question that no unit test can answer: does the Linux x64 wrapper
# artifact that CI produced actually run - and actually decide - inside the image
# Jellyfin runs in? The image is the runtime being validated, so it is also the host of
# this check; no .NET SDK, no GPU, no media, no server, and nothing is installed.
#
# The real FFmpeg is faked with the image's own /bin/echo, which is an ordinary
# executable that prints the arguments it was started with. That makes the wrapper's
# three outcomes observable as text: a passed-through vector comes back unchanged, a
# rewritten one comes back with the profile fragments inserted, and a refused one never
# reaches the child at all. /bin/echo collapses the boundaries between arguments, so the
# checks below look for fragments, not for exact tokenisation.
#
# Exit status: 0 when every mandatory check passed, 1 otherwise.

set -u

WRAPPER="${WRAPPER:-/config/anaglyfin/ffmpeg/anaglyfin-ffmpeg}"
IMAGE_FFMPEG="${ANAGLYFIN_REAL_FFMPEG:-/usr/lib/jellyfin-ffmpeg/ffmpeg}"
FAKE_FFMPEG=/bin/echo
DIAGNOSTIC='anaglyfin-wrapper:'

# A profile the build's catalog knows, and a marker carrying it. The source path is
# rooted and URL-encoded exactly as the media source provider mints it.
PROFILE='sbs_full'
REAL_SOURCE='/media/Movie.2010.3D.1080p.MVC.mkv'
MARKER="http://127.0.0.1/anaglyfin/profile/${PROFILE}?source=%2Fmedia%2FMovie.2010.3D.1080p.MVC.mkv"
MARKER_UNKNOWN='http://127.0.0.1/anaglyfin/profile/not-a-profile?source=%2Fmedia%2FMovie.mkv'

# An ordinary Jellyfin transcode vector: hardware decode options, its own maps, and no
# Anaglyfin marker anywhere. This is what an everyday playback looks like to the wrapper.
ORDINARY='-hide_banner -i /media/Ordinary.1080p.mkv -map_metadata -1 -map_chapters -1
    -map 0:0 -map 0:1 -hwaccel vaapi -vaapi_device /dev/dri/renderD128
    -c:v libx264 -preset veryfast -c:a copy -f segment -hls_time 6 out.m3u8'

failures=0
skips=0

note() { printf '%s\n' "$*"; }
ok() { printf 'PASS  %s\n' "$*"; }
bad() { printf 'FAIL  %s\n' "$*"; failures=$((failures + 1)); }
skip() { printf 'SKIP  %s\n' "$*"; skips=$((skips + 1)); }

note "== wrapper runtime check: $(date -u '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || echo 'unknown time') =="
note "kernel:  $(uname -s -m 2>/dev/null || echo 'uname unavailable')"
if [ -r /etc/os-release ]; then
    # shellcheck disable=SC1091
    note "base:    $(. /etc/os-release && printf '%s' "${PRETTY_NAME:-${NAME:-unknown}}")"
fi
note "wrapper: $WRAPPER"
note ''

# 1. The artifact is mounted, is a file, and carries its executable bit.
if [ ! -f "$WRAPPER" ]; then
    bad "the wrapper is not mounted at $WRAPPER - run the CI job (see README.md step 1) or set HARNESS_WRAPPER_BIN"
    note ''
    note "FAIL: nothing else can be checked without the artifact"
    exit 1
fi
if [ -x "$WRAPPER" ]; then
    ok "mounted as an executable file ($(wc -c < "$WRAPPER" | tr -d ' ') bytes)"
else
    bad "mounted but not executable - the mode must survive the checkout, see README.md troubleshooting"
fi

# 2. ELF identity: magic, 64-bit little-endian class, x86-64 machine. The packager
#    checks the same four bytes when it stages the artifact; this checks them again on
#    the machine that will execute them, because the artifact travels through a bind
#    mount and a checkout on the way.
header=$(od -An -tx1 -N20 "$WRAPPER" 2>/dev/null | tr -s ' ')
case "$header" in
    *' 7f 45 4c 46 02 01 '*) ok "ELF magic and 64-bit little-endian class" ;;
    *) bad "not a 64-bit little-endian ELF: ${header:-od could not read the file}" ;;
esac
case "$header" in
    *' 3e 00') ok 'ELF machine is x86-64' ;;
    *) bad "ELF machine is not x86-64 (header read: ${header:-empty})" ;;
esac

# 3. The wrapper process itself starts in this runtime, with a refusal that only the
#    managed code can produce: a rooted real-FFmpeg path that does not exist is exit 127
#    plus one diagnostic line, and no FFmpeg is started. A binary that could not load
#    here would die with the loader's own message and 126/139 instead.
out=$(ANAGLYFIN_REAL_FFMPEG=/nonexistent/anaglyfin-real-ffmpeg "$WRAPPER" -version 2>&1)
rc=$?
if [ "$rc" -eq 127 ] && printf '%s' "$out" | grep -q "$DIAGNOSTIC"; then
    ok 'the binary executes in this image (missing-real-FFmpeg refusal reached)'
else
    bad "expected exit 127 and a '${DIAGNOSTIC}' line, got exit ${rc}: $(printf '%s' "$out" | head -n 3)"
fi

# 4. Ordinary playback is invisible: an ordinary vector must come back from the child
#    unchanged, including the hardware-decode options, with no diagnostic and no slot.
out=$(ANAGLYFIN_REAL_FFMPEG=$FAKE_FFMPEG ANAGLYFIN_LOCK_DIR=/tmp/anaglyfin-check "$WRAPPER" $ORDINARY 2>&1)
rc=$?
if [ "$rc" -ne 0 ]; then
    bad "pass-through exited ${rc} instead of the child's 0: $(printf '%s' "$out" | head -n 3)"
else
    for fragment in '/media/Ordinary.1080p.mkv' '-hwaccel vaapi' '-map 0:1' '-c:a copy' 'out.m3u8'; do
        if printf '%s' "$out" | grep -qF -- "$fragment"; then
            ok "pass-through kept ${fragment}"
        else
            bad "pass-through lost ${fragment}: $(printf '%s' "$out" | head -n 3)"
        fi
    done
    if printf '%s' "$out" | grep -q -- "$DIAGNOSTIC"; then
        bad 'pass-through wrote a wrapper diagnostic; ordinary playback is supposed to be silent'
    else
        ok 'pass-through wrote no diagnostic'
    fi
fi

# 5. Marker transport and command rewrite: the marker token must arrive as one argument,
#    the child must see the decoded source path instead of it, and the profile of the
#    marker must be inserted into the command. This is docs/validation.md V6 and V7.1 at
#    the wrapper seam; only a real server can show the same pair arriving from Jellyfin.
out=$(ANAGLYFIN_REAL_FFMPEG=$FAKE_FFMPEG ANAGLYFIN_LOCK_DIR=/tmp/anaglyfin-check "$WRAPPER" \
    -hide_banner -i "$MARKER" -map_metadata -1 -map 0:0 -map 0:1 \
    -c:v libx264 -preset veryfast -c:a copy -f segment out.m3u8 2>&1)
rc=$?
if [ "$rc" -ne 0 ]; then
    bad "a valid marker was not started (exit ${rc}): $(printf '%s' "$out" | head -n 3)"
else
    if printf '%s' "$out" | grep -qF -- "$REAL_SOURCE"; then
        ok 'the child received the decoded source path'
    else
        bad "the child did not receive ${REAL_SOURCE}: $(printf '%s' "$out" | head -n 3)"
    fi
    if printf '%s' "$out" | grep -q 'anaglyfin/profile\|127\.0\.0\.1'; then
        bad 'the marker text reached the child process; it must be replaced, not forwarded'
    else
        ok 'no marker text reached the child process'
    fi
    for fragment in '-view_ids -1' '-sn'; do
        if printf '%s' "$out" | grep -qF -- "$fragment"; then
            ok "the ${PROFILE} rewrite inserted ${fragment}"
        else
            bad "the ${PROFILE} rewrite did not insert ${fragment}: $(printf '%s' "$out" | head -n 3)"
        fi
    done
    if printf '%s' "$out" | grep -q '0:v:view\|vidx:\|vpos:'; then
        bad "the ${PROFILE} rewrite left a view specifier in the command: $(printf '%s' "$out" | head -n 3)"
    else
        ok "the ${PROFILE} rewrite left no view specifier beside -view_ids"
    fi
    if printf '%s' "$out" | grep -qF -- '-c:v libx264'; then
        ok 'the rewrite kept the server-owned encoder arguments'
    else
        bad 'the rewrite removed the server-owned encoder arguments'
    fi
fi

# 6. A marker whose profile is not on the allowlist is refused before anything starts:
#    exit 65, one diagnostic, and no echo from the fake child at all.
out=$(ANAGLYFIN_REAL_FFMPEG=$FAKE_FFMPEG ANAGLYFIN_LOCK_DIR=/tmp/anaglyfin-check "$WRAPPER" \
    -hide_banner -i "$MARKER_UNKNOWN" -c:v libx264 out.m3u8 2>&1)
rc=$?
if [ "$rc" -eq 65 ] && printf '%s' "$out" | grep -q "$DIAGNOSTIC"; then
    ok 'an unknown profile is refused with exit 65'
else
    bad "expected exit 65 and a '${DIAGNOSTIC}' line, got exit ${rc}: $(printf '%s' "$out" | head -n 3)"
fi
if printf '%s' "$out" | grep -qF -- 'out.m3u8'; then
    bad 'the child ran for a refused command'
else
    ok 'the refused command never reached the child'
fi

# 7. The optional last step is the real thing: hand the wrapper the FFmpeg this image
#    ships and let it answer as FFmpeg would. Skipped when that path is not in the
#    image, because the whole point of ANAGLYFIN_REAL_FFMPEG is that an administrator
#    names the binary rather than the harness guessing at it.
if [ -x "$IMAGE_FFMPEG" ]; then
    out=$(ANAGLYFIN_REAL_FFMPEG=$IMAGE_FFMPEG ANAGLYFIN_LOCK_DIR=/tmp/anaglyfin-check "$WRAPPER" -version 2>&1)
    rc=$?
    if [ "$rc" -eq 0 ] && printf '%s' "$out" | grep -qi 'ffmpeg version'; then
        ok "pass-through reached the image's FFmpeg at ${IMAGE_FFMPEG}: $(printf '%s' "$out" | head -n 1)"
    else
        bad "pass-through to ${IMAGE_FFMPEG} failed (exit ${rc}): $(printf '%s' "$out" | head -n 3)"
    fi
else
    skip "no FFmpeg at ${IMAGE_FFMPEG}; discover the real path with the commands in README.md step 4 and set HARNESS_REAL_FFMPEG"
fi

note ''
if [ "$failures" -eq 0 ]; then
    note "OK: the wrapper artifact runs and decides correctly in this image (${skips} check(s) skipped)"
    exit 0
fi
note "FAILED: ${failures} check(s) failed (${skips} skipped)"
exit 1
