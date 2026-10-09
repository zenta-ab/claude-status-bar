# Multiple accounts

One person can hold several Claude plans at once — a personal Pro/Max, a Team seat, an Enterprise
seat. This is how the app tracks more than one, without hardcoding anything about any particular
organisation: **every label comes from the user's own Anthropic data.**

**Every tracked account owns its own login**, in a folder this app creates and owns. The app never
follows the login the user happens to have in their terminal or VS Code, never reads token values,
and never touches a folder it does not own. The design decisions and what was rejected are in
`docs/reviews/own-login-design.md`.

## What Claude Code gives us

- **One login per config directory.** The default is `%USERPROFILE%\.claude`, overridable with
  `CLAUDE_CONFIG_DIR`. Verified: a child started with an empty config dir returns no
  `subscription_type` and no `rate_limits`, while the default one returns the live account. So
  *N accounts = N config directories = N child processes.*
- **`.claude.json` → `oauthAccount`** carries the identity and the label material: `accountUuid`,
  `organizationUuid`, `organizationName`, `organizationType`, `organizationRole`, `seatTier`,
  `billingType`, `emailAddress`, `displayName`, `fullName`. **Where that file lives depends on
  the config directory** (verified on a real, already-logged-in installation): for the DEFAULT
  account, Claude Code keeps it at `%USERPROFILE%\.claude.json` — a *sibling* of
  `%USERPROFILE%\.claude`, not inside it. When `CLAUDE_CONFIG_DIR` is set, Claude Code relocates
  the *whole* state directory there, `.claude.json` included, so for every app-owned slot the
  identity is at `<slot>\config\.claude.json`. (The app no longer reads the default location at
  all: it has no default-login account.)
- **`get_usage` → `subscription_type`** (`max`, `team`, …) for the account that child is logged into.
- **`get_usage` with no usable login** answers, successfully and well-formed, with
  `{"subscription_type": null, "rate_limits_available": false, "rate_limits": null}` — see
  "NeedsLogin".
- **`claude auth status --json`** prints `loggedIn`, `authMethod` (`claude.ai` for a subscription
  login, `none` when logged out) and the account's email among other things, and exits non-zero
  when logged out. The app reads only the first two and never logs the output.
- **`claude auth login --claudeai`** logs a subscription account into whatever config directory the
  environment points at, through the browser.

## Slots: the app owns the logins

