#!/bin/sh
# Builds every release artifact into out/release/:
#   Klip-<v>-windows-x64.exe   portable, self-contained
#   Klip-<v>-android.apk       signed with the keystore described in android/app/build.gradle.kts
#   Klip-<v>-x86_64.AppImage   any distribution
#   klip_<v>_amd64.deb         Debian / Ubuntu / Mint
#   SHA256SUMS.txt
# Run from the repo root on Linux with the .NET 10 SDK, a JDK 21 and the Android SDK installed.
set -eu

V=$(sed -n 's#.*<Version>\(.*\)</Version>.*#\1#p' linux/Klip.Linux/Klip.Linux.csproj)
OUT=out/release
BUILD=out/build
rm -rf "$OUT" "$BUILD"
mkdir -p "$OUT" "$BUILD"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "== Klip $V =="

echo "-- Tests"
dotnet test Klip.slnx -c Release --nologo -v q
(cd core-kotlin && ./gradlew -q test)

echo "-- Windows"
dotnet publish windows/Klip.Windows -c Release -r win-x64 --self-contained -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$BUILD/windows" --nologo -v q
cp "$BUILD/windows/Klip.exe" "$OUT/Klip-$V-windows-x64.exe"

echo "-- Android"
(cd android && ./gradlew -q :app:lintRelease :app:assembleRelease)
cp android/app/build/outputs/apk/release/app-release.apk "$OUT/Klip-$V-android.apk"

echo "-- Linux"
dotnet publish linux/Klip.Linux -c Release -r linux-x64 --self-contained -p:DebugType=none -o "$BUILD/linux" --nologo -v q

# AppImage
APPDIR="$BUILD/AppDir"
mkdir -p "$APPDIR/usr/lib/klip" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/scalable/apps"
cp -r "$BUILD/linux/." "$APPDIR/usr/lib/klip/"
cp linux/packaging/AppRun "$APPDIR/AppRun" && chmod +x "$APPDIR/AppRun"
cp linux/packaging/klip.desktop "$APPDIR/klip.desktop"
cp linux/packaging/klip.desktop "$APPDIR/usr/share/applications/klip.desktop"
cp linux/packaging/klip.svg "$APPDIR/klip.svg"
cp linux/packaging/klip.svg "$APPDIR/usr/share/icons/hicolor/scalable/apps/klip.svg"
TOOL=${APPIMAGETOOL:-/opt/appimagetool/appimagetool-x86_64.AppImage}
if [ ! -x "$TOOL" ]; then
  mkdir -p "$(dirname "$TOOL")"
  curl -fsSL -o "$TOOL" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$TOOL"
fi
ARCH=x86_64 "$TOOL" --appimage-extract-and-run --no-appstream "$APPDIR" "$OUT/Klip-$V-x86_64.AppImage" >"$BUILD/appimagetool.log" 2>&1 \
  || { cat "$BUILD/appimagetool.log"; exit 1; }

# .deb
DEB="$BUILD/deb"
mkdir -p "$DEB/DEBIAN" "$DEB/usr/lib/klip" "$DEB/usr/bin" "$DEB/usr/share/applications" "$DEB/usr/share/icons/hicolor/scalable/apps"
cp -r "$BUILD/linux/." "$DEB/usr/lib/klip/"
ln -s ../lib/klip/klip "$DEB/usr/bin/klip"
cp linux/packaging/klip.desktop "$DEB/usr/share/applications/klip.desktop"
cp linux/packaging/klip.svg "$DEB/usr/share/icons/hicolor/scalable/apps/klip.svg"
SIZE=$(du -sk "$DEB/usr" | cut -f1)
cat > "$DEB/DEBIAN/control" <<EOF
Package: klip
Version: $V
Architecture: amd64
Maintainer: Klip <klip@users.noreply.github.com>
Installed-Size: $SIZE
Depends: libx11-6, libxfixes3, libice6, libsm6, libfontconfig1, xclip | wl-clipboard
Recommends: wl-clipboard, xclip, avahi-utils, libnotify-bin, libsecret-tools
Section: utils
Priority: optional
Homepage: https://github.com/enekor/klip
Description: Sincronizador local de portapapeles
 Comparte el portapapeles (texto y enlaces) entre Windows, Android y Linux
 dentro de tu red local, sin nube. Mensajes firmados y cifrados de extremo a
 extremo, emparejamiento por PIN y grupo automático.
EOF
dpkg-deb --build --root-owner-group "$DEB" "$OUT/klip_${V}_amd64.deb" >/dev/null

(cd "$OUT" && sha256sum -- * > SHA256SUMS.txt)
echo "-- Done:"
ls -la "$OUT"
