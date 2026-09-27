#!/bin/zsh
# Builds OpenTypeless and installs it as the ONE copy on this Mac: /Applications/OpenTypeless.app.
#
#   scripts/build-app.sh
#
# Keeping the system tidy:
#   • The .app is assembled in a hidden staging folder (never indexed by Spotlight), moved into
#     /Applications, and the staging copy is deleted — no second "OpenTypeless" in Launchpad/Spotlight.
#   • Any other registered copy of the app (old build folders etc.) is unregistered from LaunchServices,
#     and deleted if it lives inside this project.
#   • If the code signature changed (ad-hoc signing changes on every build), the stale Accessibility /
#     Microphone entries for the old signature are reset, so System Settings shows exactly one
#     OpenTypeless entry instead of several dead ones. Run scripts/create-signing-cert.sh once to get a
#     stable signature and keep permissions across updates.
set -euo pipefail
cd "$(dirname "$0")/.."

APP_NAME="OpenTypeless"
BUNDLE_ID="local.opentypeless.app"
VERSION="1.0.0"
IDENTITY_NAME="OpenTypeless Dev"
INSTALLED="/Applications/$APP_NAME.app"
STAGING_DIR=".build/app-staging"
STAGED="$STAGING_DIR/$APP_NAME.app"
LSREGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
PROJECT_DIR="$(pwd)"

echo "▸ Compiling (release)…"
swift build -c release --product "$APP_NAME"
BIN="$(swift build -c release --show-bin-path)/$APP_NAME"

echo "▸ Assembling"
rm -rf "$STAGING_DIR"
mkdir -p "$STAGED/Contents/MacOS" "$STAGED/Contents/Resources"
touch "$STAGING_DIR/.metadata_never_index"
cp "$BIN" "$STAGED/Contents/MacOS/$APP_NAME"
cp Resources/AppIcon.icns "$STAGED/Contents/Resources/"

cat > "$STAGED/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>$APP_NAME</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key><string>$APP_NAME</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$(date +%Y%m%d%H%M)</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundleDevelopmentRegion</key><string>en</string>
    <key>CFBundleLocalizations</key><array><string>en</string><string>zh-Hans</string></array>
    <key>LSMinimumSystemVersion</key><string>26.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSMicrophoneUsageDescription</key>
    <string>OpenTypeless records your voice to turn it into text.</string>
</dict>
</plist>
PLIST

if security find-identity -v -p codesigning | grep -q "$IDENTITY_NAME"; then
    echo "▸ Signing with \"$IDENTITY_NAME\" (stable — permissions survive updates)"
    codesign --force --deep --sign "$IDENTITY_NAME" "$STAGED"
else
    echo "▸ Signing ad-hoc (run scripts/create-signing-cert.sh once to keep permissions across updates)"
    codesign --force --deep --sign - "$STAGED"
fi

requirement() { codesign -d -r- "$1" 2>/dev/null | sed -n 's/^#* *designated => //p'; }
OLD_REQ=""
[[ -d "$INSTALLED" ]] && OLD_REQ="$(requirement "$INSTALLED")"
NEW_REQ="$(requirement "$STAGED")"

echo "▸ Replacing $INSTALLED"
osascript -e "quit app id \"$BUNDLE_ID\"" >/dev/null 2>&1 || true
for _ in {1..20}; do pgrep -x "$APP_NAME" >/dev/null || break; sleep 0.2; done
pkill -x "$APP_NAME" 2>/dev/null || true
rm -rf "$INSTALLED"
mv "$STAGED" "$INSTALLED"
rm -rf "$STAGING_DIR"

if [[ -n "$OLD_REQ" && "$OLD_REQ" != "$NEW_REQ" ]]; then
    echo "▸ Signature changed — clearing stale privacy entries (macOS will ask once more)"
    for service in Accessibility Microphone ListenEvent; do
        tccutil reset "$service" "$BUNDLE_ID" >/dev/null 2>&1 || true
    done
fi

echo "▸ Cleaning up other copies"
"$LSREGISTER" -dump 2>/dev/null \
    | sed -n "s/^path: *\(.*\/$APP_NAME\.app\) (0x[0-9a-f]*)$/\1/p" | sort -u \
    | while read -r stale; do
        [[ "$stale" == "$INSTALLED" ]] && continue
        "$LSREGISTER" -u "$stale" >/dev/null 2>&1 || true
        if [[ "$stale" == "$PROJECT_DIR"/* && -d "$stale" ]]; then
            rm -rf "$stale"
            echo "  removed $stale"
        else
            echo "  unregistered $stale"
        fi
    done
rm -rf build   # v0.1 build output
"$LSREGISTER" -f "$INSTALLED"

open "$INSTALLED"
echo "✓ Installed $APP_NAME $VERSION → $INSTALLED"