A **slot** is a folder under `%LOCALAPPDATA%\ClaudeStatusBar\accounts\`:

```
accounts\<id>\config\       the Claude config directory (the login lives here)
accounts\<id>\slot.json     { "state": "pending" | "active" | "retired", "createdUtc": "...", "schema": 1 }
```

`slot.json` carries no identity and no email. States:

| state | meaning |
|---|---|
| `pending` | a login is in progress, or was interrupted |
| `active` | a tracked account |
| `retired` | being deleted; never adopted, deletion is retried until it succeeds |

**Ids.** New slots get 8 random lowercase hex characters, regenerated if any folder of that name
exists (a retired or half-deleted one included), so an id is never reused. The numeric folders earlier
versions created (`1`, `4`, ...) stay valid ids.

**Path guard** (`Config/SlotStore.cs`, one function for everything that logs in, deletes or spawns
with write access): a path is accepted only if it is *exactly* `<accountsRoot>\<id>\config` (or
`<accountsRoot>\<id>` for a delete), where `<accountsRoot>` is the canonical
`%LOCALAPPDATA%\ClaudeStatusBar\accounts`, `<id>` matches `^([0-9]+|[0-9a-f]{8})\z`, and the root,
the slot folder and the config folder are not reparse points (junctions, symlinks). The comparison
is on the literal string, never on a normalised form of it, so `..`, doubled or trailing
separators, forward slashes, `\\?\` and UNC spellings, trailing dots and spaces, alternate data
streams, a different root, and the user's own `%USERPROFILE%\.claude` are all simply "not that
shape". Whatever the guard returns is its own spelling of the path, never the caller's. A refusal
is logged and the operation does not happen. Parents of the accounts root are not checked: they are
the user's profile and may legitimately be redirected.

## accounts.json is metadata

`%LOCALAPPDATA%\ClaudeStatusBar\accounts.json`:

```jsonc
{
  "version": 2,
  "displayMode": "perAccount",   // or "binding": one icon for the account closest to its limit
  "maxIcons": 3,                 // perAccount only; accounts beyond this appear in the panel only
  "accounts": [
    { "slot": "a1b2c3d4", "label": null, "enabled": true }
  ]
}
```

An empty `accounts` list is valid (it is the state of a fresh install: see "Zero accounts"), and
`"accounts": null` means the same. Unknown fields round-trip. The slots are the source of truth; this
file only orders them, names them and switches them on and off.

**Migration from version 1** (no `version` field, `configDir` paths): an entry whose `configDir` --
first put into canonical form (full path, separators, trailing separator, `..`, 8.3 short names expanded
with `GetLongPathName`; `SlotStore.CanonicalizeLegacyPath`), so that how an older script happened to spell
a path never decides its fate -- passes the path guard (and whose folder exists) becomes `{ "slot": "<id>" }`, and if that folder has
no `slot.json` one is written as `active` — the user referenced the folder, so the user chose it.
Entries that fail the guard, and the old follower entry (`configDir: null`, "whatever I am logged
into"), are dropped and logged. Folders no version 1 entry referenced and that have no `slot.json`
are left alone: not adopted, not deleted. History of a dropped entry stays on disk keyed by identity
and resumes if the same identity is logged in again. An out-of-range `maxIcons` in a version 1 file is
clamped to 1 during migration instead of getting the whole file quarantined (a version 2 file with one is
still invalid).

## Reconciliation

On every start, and after every change, `Config/AccountReconciler.cs` makes the account list agree
with the folders:

- **`retired` folders** are deleted (a locked one is left for the next start) and never adopted.
- **`pending` folders** — a login interrupted between the browser and the save — are **adopted as
  `active`** if their config holds an `oauthAccount` that is not a duplicate of an active slot, and
  otherwise retired and deleted.
- **Entries** whose folder is missing or not `active`, or that are listed twice, are dropped.
- **`active` folders** the file does not list are appended, enabled. This is the recovery path for a
  lost or quarantined `accounts.json`: a malformed file is still renamed to
  `accounts.bad-<timestamp>.json` rather than overwritten, but the account set is then rebuilt from the
  active slots, so no login is ever orphaned.

Two rules keep reconciliation from doing damage. **Protected slots:** the pending slot a running login
console is writing into is passed to EVERY pass and left exactly as it is (it has no identity yet, so an
unprotected pass would retire and delete it under the running login). **Isolation:** each slot is handled
on its own; one that cannot be handled (say a config folder that is a junction, which the path guard
refuses) is logged and skipped, and everything else is still reconciled.

## Environment: one builder for every `claude` the app starts

`Data/ChildEnvironment.cs` is used for the polling child, `auth login` and `auth status`. It

- sets `CLAUDE_CONFIG_DIR` **and** `CLAUDE_SECURESTORAGE_CONFIG_DIR` to the slot's (guarded) config
  dir. The second outranks the first when Claude Code chooses where credentials live (`docs/mac-port.md`
  has the selector), so a stray inherited value would silently put every account in one store;
- **removes** every variable that makes Claude Code use some other credential, identity, provider or
  endpoint than the stored OAuth login;
- gives the child the slot's own agent folder (`%LOCALAPPDATA%\ClaudeStatusBar\agent\<id>`) as
  working directory, never a user repo.

The removal list was taken from the strings of the 2.1.292 binary (names that read as overriding
the stored login; their exact semantics were inferred from the names and surrounding code, not
exercised):

| group | variables |
|---|---|
| a token or key instead of the stored login | `CLAUDE_CODE_OAUTH_TOKEN`, `CLAUDE_CODE_OAUTH_REFRESH_TOKEN`, `CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR`, `CLAUDE_CODE_OAUTH_SCOPES`, `CLAUDE_CODE_OAUTH_CLIENT_ID`, `CLAUDE_CODE_SESSION_ACCESS_TOKEN`, `CLAUDE_SESSION_INGRESS_TOKEN_FILE`, `CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR`, `CLAUDE_CODE_WEBSOCKET_AUTH_FILE_DESCRIPTOR`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_API_KEY` |
| another provider or gateway | `CLAUDE_CODE_USE_BEDROCK`, `_VERTEX`, `_FOUNDRY`, `_MANTLE`, `_GATEWAY`, `CLAUDE_CODE_GATEWAY_TOKEN`, `CLAUDE_CODE_GATEWAY_TOKEN_FILE_DESCRIPTOR`, `ANTHROPIC_FOUNDRY_API_KEY`, `ANTHROPIC_FOUNDRY_AUTH_TOKEN`, `ANTHROPIC_AWS_API_KEY`, `AWS_BEARER_TOKEN_BEDROCK` |
| another endpoint, or extra headers | `ANTHROPIC_BASE_URL`, `ANTHROPIC_CUSTOM_HEADERS`, `CLAUDE_CODE_API_BASE_URL`, `CLAUDE_CODE_CUSTOM_OAUTH_URL`, `CLAUDE_LOCAL_OAUTH_API_BASE`, `_APPS_BASE`, `_CONSOLE_BASE` |
| Console / workload-identity profiles | `ANTHROPIC_PROFILE`, `ANTHROPIC_CONFIG_DIR`, `ANTHROPIC_FEDERATION_RULE_ID`, `ANTHROPIC_ORGANIZATION_ID`, `ANTHROPIC_IDENTITY_TOKEN`, `ANTHROPIC_IDENTITY_TOKEN_FILE`, `ANTHROPIC_ENVIRONMENT_KEY` |
| who the account is, and its plan | `CLAUDE_CODE_ACCOUNT_UUID`, `CLAUDE_CODE_ORGANIZATION_UUID`, `CLAUDE_CODE_USER_EMAIL`, `CLAUDE_CODE_SUBSCRIPTION_TYPE`, `CLAUDE_CODE_RATE_LIMIT_TIER` |
| auth handled by a host application | `CLAUDE_CODE_SDK_HAS_OAUTH_REFRESH`, `CLAUDE_CODE_SDK_HAS_HOST_AUTH_REFRESH`, `CLAUDE_CODE_HOST_AUTH_ENV_VAR`, `CLAUDE_CODE_HOST_CREDS_FILE`, `CLAUDE_BG_AUTH_SNAPSHOT_PATH`, `CLAUDE_BG_CLAIM_AUTH`, `CLAUDE_BG_PTY_AUTH`, `CLAUDE_BG_RV_AUTH`, `CLAUDE_BG_SOCKET_TOKENS_PATH` |

