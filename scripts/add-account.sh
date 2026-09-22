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

# Reads oauthAccount from a config directory's .claude.json and prints, tab-separated:
#   <accountUuid>\t<organizationUuid>\t<organizationName>
# Empty output when the directory holds no login. Pass "" for Claude Code's own default login.
read_identity() {
    python3 - "${1:-}" <<'PY'
import json, os, sys
config_dir = sys.argv[1]
path = os.path.expanduser("~/.claude.json") if not config_dir \
    else os.path.join(config_dir, ".claude.json")
try:
    oauth = json.load(open(path)).get("oauthAccount") or {}
except Exception:
    raise SystemExit(0)
if not oauth.get("accountUuid"):
    raise SystemExit(0)
print("\t".join([oauth.get("accountUuid") or "",
                 oauth.get("organizationUuid") or "",
                 oauth.get("organizationName") or ""]))
PY
}

# Which organization a login landed in, in words you recognise from claude.ai -- the one thing
# that tells a personal plan and a Team/Enterprise login on the same email apart. The plan itself
# (max, team, ...) only arrives with get_usage, so this script cannot show it. Anthropic's
# auto-generated "<email>'s Organization" is the personal one (AccountLabel's ladder skips it for
# exactly this reason).
format_organization() {
    name=$(printf '%s' "${1:-}" | cut -f3)
    case "$name" in
        "") echo "an unnamed organization" ;;
        *"'s Organization") echo "your personal organization" ;;
        *) echo "the organization \"$name\"" ;;
    esac
}

# Mirrors AccountIdentity.stateKey (docs/multi-account.md, "Identity guard"): accountUuid alone
# identifies the PERSON, not the plan, so accountUuid+organizationUuid is the actual identity.
# One person's Team seat and personal Max seat share an accountUuid and differ only here.
state_key_of() {
    account=$(printf '%s' "${1:-}" | cut -f1)
    org=$(printf '%s' "${1:-}" | cut -f2)
    [ -z "$account" ] && return 0
    if [ -n "$org" ]; then echo "${account}_${org}"; else echo "$account"; fi
}

# Prints "<index>\t<slot-or-empty>" for the first already-registered account whose login is the
# SAME as $1's state key, or nothing. This is what makes the organization line worth printing:
# without it, logging into the same organization twice silently produces two icons for one quota.
find_duplicate_of() {
    target="$1"
    [ -z "$target" ] && return 0
    ensure_config
    python3 - "$ACCOUNTS_JSON" "$target" <<'PY'
import json, os, sys
path, target = sys.argv[1], sys.argv[2]
def key(config_dir):
    probe = os.path.expanduser("~/.claude.json") if not config_dir \
        else os.path.join(config_dir, ".claude.json")
    try:
        oauth = json.load(open(probe)).get("oauthAccount") or {}
    except Exception:
        return None
    account = oauth.get("accountUuid")
    if not account:
        return None
    org = oauth.get("organizationUuid")
    return f"{account}_{org}" if org else account
cfg = json.load(open(path))
for index, account in enumerate(cfg.get("accounts", [])):
    if key(account.get("configDir")) == target:
        d = account.get("configDir") or ""
        slot = ""
        parts = d.rstrip("/").split("/")
        if len(parts) >= 2 and parts[-1] == "config":
            slot = parts[-2]
        print(f"{index}\t{slot}")
        break
PY
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
        reg_dir=$(canonical "$APP_SUPPORT/accounts/$2/config")
        reg_identity=$(read_identity "$reg_dir")
        if [ -n "$reg_identity" ]; then
            reg_dup=$(find_duplicate_of "$(state_key_of "$reg_identity")")
            if [ -n "$reg_dup" ]; then
                echo "This login ($(format_organization "$reg_identity")) is already tracked as"
                echo "account [$(printf '%s' "$reg_dup" | cut -f1)] — not registering a duplicate."
                exit 1
            fi
            echo "Registering $(format_organization "$reg_identity")."
        fi
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

A browser window will open for you to log in. If your login belongs to more than one
organization — say a personal plan and a Team — choose the one you want THIS slot to track
there. Picking the same account AND organization as an existing entry gives you two icons
for one quota, so this script checks and refuses that.

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

new_identity=$(read_identity "$dir")
duplicate=$(find_duplicate_of "$(state_key_of "$new_identity")")

if [ -n "$duplicate" ]; then
    dup_index=$(printf '%s' "$duplicate" | cut -f1)
    echo ""
    echo "You logged in to $(format_organization "$new_identity"), which is the SAME login as"
    echo "account [$dup_index] — not adding it as a new account."
    echo ""
    echo "Nothing was registered, but the new login directory is left on disk at:"
    echo "  $dir"
    echo ""
    echo "To use it, log in there with a different account or organization:"
    echo "  CLAUDE_CONFIG_DIR=\"$dir\" CLAUDE_SECURESTORAGE_CONFIG_DIR=\"$dir\" claude /login"
    echo "...choosing the other account or organization, then /exit and re-run:"
    echo "  $0 --register $slot"
    exit 1
fi

echo ""
echo "Logged in to $(format_organization "$new_identity")."
register_slot "$slot"
echo ""
list_accounts
echo ""
echo "Restart the status bar app to pick up the new account."
