# Multiple accounts

One person can hold several Claude plans at once — a personal Pro/Max, a Team seat, an Enterprise
seat. This is how the app tracks more than one, without hardcoding anything about any particular
organisation: **every label comes from the user's own Anthropic data.**

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
  `%USERPROFILE%\.claude`, not inside it; `%USERPROFILE%\.claude` itself only holds
  credentials/plugins/CLAUDE.md/etc. A literal `<configDir>\.claude.json` composition
  (`%USERPROFILE%\.claude\.claude.json`) does not exist on disk. When `CLAUDE_CONFIG_DIR` is
  set, Claude Code relocates the *whole* state directory there, `.claude.json` included, so the
  literal `<configDir>\.claude.json` composition is correct for every non-default account (each
  one gets its own `CLAUDE_CONFIG_DIR`, per `scripts\add-account.ps1`).
- **`get_usage` → `subscription_type`** (`max`, `team`, …) for the account that child is logged into.

## Labels (never hardcoded)

1. The user's own override from `accounts.json`, if set.
2. `oauthAccount.organizationName` — **unless** it matches Anthropic's own auto-generated name for
   a personal organisation, `<emailAddress>'s Organization` (observed on this machine's real
   personal Max login: `alex@example.com's Organization`). That name is a poor tray label, so
   it is skipped and falls through to step 3 instead.
3. The plan from `subscription_type`, title-cased ("Max", "Team", "Enterprise").
4. The local part of `emailAddress`.
5. "Konto N".

If two accounts resolve to the same label, append the plan; if still equal, append the email local
part. Labels are display-only; **the account+organisation pair is the identity** everywhere else
(see "Identity guard" below) — never `accountUuid` alone.

## Configuration

`%LOCALAPPDATA%\ClaudeStatusBar\accounts.json`, created on first run:

```jsonc
{
  "displayMode": "perAccount",   // or "binding": one icon for the account closest to its limit
  "maxIcons": 3,                 // perAccount only; accounts beyond this appear in the panel only
  "accounts": [
    { "configDir": null, "label": null, "enabled": true }   // null = Claude Code's own default login
  ]
}
```

- **Default install = exactly one account, `configDir: null`**, i.e. whatever the user is logged into.
  Zero setup, which is what a first-time user of a public tool should get.
- **Adding an account** (`scripts\add-account.ps1`) creates
  `%LOCALAPPDATA%\ClaudeStatusBar\accounts\<n>\config`, runs an interactive `claude` login with
  `CLAUDE_CONFIG_DIR` pointed at it, and appends the entry. It also offers to pin the *existing*
  account to its own directory, because a follower entry plus a pinned one can show the same account
  twice and leave another invisible.
- Both settings are also togglable from the tray context menu.

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

## Display

**perAccount** (default): one tray icon per enabled account, in config order, capped at `maxIcons`
(3 by default). Each icon renders exactly as the single-account icon does today, and its tooltip
starts with the account label. Accounts past the cap are panel-only.

**binding**: one tray icon showing the account closest to being blocked — the highest severity, ties
broken by the shorter time to depletion, then by config order. Its tooltip names that account.

**Panel**: clicking an icon opens the panel for that account (the v2 layout unchanged: status box,
session and weekly sections). With more than one account it also lists the other accounts as compact
rows — label, verdict, time to reset — and clicking a row switches the panel to that account. In
binding mode the panel opens on the binding account.

## Each account needs its own login (not a copy)

An account entry points at a config directory, and that directory must have been logged into
directly. **Copying an existing login into a new directory does not work**: Claude Code rotates its
OAuth refresh token, which invalidates the copy within minutes — observed live as `401` responses and
`rate_limits: null`, i.e. a permanent "Kan inte läsa kvoten". `scriptsdd-account.ps1` therefore only
ever creates a fresh directory and runs an interactive login in it. If a run is interrupted before the
account is saved, re-run it with `-Register <slot>` to register the directory that was already logged in.

## Cost and limits

Each account costs one resident `claude.exe` child (~230 MB) and one poll cycle; control requests are
unmetered, so accounts do not consume quota. The per-account poll cadence is unchanged. Accounts are
polled independently: one failing or logged-out account degrades only its own icon and panel row,
never the others.

## Out of scope for this round

Per-account alerts and notifications, and any aggregate "total across accounts" view — the plans have
separate limits and adding them up would be meaningless.