Why it matters in practice: an app started from inside a Claude Code session, an IDE, or a shell with
`ANTHROPIC_API_KEY` set would otherwise hand all of that to every child, and an "account" would quietly
report somebody else's quota.

## Login flow

Reached from the tray menu — **"Lägg till konto…"** at the top level of every icon's menu (the
zero-accounts icon included) and **"Konton ▸ <label> ▸ Logga in igen / Byt namn… / Ta bort…"** — and from the panel's
"Logga in igen" button for an account that needs a login. Only one flow runs at a time; the entries are
disabled while one does.

**Add, and re-login, always log into a NEW slot** (never into the folder being replaced):

1. Create a `pending` slot.
2. Open a visible console running `claude auth login --claudeai` with the environment builder,
   preceded by one printed line (Swedish): log in with the account you want and choose the right
   organisation in the browser; the window closes by itself. The console is a PowerShell process
   (`-EncodedCommand`, so the Swedish text and every path are quoted safely). It is deliberately NOT in a
   kill-on-close job and is never killed by the app: when `claude auth login` cold-starts the browser, the
   browser lives under the console, and ending the console would close the user's browser mid-login. The app
   waits for it to exit, with no timeout (closing the window gives up). If the app itself exits meanwhile, the
   console is left running and the pending slot is adopted by the next start if the login finished.
3. Run `claude auth status --json` (same environment, 20 s timeout). Only `loggedIn: true` with
   `authMethod: "claude.ai"` counts. Its raw output is never logged — it contains the email. **No answer**
   (a cold first `claude.exe` run can be slow) is retried once with a 60 s timeout; if there is still none but
   the slot's `.claude.json` holds an `oauthAccount`, the login is accepted — a just-completed login is never
   thrown away over a timeout — and the new child's first poll flags NeedsLogin if it was not valid after all.
4. The identity used for every decision comes **only** from `AccountIdentity.ReadFrom(<slot>\config\.claude.json)`,
   i.e. `accountUuid` + `organizationUuid`.

Outcomes (`Model/LoginFlowDecision.cs`, pure and tested):

| flow | result |
|---|---|
| add, new identity | slot → `active`, entry appended, set rebuilt, toast "Inloggad: <label>" (label from the ladder below — never the raw "<email>'s Organization") |
| add, identity already tracked | a message naming which account it already is and to choose another account or organisation in the browser; the new slot is retired and deleted |
| re-login of X, same identity | swap: the entry points at the new slot, the old child is stopped, the old slot is retired and deleted, set rebuilt |
| re-login of X, a different tracked or an untracked identity | nothing changes for X; message says who was logged in and that nothing changed; the new slot is retired and deleted |
| re-login of X, but X's own identity cannot be read | nothing changes (it cannot be confirmed that this is the same account); the message says so and suggests removing and re-adding; the new slot is retired and deleted |
| not logged in / another auth method | short message; the new slot is retired and deleted |

