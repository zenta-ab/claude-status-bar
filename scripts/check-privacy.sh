#!/bin/sh
# macOS/Linux counterpart of scripts/check-privacy.ps1. Fails if files about to be committed
# contain personal data. This repo is public, and agent-written code has twice leaked a real
# email address and real account UUIDs into docs and tests.
#
#   ./scripts/check-privacy.sh            Check files staged for commit (what the pre-commit hook runs)
#   ./scripts/check-privacy.sh --all      Check every tracked file
#   ./scripts/check-privacy.sh --install  Install the pre-commit hook for this clone
#
# The rules are kept deliberately identical to check-privacy.ps1, with one addition the
# PowerShell version cannot make: macOS/Linux home directories are /Users/<name> and
# /home/<name>, which the Windows-only `<drive>:\Users\<name>` pattern never matched. A commit
# made from a Mac was therefore unprotected in both directions -- the hook itself also only
# ever invoked powershell, which is not installed on a stock Mac.
set -eu

REPO=$(git rev-parse --show-toplevel)
MODE=staged
case "${1:-}" in
    --all) MODE=all ;;
    --install) MODE=install ;;
    "") ;;
    *) echo "usage: $0 [--all|--install]" >&2; exit 2 ;;
esac

if [ "$MODE" = install ]; then
    git -C "$REPO" config core.hooksPath .githooks
    echo "Pre-commit hook installed (core.hooksPath = .githooks)."
    exit 0
fi

if ! command -v python3 >/dev/null 2>&1; then
    echo "check-privacy: python3 not found; cannot run the content scan." >&2
    exit 1
fi

# The scanner is built with a QUOTED heredoc rather than passed as `python3 -c '...'`.
# A single-quoted shell string ends at the first apostrophe, so one ordinary English possessive
# in a comment silently turns the rest of the program into shell code -- which is exactly what
# happened: a line reading "the organisation-s public contact address" (with a real apostrophe)
# broke this script for every macOS user, while Windows, which runs the .ps1, saw nothing.
# A quoted heredoc cannot be terminated by any character in the body.
SCANNER=$(cat <<'PYSRC'
import os, re, sys


repo = sys.argv[1]

# What counts as a placeholder rather than personal data.
#   - any address at a reserved example domain (RFC 2606) or a noreply address
#   - the organisation's public contact address, which every commit is authored as
#   - any UUID whose every dash-separated group is one repeated character
#   - a home directory whose name is an obvious stand-in
ALLOWED_EMAIL = re.compile(r"@(example|invalid|test)\.(com|org|net)$|@localhost$|noreply|^info@zenta\.se$")
ALLOWED_WIN_PATH = re.compile(r"(?i)\\Users\\(someone|user|username|test|example)$")
ALLOWED_POSIX_PATH = re.compile(r"^/(Users|home)/(someone|user|username|test|example)$")

PATTERNS = [
    ("email address", re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+[.][A-Za-z]{2,}")),
    ("UUID",          re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")),
    ("user path",     re.compile(r"[A-Za-z]:\\Users\\[A-Za-z0-9._-]+")),
    ("home path",     re.compile(r"/(?:Users|home)/[A-Za-z0-9._-]+")),
    ("OAuth token",   re.compile(r"sk-ant-[A-Za-z0-9]")),
]

# Verbatim third-party review output and design previews are already vetted.
SKIP = re.compile(r"^(docs/reviews/|docs/design-previews/)")

def placeholder_uuid(u):
    """11111111-1111-4111-8111-111111111111 and aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee are
    placeholders: one repeated character per group, allowing for the version/variant nibble
    that makes a UUID well-formed. Anything else is treated as real."""
    groups = u.split("-")
    if len(groups) != 5:
        return False
    for i, g in enumerate(groups):
        if i in (2, 3):
            g = g[1:]          # skip the version / variant nibble
        if g and any(c != g[0] for c in g):
            return False
    return True

def allowed(kind, hit):
    if kind == "email address":
        return bool(ALLOWED_EMAIL.search(hit))
    if kind == "UUID":
        return placeholder_uuid(hit)
    if kind == "user path":
        return bool(ALLOWED_WIN_PATH.search(hit))
    if kind == "home path":
        return bool(ALLOWED_POSIX_PATH.match(hit))
    return False

findings, checked = [], 0
for name in (l.strip() for l in sys.stdin):
    if not name or SKIP.match(name):
        continue
    full = os.path.join(repo, name)
    if not os.path.isfile(full):
        continue
    try:
        if os.path.getsize(full) > 2 * 1024 * 1024:
            continue
        with open(full, encoding="utf-8", errors="replace") as fh:
            lines = fh.readlines()
    except OSError:
        continue
    checked += 1
    for n, line in enumerate(lines, 1):
        for kind, rx in PATTERNS:
            for m in rx.finditer(line):
                if not allowed(kind, m.group(0)):
                    findings.append((name, n, kind, m.group(0)))

if not findings:
    print(f"check-privacy: clean, no personal data in {checked} files.")
    sys.exit(0)

print("")
print("check-privacy: BLOCKED -- personal data in files staged for commit:")
for f, n, kind, hit in findings:
    print(f"  {f}:{n}  {kind}: {hit}")
print("")
print("Replace with placeholders (alex@example.com, 11111111-1111-4111-8111-111111111111,")
print("/Users/someone) or commit with --no-verify if you are certain.")
sys.exit(1)
PYSRC
)

# The file list arrives on stdin from this pipe; the heredoc above was consumed at assignment
# time and does not compete for it.
if [ "$MODE" = all ]; then
    git -C "$REPO" ls-files
else
    git -C "$REPO" diff --cached --name-only --diff-filter=ACM
fi | MODE="$MODE" python3 -c "$SCANNER" "$REPO"
