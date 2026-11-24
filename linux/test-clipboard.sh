#!/bin/sh
# Runs the Linux platform tests against a real X11 server (Xvfb) and a real Wayland compositor (headless sway).
# Needs: xvfb xclip wl-clipboard sway. Usage: linux/test-clipboard.sh  (from the repo root)
set -eu

RUNTIME=$(mktemp -d)
chmod 700 "$RUNTIME"
export XDG_RUNTIME_DIR="$RUNTIME"

Xvfb :99 -screen 0 1024x768x24 >/dev/null 2>&1 &
XVFB=$!
WLR_BACKENDS=headless WLR_LIBINPUT_NO_DEVICES=1 WLR_RENDERER=pixman sway -c /dev/null >"$RUNTIME/sway.log" 2>&1 &
SWAY=$!
trap 'kill $XVFB $SWAY 2>/dev/null; rm -rf "$RUNTIME"' EXIT

# Wait for both displays.
for _ in $(seq 50); do
  [ -S "$RUNTIME/wayland-1" ] && [ -e /tmp/.X11-unix/X99 ] && break
  sleep 0.1
done

# X11 tests: DISPLAY only. Wayland tests: WAYLAND_DISPLAY only (so each backend is tested in isolation).
env -u WAYLAND_DISPLAY DISPLAY=:99 KLIP_TEST_X11=1 \
  dotnet test linux/Klip.Linux.Platform.Tests --filter "FullyQualifiedName~X11|FullyQualifiedName~SystemTests" "$@"
env -u DISPLAY WAYLAND_DISPLAY=wayland-1 KLIP_TEST_WAYLAND=1 \
  dotnet test linux/Klip.Linux.Platform.Tests --filter "FullyQualifiedName~Wayland" "$@"