**Never lose a good login — the order of the steps** (`Flow/AccountFlowController.cs`; tested with the order
and the crash windows injected):

- *Swap*: the new slot is written `active` and the entry is saved pointing at it FIRST. Only then is the old
  child stopped, the old slot retired and deleted. Nothing that fails after that point can discard the new slot;
  if the entry cannot be saved, the old login is untouched and only the new slot is dropped. Panel focus is
  moved to the new slot before anything is awaited.
- *Remove*: a confirmation naming the label (and the plan when known); the busy flag is taken BEFORE the dialog
  (nothing can start under it, and it is released on No), and afterwards the entry is looked up again and the
  removal abandoned if it changed. Then the `retired` marker is written FIRST (if that fails nothing is saved,
  stopped or deleted), the entry removal is saved, the child is stopped, the folder is deleted (with retries; the
  next start finishes a locked one).
- *Discard* (a rejected new slot): marked `retired` first; if the marker cannot be written the folder is NOT
  deleted.
- *Stopping*: a stop that does not finish within the deadline is followed by killing what is left (only
  processes this app started, matched by process id AND start time) and waiting for it, BEFORE any slot is deleted
  or the new set starts.
- Deleting a login folder (thousands of small files) runs on the thread pool, first attempt included, never on the
  UI thread. Dialogs get a topmost, activated owner so they cannot open behind the browser.
- While the set changes, the UI does not tick: no focus is resolved against a half-changed list and the
  zero-accounts icon cannot flash.

**One instance.** A second live instance exits quietly at start (a session-local named mutex): it would
reconcile the slots under the first one's login and run a second set of children.

**Why no `auth logout`, anywhere.** A logout revokes the grant on the server. It is unverified whether
revoking one slot's grant also invalidates another slot's grant for the same account (two slots for
the same person each hold their own). Removing an account therefore only deletes its folder; the
grant is left to expire on the server. Swapping and discarding a duplicate work the same way.

**Why no follower.** The old `configDir: null` entry meant "whatever Claude Code is logged into", so the
tray silently changed account whenever the user ran `/login` in a terminal or VS Code, and two entries
could end up on the same login. Every account is now an explicit login the app owns; nothing creates a
follower and old ones are dropped by the migration.

**Windows Terminal as the default terminal** (measured on a stock Windows 11 with Windows Terminal 1.24
installed and no default-terminal override, i.e. "let Windows decide"): a GUI-subsystem process
starting a console child with `CreateNoWindow = false` gets the child's *real* process handle, not a
stub — `WaitForExit(1.5 s)` returned `false` while the child slept for 4 s, `WaitForExit()` returned at
4.4–4.5 s, and the child's exit code (7) came back. Waiting works, so no polling workaround is needed.

## NeedsLogin

A child whose login expired or is missing answers `get_usage` with the null shape (above), which is
not a parse error. `UsageParser.Parse` returns it as a structured result (`NotLoggedIn`), not the
generic error. For an account:

- the **first** such answer restarts that account's child once (a stale process is the usual cause);
- if the **fresh child's first answer** is also the null shape, the account is **NeedsLogin**;
- any successful reading clears it. `auth status` is never used for this decision.

*Evidence that the null shape is what a dead login looks like.* On an installation where one pinned
account's credentials had died, that account's child answered `get_usage` about 70 times over roughly 11 hours
(one session of the app, long-lived child process) -- and every one of those responses (checked in the app's own
per-account raw logs, read-only) was exactly the null shape this classifier uses: well-formed, `subscription_type`
null, `rate_limits_available` false, `rate_limits` null, no error object. Not a single authentication-error
(401 / `authentication_error`) response appeared anywhere in the logs of any account, so no such shape is
classified; if one ever shows up it should be added to `UsageParser.IsNotLoggedInShape` the same way. The toast
for a spell is shown once and remembered by slot id across rebuilds; only a successful reading ends the spell.

