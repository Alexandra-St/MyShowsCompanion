#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PROJECT="$ROOT/src/MyShowsCompanion.Desktop/MyShowsCompanion.Desktop.csproj"
ARTIFACTS="$ROOT/artifacts/macos"
PUBLISH="$ARTIFACTS/osx-arm64-publish"
ICONSET="$ARTIFACTS/MyShowsCompanion.iconset"
APP="$ROOT/MyShows Companion.app"

if [[ -e "$APP" ]]; then
  echo "Refusing to overwrite existing app bundle: $APP" >&2
  exit 1
fi

mkdir -p "$ARTIFACTS"
dotnet publish "$PROJECT" -c Release -r osx-arm64 --self-contained true \
  -p:UseAppHost=true -p:Version=1.0.0 -o "$PUBLISH"

rm -rf "$ICONSET"
swift "$ROOT/packaging/macos/create-placeholder-icon.swift" "$ICONSET"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
cp "$ROOT/packaging/macos/Info.plist" "$APP/Contents/Info.plist"
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/MyShowsCompanion.icns"
chmod +x "$APP/Contents/MacOS/MyShowsCompanion.Desktop"

echo "Created: $APP"
