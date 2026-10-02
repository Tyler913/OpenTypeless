#!/bin/zsh
# Builds OpenTypeless and installs it as the ONE copy on this Mac: /Applications/OpenTypeless.app.
#
#   scripts/build-app.sh             build and install into /Applications
#   scripts/build-app.sh --package   build the distributables in dist/ (doesn't touch /Applications), for this Mac's
#                                    processor: OpenTypeless-<version>-macOS-arm64 (Apple silicon) or -macOS-x64 (Intel)
#                                      .dmg   what people download: drag the app onto Applications
#                                      .zip   what the in-app updater and Homebrew install
#
# The zip is signed with $OPENTYPELESS_SIGNING_IDENTITY when it's set (the release certificate, set up by CI, see
# scripts/create-release-cert.sh), and ad hoc otherwise. A stable certificate lets in-app updates keep the
# Accessibility and Microphone permissions, and lets the updater check that an update was signed by the same key.
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
VERSION="1.4.2"
IDENTITY_NAME="OpenTypeless Dev"
INSTALLED="/Applications/$APP_NAME.app"
STAGING_DIR=".build/app-staging"
STAGED="$STAGING_DIR/$APP_NAME.app"
LSREGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
PROJECT_DIR="$(pwd)"
PACKAGE=false
[[ "${1:-}" == "--package" ]] && PACKAGE=true
# The release names Intel builds x64, like the Windows ones.
case "$(uname -m)" in
    arm64) PLATFORM="macOS-arm64" ;;
    x86_64) PLATFORM="macOS-x64" ;;
    *) echo "Unsupported processor: $(uname -m)" >&2; exit 1 ;;
esac

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
    <key>CFBundleLocalizations</key><array><string>en</string><string>zh-Hans</string><string>ja</string><string>ko</string><string>es</string><string>pt-BR</string><string>fr</string><string>de</string><string>ru</string></array>
    <key>LSMinimumSystemVersion</key><string>26.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSMicrophoneUsageDescription</key>
    <string>OpenTypeless records your voice to turn it into text.</string>
    <key>NSSpeechRecognitionUsageDescription</key>
    <string>With Live preview on, OpenTypeless shows what you say as you talk, recognised on this Mac.</string>
</dict>
</plist>
PLIST

if $PACKAGE; then
    if [[ -n "${OPENTYPELESS_SIGNING_IDENTITY:-}" ]]; then
        echo "▸ Signing with the release certificate"
        codesign --force --deep --sign "$OPENTYPELESS_SIGNING_IDENTITY" "$STAGED"
    else
        echo "▸ Signing ad-hoc for distribution"
        codesign --force --deep --sign - "$STAGED"
    fi
    codesign --verify --deep --strict "$STAGED"
    codesign -d -r- "$STAGED" 2>/dev/null | sed -n 's/^#* *designated => /  Requirement: /p'
    mkdir -p dist
    ZIP="dist/$APP_NAME-$VERSION-$PLATFORM.zip"
    DMG="dist/$APP_NAME-$VERSION-$PLATFORM.dmg"
    rm -f "$ZIP" "$DMG"
    ditto -c -k --sequesterRsrc --keepParent "$STAGED" "$ZIP"
    # The disk image opens to the app next to a link to Applications, to drag it onto.
    DMG_ROOT="$STAGING_DIR/dmg"
    mkdir -p "$DMG_ROOT"
    ditto "$STAGED" "$DMG_ROOT/$APP_NAME.app"
    ln -s /Applications "$DMG_ROOT/Applications"
    hdiutil create -quiet -volname "$APP_NAME $VERSION" -srcfolder "$DMG_ROOT" -fs HFS+ -format UDZO -ov "$DMG"
    hdiutil verify -quiet "$DMG"
    if [[ -n "${OPENTYPELESS_SIGNING_IDENTITY:-}" ]]; then
        codesign --force --sign "$OPENTYPELESS_SIGNING_IDENTITY" "$DMG"
    fi
    "$LSREGISTER" -u "$STAGED" >/dev/null 2>&1 || true
    rm -rf "$STAGING_DIR"
    for file in "$ZIP" "$DMG"; do
        echo "✓ Packaged $file ($(du -h "$file" | cut -f1 | xargs))"
        echo "  SHA-256: $(shasum -a 256 "$file" | cut -d' ' -f1)"
    done
    exit 0
elif security find-identity -v -p codesigning | grep -q "$IDENTITY_NAME"; then
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