NeedsLogin is distinct from Unknown in the view (`QuotaView.NeedsLogin`): the icon stays the grey ring with
"!", the tooltip reads "Inte inloggad – högerklicka och välj Logga in igen", the panel's status box says
"Inloggningen har gått ut eller saknas" with a **"Logga in igen"** button that starts that account's
re-login flow, and there is one toast per transition into NeedsLogin (so it is seen in binding mode and
beyond `maxIcons`). Polling continues on the ordinary failure backoff; after a successful re-login the
new account's child polls immediately.

"Senast avläst" is the time of the last **successful** read. A failed poll no longer moves it.

## Loading

From the moment an account's runtime starts (app start, after a rebuild, a newly added account) until its
first result — a successful read or a failure — or 30 s, whichever comes first, the account is **loading**
(`Model/LoadingTracker`; `QuotaView.Loading`). It must not look like an error: the icon is the grey ring
WITHOUT the "!" with a short arc travelling round it, the tooltip reads "Hämtar kvoten…", the panel's status
box "Hämtar kvoten…" (not "Kan inte läsa kvoten") and its row in the other-accounts list "hämtar…". The
first "not logged in" answer (which only restarts the child) is not yet a result, so loading continues until
the confirming answer. After loading the ordinary Unknown / NeedsLogin rules apply. A rebuild creates new
runtimes, so the account is loading again until the new one has read.

The frames are rendered ONCE per tray size and cached as ready-made in-memory ICO bytes
(`Icons/LoadingFrames`: 8 frames, at most 8 a second, the same instant for every account); animating costs
one `new Icon(stream)` per frame, retired by `IconSlot`'s usual two-generation scheme (`Icon.FromHandle`
stays banned). A timer drives the frames only while some account is loading, and `--selftest` checks the
GDI/USER handle counts stay flat over thousands of frames.

## Panel header

The title is the account's label — the name the user gave it (Byt namn…) or the automatic one (see
"Labels"). The line under it is the plan and organisation from Anthropic's own data
(`PanelText.ComposeAccountSubtitle`): `subscription_type` title-cased and the organisation name, joined
with " · " — "Max · personlig organisation", "Team · Acme AB". Anthropic's auto-generated "<email>'s
Organization" is shown as "personlig organisation", never by name and never with an address. Unknown parts
are left out, and so is any part that just repeats the title (an account whose automatic label is already
"Max" shows no second "Max"); with nothing left there is no subtitle line. The DEMO badge and the freshness
line are unchanged.

## All accounts in one panel

