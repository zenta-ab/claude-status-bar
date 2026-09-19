#!/bin/sh
# macOS counterpart of scripts/add-account.ps1 (docs/multi-account.md).
#
# Adds a second (third, …) Claude account to the status bar by creating a dedicated
# CLAUDE_CONFIG_DIR and running an interactive `claude` login inside it.
#
#   ./scripts/add-account.sh            add the next free slot
#   ./scripts/add-account.sh --register 1   register slot 1 that was already logged in
#   ./scripts/add-account.sh --list     show what is configured
#
# WHY A FRESH LOGIN AND NOT A COPY. Claude Code rotates its OAuth refresh token, so a copied
# login is invalidated within minutes — observed on Windows as 401s and `rate_limits: null`,
# i.e. a permanent "Kan inte läsa kvoten". This script therefore only ever creates a new
# directory and logs into it directly.
#
# WHY THE PATH SPELLING MATTERS. Claude Code derives the login's Keychain service name from
# `sha256(NFC(configDir))` (docs/mac-port.md §2). A trailing slash or a symlinked spelling is a
# *different account* as far as it is concerned, and the app would read it as logged out. The
# path is canonicalised once, here, and the same string is written to accounts.json.
set -eu

APP_SUPPORT="$HOME/Library/Application Support/ClaudeStatusBar"
ACCOUNTS_JSON="$APP_SUPPORT/accounts.json"

canonical() {
    # Absolute, symlinks resolved, no trailing slash, NFC — the same normalisation
    # ChildProcessSpec.canonicalConfigDirectory performs.
    python3 -c '
import os, sys, unicodedata
p = os.path.realpath(os.path.expanduser(sys.argv[1])).rstrip("/") or "/"
sys.stdout.write(unicodedata.normalize("NFC", p))
' "$1"
}

ensure_config() {
    [ -f "$ACCOUNTS_JSON" ] && return 0
    mkdir -p "$APP_SUPPORT"
    cat > "$ACCOUNTS_JSON" <<'JSON'
{
  "accounts" : [
    {
      "configDir" : null,
      "enabled" : true,
      "label" : null
    }
  ],
  "displayMode" : "perAccount",
  "maxIcons" : 3
}
JSON
    echo "created $ACCOUNTS_JSON with the default single account"
}

list_accounts() {
    ensure_config
    python3 - "$ACCOUNTS_JSON" <<'PY'
import json, os, sys
cfg = json.load(open(sys.argv[1]))
print(f"displayMode: {cfg.get('displayMode')}   maxIcons: {cfg.get('maxIcons')}")
for i, a in enumerate(cfg.get("accounts", [])):
    d = a.get("configDir")
    where = "(Claude Code's own default login)" if not d else d.replace(os.path.expanduser("~"), "~")
    # Is that directory actually logged in?
    probe = os.path.expanduser("~/.claude.json") if not d else os.path.join(d, ".claude.json")
    state = "not logged in"
    try:
        oauth = json.load(open(probe)).get("oauthAccount") or {}
        if oauth.get("accountUuid"):
            state = f"logged in ({oauth.get('organizationType') or 'unknown plan'})"
    except Exception:
        pass
    label = a.get("label") or "(from your Anthropic data)"
    print(f"  [{i}] {where}\n      label: {label}   enabled: {a.get('enabled')}   {state}")
PY
}

register_slot() {
    slot="$1"
    dir=$(canonical "$APP_SUPPORT/accounts/$slot/config")
    ensure_config
    python3 - "$ACCOUNTS_JSON" "$dir" <<'PY'
import json, sys
path, config_dir = sys.argv[1], sys.argv[2]
cfg = json.load(open(path))
accounts = cfg.setdefault("accounts", [])
if any(a.get("configDir") == config_dir for a in accounts):
    print(f"already registered: {config_dir}")
else:
    accounts.append({"configDir": config_dir, "label": None, "enabled": True})
    json.dump(cfg, open(path, "w"), indent=2, sort_keys=True)
    print(f"registered {config_dir}")
PY
}

case "${1:-}" in
    --list) list_accounts; exit 0 ;;
    --register)
        [ $# -ge 2 ] || { echo "usage: $0 --register <slot>" >&2; exit 2; }
        register_slot "$2"; list_accounts; exit 0 ;;
    "") ;;
    *) echo "usage: $0 [--list | --register <slot>]" >&2; exit 2 ;;
esac

command -v claude >/dev/null 2>&1 || {
    echo "claude is not on PATH. Install Claude Code first." >&2; exit 1; }

ensure_config

# Next free slot.
slot=1
while [ -d "$APP_SUPPORT/accounts/$slot/config" ]; do slot=$((slot + 1)); done
dir=$(canonical "$APP_SUPPORT/accounts/$slot/config")
mkdir -p "$dir"
dir=$(canonical "$dir")   # re-canonicalise now that it exists, so symlinks resolve

cat <<EOF

Adding account slot $slot.

  config directory : $dir

A browser window will open for you to log in. Log in with the account you want THIS slot to
track — a different one from your existing login, or you will end up tracking the same account
twice.

Your existing login is untouched: Claude Code stores each config directory's credentials under
its own Keychain service name, derived from the directory path.

EOF

printf "Press Return to start the login, or Ctrl-C to abort. "
read -r _

# The exit code of `claude /login` is NOT a signal about whether the login worked: it starts a
# full interactive session, and quitting that session (or Ctrl-C) exits non-zero even though the
# browser login completed. Gating registration on it silently skipped registration for a login
# that had in fact succeeded. So: run it, ignore the status, and judge by the OUTCOME on disk.
CLAUDE_CONFIG_DIR="$dir" CLAUDE_SECURESTORAGE_CONFIG_DIR="$dir" claude /login || true

if ! python3 -c '
import json, sys
try:
    oauth = json.load(open(sys.argv[1] + "/.claude.json")).get("oauthAccount") or {}
    sys.exit(0 if oauth.get("accountUuid") else 1)
except Exception:
    sys.exit(1)
' "$dir"; then
    echo ""
    echo "That directory has no oauthAccount, so the login did not take."
    echo "Nothing was registered. Re-run this script to try again, or if you are sure you did"
    echo "log in, register it manually with:"
    echo "  $0 --register $slot"
    exit 1
fi

register_slot "$slot"
echo ""
list_accounts
echo ""
echo "Restart the status bar app to pick up the new account."
