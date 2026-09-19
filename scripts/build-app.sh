#!/bin/sh
# Builds ClaudeStatusBar.app from the SwiftPM package and, optionally, installs it.
# The macOS counterpart of scripts/install.ps1.
#
#   ./scripts/build-app.sh              build into src/Mac/.build/ClaudeStatusBar.app
#   ./scripts/build-app.sh --install    also copy it to ~/Applications and launch it
#   ./scripts/build-app.sh --login-item also register it to start at login
#   ./scripts/build-app.sh --uninstall  remove the app and unregister the login item
#
# WHY AN APP BUNDLE AND NOT THE BARE BINARY. A menu-bar app run from a terminal dies with the
# terminal session, cannot be a login item, and has no stable identity for macOS to attach
# permissions to. A bundle fixes all three. It is also what makes `LSUIElement` work, which is
# what keeps the app out of the Dock and the ⌘-Tab switcher — a status-bar app has no business
# in either.
set -eu

REPO=$(cd "$(dirname "$0")/.." && pwd)
PACKAGE="$REPO/src/Mac"
APP_NAME="ClaudeStatusBar"
BUNDLE_ID="se.zenta.claudestatusbar"
BUILD_DIR="$PACKAGE/.build"
APP="$BUILD_DIR/$APP_NAME.app"
INSTALL_DIR="$HOME/Applications"

mode=build
case "${1:-}" in
    --install) mode=install ;;
    --login-item) mode=login ;;
    --uninstall) mode=uninstall ;;
    "") ;;
    *) echo "usage: $0 [--install|--login-item|--uninstall]" >&2; exit 2 ;;
esac

if [ "$mode" = uninstall ]; then
    osascript -e "tell application \"System Events\" to delete login item \"$APP_NAME\"" 2>/dev/null || true
    pkill -f "$APP_NAME.app/Contents/MacOS/" 2>/dev/null || true
    rm -rf "$INSTALL_DIR/$APP_NAME.app"
    echo "Removed $INSTALL_DIR/$APP_NAME.app and any login item."
    exit 0
fi

echo "Building release binaries…"
(cd "$PACKAGE" && swift build -c release)

BIN="$PACKAGE/.build/release/statusbar"
[ -x "$BIN" ] || { echo "build produced no statusbar binary" >&2; exit 1; }

echo "Assembling $APP_NAME.app…"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/$APP_NAME"

VERSION=$(cd "$REPO" && git describe --tags --always 2>/dev/null || echo "0.1.0")

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>Claude Status Bar</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key><string>$APP_NAME</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>13.0</string>
    <!-- A status-bar app belongs in neither the Dock nor the ⌘-Tab switcher. -->
    <key>LSUIElement</key><true/>
</dict>
</plist>
PLIST

# Ad-hoc signing. Enough for local use: it gives the bundle a stable code identity so macOS can
# attach permissions to it and so it is not killed on launch. It is NOT notarised — see the note
# at the end for what that means if this is ever handed to someone else.
echo "Signing (ad-hoc)…"
codesign --force --deep --sign - "$APP" 2>/dev/null \
    || echo "  codesign failed; the app will still run locally, but macOS may re-prompt on updates."

echo "Built $APP"

if [ "$mode" = build ]; then
    echo ""
    echo "Run it with:            open \"$APP\""
    echo "Install it with:        $0 --install"
    echo "Start it at login with: $0 --login-item"
    exit 0
fi

echo "Installing to ${INSTALL_DIR}…"
mkdir -p "$INSTALL_DIR"
pkill -f "$APP_NAME.app/Contents/MacOS/" 2>/dev/null || true
rm -rf "$INSTALL_DIR/$APP_NAME.app"
cp -R "$APP" "$INSTALL_DIR/"

if [ "$mode" = login ]; then
    # `SMAppService` is the modern API, but it requires the app to register itself from inside
    # its own bundle, which a shell script cannot do on its behalf. A System Events login item
    # is the equivalent that IS scriptable, and it is what the user can also see and remove in
    # System Settings → General → Login Items.
    osascript -e "tell application \"System Events\" to delete login item \"$APP_NAME\"" 2>/dev/null || true
    osascript -e "tell application \"System Events\" to make login item at end with properties {path:\"$INSTALL_DIR/$APP_NAME.app\", hidden:true, name:\"$APP_NAME\"}" >/dev/null
    echo "Registered as a login item (visible in System Settings → General → Login Items)."
fi

open "$INSTALL_DIR/$APP_NAME.app"
echo ""
echo "Running. The icons are on the right-hand side of the menu bar."
echo ""
echo "NOTE ON DISTRIBUTION: this bundle is ad-hoc signed, not notarised. That is fine on the"
echo "machine that built it. Copied to someone else's Mac, Gatekeeper will refuse it until they"
echo "right-click → Open, or until the app is signed with a Developer ID and notarised."