The tray menu toggle **"Visa alla konton i panelen"** (persisted as an optional `"panelMode": "all"` in
accounts.json; absent = `single`, so nothing changes for an existing user until it is used) makes a click on
ANY icon open one panel with a compact card per enabled account — accounts without an icon too (beyond
`maxIcons`, or hidden by "Visa bara den som är närmast taket"). A card has the label, the "plan ·
organisation" line, the short verdict in its colour (the status box's own first line, so the wording is the
same), two thin bars (session, week) with the percent and the day-aware reset time, and a one-line note only
when the account is not Live ("Hämtar…", "Inaktuell — …", "Senast avläst …", "Inte inloggad" with a "Logga in
igen" button). Order (`AccountCards.Order`): the accounts that have an icon, in the order their icons appear
on screen left to right (the icon rectangles `PanelAnchor` reads), then the accounts without an icon in config
order; if any icon's rectangle cannot be read the icon group falls back to config order as a whole. The card of
the clicked icon has an accent edge, the card under the mouse is highlighted, and clicking a card opens that
account's detailed panel, which has a "← Alla konton" link back. The panel stays non-activating and never taller
than the work area (`PanelAnchor` caps it; the cards scroll with the mouse wheel when they do not fit).

## Times always carry their day

A clock time that is not today says which day (`TimeText`): "i morgon" tomorrow, the weekday for 2–6 days
ahead, the date ("14 okt") from 7 days (a weekday alone would be today's own), "igår" yesterday — by calendar
day in local time, so midnight and daylight-saving changes are right (tested across both Stockholm changes). This
covers the status boxes, section headers, tooltips, the footer's last-read time and the other-accounts rows. A row
for a spent quota names the quota and the day: "veckan slut · öppnar tis 15:00", "sessionen slut · öppnar 17:19",
"veckan slut · öppnar i morgon 07:00"; if the weekly quota is spent that is what the row reports.

## Zero accounts

With no enabled account the app shows exactly one grey icon with a padlock in the ring (the same glyph as an account that needs a login; the "!" stays for a real read failure), tooltip "Logga in för att visa kvoten", the
full menu (so "Lägg till konto…" and Exit are always reachable), and a left click that starts the add
flow. This is also what a fresh install looks like.

## Child lifecycle

**Graceful stop.** To end an account's child: stop polling, mark the supervisor disposed (so the child's
clean exit is not mistaken for a crash and relaunched), close stdin **under the stdin lock** (never while a
request is writing; if a write is stuck the lock is not obtained and the child is killed instead), wait for
the child to exit by itself (bounded), then `Kill(entireProcessTree)` and wait for it to be gone. All
accounts stop in parallel within the shutdown deadline.

*Measured* (claude.exe 2.1.292, `-p --input-format stream-json --output-format stream-json --verbose`, fresh
empty config dir, Windows 11): **0.39–0.54 s** from closing stdin to exit (544 ms with no request served,
412 and 389 ms after a `get_usage` was answered), exit code 0; the first `get_usage` answer arrived about
0.55 s after launch. The wait is therefore 2 s — about four times the worst observation.

**Supervisor leak fix.** `ClaudeCliChannelSupervisor` used to publish a channel launched while it was being
disposed, leaving a `claude.exe` that nothing would ever stop. It now re-checks the disposed flag after
`Start`, and a launch that is in flight at stop time is disposed instead of published.

**The account set is rebuilt, not patched.** The list used to be built once and addressed by index (icons,
panel focus, right-click target). On any add / remove / re-login every runtime is stopped gracefully (in
parallel), the config is reconciled again, and a new set is built with new icons. Panel focus is kept by
slot id (`Model/AccountFocus.cs`); per-account actions (re-login, remove, the panel's button) are addressed
by slot id, never by position.

**`scripts\install.ps1`** signals the running app through the named event `Local\ClaudeStatusBar-Exit`
(the app listens and runs its normal graceful shutdown), waits up to 15 s, and only then kills it.
`-Uninstall -RemoveLogins` deletes `%LOCALAPPDATA%\ClaudeStatusBar\accounts` after the app has exited (no
logout, and without following reparse points); without `-RemoveLogins` an uninstall prints where the logins
are.

## Labels (never hardcoded)

1. The user's own override from `accounts.json`, if set.
2. `oauthAccount.organizationName` — **unless** it matches Anthropic's own auto-generated name for
   a personal organisation, `<emailAddress>'s Organization` (observed on this machine's real
   personal Max login: `alex@example.com's Organization`). That name is a poor tray label, so
   it is skipped and falls through to step 3 instead.
3. The plan from `subscription_type`, title-cased ("Max", "Team", "Enterprise").
4. The local part of `emailAddress`.
5. "Konto N".

If two accounts resolve to the same label (the whole-set pass, `AccountLabel.Disambiguate`), per group of
colliding labels:

1. **the email domain**, when every member of the group has one and they are all different — "Max (example.com)"
   / "Max (example.org)";
2. otherwise **the plan** — "Acme (Max)" / "Acme (Team)";
3. if still equal (same organisation, plan and domain), **the plan and the email local part** —
   "Acme (Team, alice)" / "Acme (Team, bob)".

A group the domain does not separate (a shared or missing domain) skips step 1 and behaves exactly as before; a
label that is not colliding is never touched; an account flagged as a duplicate does not count as a collision.
Every part comes from the user's own account data. The statistics window shows the same label the tray does
(`StatisticsDataLoader.ResolveAccountLabel` takes the live, already-disambiguated label and only re-resolves the
plan for a capture that ran before the first poll, where the override still wins).

**Renaming.** "Konton ▸ <label> ▸ Byt namn…" opens a small topmost dialog with the current label prefilled and the
hint that an empty name means the automatic name. The name is trimmed, control characters are dropped, and it is
capped at 40 characters; it is stored in that entry's `label` **by slot id** (the entry is looked up again after
the dialog, like Remove), accounts.json is saved, and the icons, panel and menu are redrawn. Nothing is stopped,
rebuilt or restarted, and it respects the one-operation-at-a-time flag. Confirming the prefilled automatic name
unchanged does not freeze it into an override.

Labels are display-only; **the account+organisation pair is the identity** everywhere else
(see "Identity guard" below) — never `accountUuid` alone.

## Identity guard

State is keyed by slot, not by `accountUuid` alone — **`accountUuid` identifies the PERSON, not the
plan.** One person can hold two separate plans (e.g. a Team seat and a personal Max seat) in two
different config directories, and Anthropic hands both logins the *same* `accountUuid`; only
`organizationUuid` differs between them. Evidence from a real, already-logged-in installation on
this machine:

```
%USERPROFILE%\.claude.json (terminal login, Team plan)
   organizationName "Acme AB"                               organizationType claude_team
   accountUuid 11111111-1111-4111-8111-111111111111        organizationUuid 22222222-2222-4222-8222-222222222222
%LOCALAPPDATA%\ClaudeStatusBar\accounts\1\config\.claude.json (personal Max plan)
   organizationName "alex@example.com's Organization" organizationType claude_max
   accountUuid 11111111-1111-4111-8111-111111111111        organizationUuid 33333333-3333-4333-8333-333333333333
```

Keying state on `accountUuid` alone collapsed both logins onto one key: one log directory, one
`window-shape-YYYY-MM.csv`, one `QuotaModel` fed two plans' contradictory percentages — the visible
symptom was one tray icon instead of two, and a panel unable to render either verdict ("Kan inte
läsa kvoten" / "Data saknas").

The identity key is `AccountIdentity.StateKey`: `accountUuid` and `organizationUuid` joined with an
underscore (`<accountUuid>_<organizationUuid>`), or `accountUuid` alone when a login has no
organisation (`organizationUuid` null/empty — a bare personal login with no team at all). This key
is what the per-account CSV (`logs\<stateKey>\window-shape-YYYY-MM.csv`), the model, warm start and
freshness all live under. When a slot's `StateKey` changes — the user ran `/login` and switched
accounts *or organisations* in the same config dir, including switching between two organisations
for the same person — that slot's in-memory state is dropped and rebuilt under the new key; the old
key's history stays on disk for when that identity comes back. Without this, a login/organisation
switch silently mixes two plans' percentages into one forecast.

**Migration note:** the pre-fix build kept everything for this machine's two real accounts under one
shared directory, `logs\11111111-1111-4111-8111-111111111111\` (mixed Team and Max history — not
safely separable after the fact). That directory is left in place as read-only historical data; the
app does not touch or delete it. Each identity starts a fresh `logs\<stateKey>\` directory of its
own the next time it is seen, with no warm-start history until it accumulates its own.

The privacy truncation rule (at most 8 characters of any identifier in a log line) applies to each
half of a composite `StateKey` independently — `AccountIdentity.KeyPrefixOf` truncates the
`accountUuid` half and the `organizationUuid` half to 8 characters each and rejoins them (e.g.
`11111111_22222222`), so two organisations for the same person still produce two distinct log-safe
prefixes instead of collapsing back onto one.

## Duplicate accounts

Two slots can still end up on the SAME identity — a login that landed in the organisation another slot
already tracks (the login flow refuses this, but a slot recovered from disk after `accounts.json` was lost
can still hold one), or a pending slot adopted by reconciliation. Before this was handled, that produced
two identical tray icons and one redundant `claude.exe` polling the exact same account for no reason.

**The rule:** two enabled accounts are duplicates when their `AccountIdentity.StateKey` — the
same accountUuid+organizationUuid pair "Identity guard" above defines, never accountUuid alone —
is equal. `Model/AccountDuplicates.Detect` is the pure grouping function (unit-tested on its own,
the same way `Model/AccountDisplayPlan.cs` is): within a group of equal keys, only the FIRST
account in list order is kept; every later one is a duplicate of it.
`StatusBarApplicationContext.RefreshDuplicates` re-runs this every eval tick (1Hz) and applies the
verdict via `AccountRuntime.SetDuplicate`. (Duplicate positions are indices into the current set, which is
rebuilt wholesale on every change, so they never outlive the set they describe.)

**What changes for a duplicate account:**

- No tray icon (`AccountDisplayPlan.Candidate.Enabled` is fed `!IsDuplicate`, so it can also never
  win binding-mode selection).
- No poll child: `SetDuplicate(true)` stops the poll timer and tears down the
  `ClaudeCliChannelSupervisor` — there is no reason to keep a second `claude.exe` running against
  an identity another child already polls. `AccountRuntime.PollAsync` also refuses outright while
  `IsDuplicate` is true, so even a stray direct call (a reload-button race, for instance) can never
  restart it.
- Its place in the panel's "other accounts" list is replaced by an explanatory row
  (`Ui/PanelText.ComposeDuplicateAccountRow`): "Konto 2 är samma inloggning som konto 1 (Max).
  Högerklicka på ikonen: Konton › Ta bort… — eller Logga in igen med ett annat konto." Clicking that
  row focuses the KEPT account instead of a stopped, unpolled one with nothing to show.
- The account it duplicates gets a short suffix on its TRAY TOOLTIP only ("· dublett: konto 2") —
  never on the panel header label, which stays exactly what it would show for a single account.
- `AccountLabel.Disambiguate` is told which accounts are duplicates and excludes them from its
  collision counting in both directions: this pass exists for DIFFERENT accounts that happen to
  share a label, not for two rows tracking the SAME login.

**Never merged or deleted:** a duplicate's on-disk state (`logs\<stateKey>\...`) is left exactly
as it is. Once its identity matches the kept account's, both runtimes naturally point at the same
`logs\<sharedStateKey>\` directory — only the kept account keeps writing to it.

**Resolves on its own:** a duplicate's own polling is stopped, so `AccountRuntime.RefreshIdentityOnly` (a
plain file read, no channel involved) keeps ITS identity current every eval tick. The moment the two keys
diverge again, `RefreshDuplicates` stops marking either one a duplicate and the paused account resumes
polling exactly like a freshly started one, immediately.

## Display

**perAccount** (default): one tray icon per enabled account, in list order, capped at `maxIcons`
(3 by default). Each icon renders exactly as the single-account icon does today, and its tooltip
starts with the account label. Accounts past the cap are panel-only.

**binding**: one tray icon showing the account closest to being blocked — the highest severity, ties
broken by the shorter time to depletion, then by list order. Its tooltip names that account.

**Panel**: clicking an icon opens the panel for that account (the v2 layout unchanged: status box,
session and weekly sections). With more than one account it also lists the other accounts as compact
rows — label, verdict, time to reset — and clicking a row switches the panel to that account. In
binding mode the panel opens on the binding account.

Both display settings are also togglable from the tray context menu.

## Each account needs its own login (not a copy)

An account is a slot, and that slot's config directory must have been logged into directly. **Copying an
existing login into a new directory does not work**: Claude Code rotates its OAuth refresh token, which
invalidates the copy within minutes — observed live as `401` responses and `rate_limits: null`, i.e. a
permanent "Kan inte läsa kvoten". The login flow therefore only ever creates a fresh slot and runs an
interactive login in it.

## macOS — verified 2026-09-19

Everything above was written from the Windows implementation. It has now been run on macOS with
two real accounts (a Team seat and a personal Max seat), and holds unchanged, with two additions:

- **The config directory's PATH is part of the account's identity on macOS.** Claude Code derives
  the login's Keychain service name from `sha256(NFC(configDir))`, so `/a/b` and `/a/b/` are two
  different accounts as far as it is concerned, and a login performed under one spelling reads as
  logged out under the other. `ChildProcessSpec.canonicalConfigDirectory` is the single place that
  decides the spelling, and `scripts/add-account.sh` writes the same canonical string into
  `accounts.json`. See `docs/mac-port.md` §2 for the decompiled selector.
- **`CLAUDE_SECURESTORAGE_CONFIG_DIR` outranks `CLAUDE_CONFIG_DIR`** in that selector. A stray one
  inherited from the user's environment would collapse every account onto a single keychain
  namespace while each still looked isolated, so a pinned account now pins both variables.
- **`add-account.sh` reports the organization and refuses a duplicate login.** It says which
  organization the login landed in — "your personal organization" for Anthropic's auto-generated
  `<email>'s Organization`, otherwise the name — before the login (choose the one you want), after
  it, and in the duplicate messages. The duplicate check compares `accountUuid_organizationUuid`,
  the same identity key the app uses, so logging into the same organization twice is refused
  instead of silently producing two icons for one quota. It runs on `--register` too.

The Mac app still uses the earlier model (`accounts.json` with `configDir` paths, `add-account.sh`).
Bringing it to the slot model is in `docs/backlog.md`; on the Mac a deleted slot must also delete its
keychain item, because there is no logout.

The label ladder was exercised against real data for the first time here: the Team account
resolves at step 2 (its `organizationName`), and the personal Max account's `organizationName` is
Anthropic's auto-generated `<emailAddress>'s Organization`, which step 2 correctly skips, so it
resolves at step 3 to its plan — "Max".

## Cost and limits

Each account costs one resident `claude.exe` child (~230 MB) and one poll cycle; control requests are
unmetered, so accounts do not consume quota. The per-account poll cadence is unchanged. Accounts are
polled independently: one failing or logged-out account degrades only its own icon and panel row,
never the others.

## Out of scope for this round

Per-account alerts and notifications, and any aggregate "total across accounts" view — the plans have
separate limits and adding them up would be meaningless.
