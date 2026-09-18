# Claude Status Bar

A Windows 11 tray app that shows how much of your Claude subscription quota is left — the current
5-hour session and the weekly limit — and whether you will run out before the quota resets.

It needs no API key and costs no quota: it reads the same numbers Claude Code's `/usage` shows,
through your existing Claude Code login.

> **Unofficial.** Not affiliated with or endorsed by Anthropic. It relies on undocumented Claude Code
> behaviour (the `get_usage` control request), which can change with any Claude Code update. It never
> reads your credentials. Provided as-is.

## What you see

**Tray icon** (next to the clock)
- Outer ring: weekly usage ("All models"). Inner pie: current 5-hour session.
- Colour is the verdict, driven by the forecast rather than the raw percentage:
  green = lasts until reset, amber = tight, red = runs out before reset, dashed grey = spent
  (the pie then counts down to the reset).
- A lighter slice continues past the used part: where you land at reset at the current pace.
- Dimmed with a dot = data may be stale. Grey outline = quota can't be read.

**Panel** (left-click the icon; click the icon again, click elsewhere or press Esc to close)
- A status box that always answers two things: will the budget last, and when does it reset or
  run out. For example: "⚠ Kvoten tar slut kl 15:01 (om 1 h 43 min) — 1 h 4 min före reset kl 16:05".
- Per window, two bars on the same scale: **Tid** (how much of the window has passed) and
  **Kvot** (how much is used, plus the forecast). A quota bar shorter than the time bar means
  you're under budget.
- The forecast stays in "Mäter takt…" until there is enough data: the first 10 minutes of a session,
  and the first 24 hours of a week (so a morning's work pace isn't extrapolated through nights).

## Requirements

- Windows 11.
- **Claude Code**, installed with the official installer (it puts `claude.exe` in
  `%USERPROFILE%\.local\bin`) or with any other `claude.exe` on `PATH`.
- **Logged in to Claude Code with a Pro or Max subscription.** Quota windows only exist for
  subscriptions; an API-key login has no plan limits to show.
- To build: .NET SDK 10.0 (`global.json` pins 10.0.204 and rolls forward to newer patches).

## Install on a computer

```powershell
gh repo clone zenta-ab/claude-status-bar   # or: git clone https://github.com/zenta-ab/claude-status-bar.git
cd claude-status-bar
claude            # once, if Claude Code isn't logged in yet: log in, then exit
.\scripts\install.ps1
```

`install.ps1` publishes a Release build to `%LOCALAPPDATA%\Programs\ClaudeStatusBar`, starts it at
Windows sign-in (`HKCU\...\Run`, no admin rights), launches it, and pins the icon next to the clock.

- **Update** after pulling or changing code: run `.\scripts\install.ps1` again.
- **Uninstall**: `.\scripts\install.ps1 -Uninstall` (logs are kept).
- Autostart can also be switched off in Task Manager → Startup apps.

If PowerShell blocks the script: `powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1`.

## How it gets the data

The app runs one long-lived Claude Code child process
(`claude -p --input-format stream-json --output-format stream-json --verbose`) and sends it the
`get_usage` control request — the same path Anthropic's VS Code extension uses. Control requests are
not metered. Nothing about your account is stored by the app; the login stays with Claude Code.

- Polls every 30 s while you're using quota, every 2.5 min when idle, 5 min when a quota is spent,
  plus one poll right after each reset. Countdowns tick locally every second.
- Claude Code refreshes its quota data about every 5 minutes; the panel header shows how old the
  numbers really are.
- The child runs in `%LOCALAPPDATA%\ClaudeStatusBar\agent`, so its session (and any `SessionStart`
  hooks) never touches your repositories. If it crashes or Claude Code auto-updates, the app restarts
  it with backoff.

## Several accounts

If you hold more than one Claude plan (a personal Pro/Max, a Team seat, ...), Status Bar can
track all of them at once — each with its own tray icon and its own claude.exe child, so one
account's quota, forecast and history never mixes with another's.

- **Add an account**: `.\scripts\add-account.ps1`. It creates a fresh login directory, runs an
  interactive `claude` login pointed at it, adds it to `accounts.json`, and restarts the app.
  `-List` shows configured accounts and their slot numbers; `-Remove <n>` drops one (its login
  stays on disk, only the app forgets about it). The first time you add a second account, also
  run `-PinDefault` — it explains why and copies your current default login into its own fixed
  directory, so it can't later start following a different account you log into.
- **Two display modes**, toggled from the tray icon's right-click menu or in `accounts.json`:
  `perAccount` (default) shows one icon per account, up to `maxIcons` (3); `binding` shows a
  single icon for whichever account is closest to being blocked. Either way, left-clicking an
  icon opens the panel for that account, and the panel's "other accounts" list lets you switch to
  any account that isn't showing an icon right now.
- **Cost**: one resident `claude.exe` child (~230 MB) and one poll cycle per account. Control
  requests are unmetered, so accounts never consume each other's quota, and one account failing
  or logged out only degrades its own icon and panel row.

See `docs/multi-account.md` for the full design (labels, identity handling, configuration shape).

## Troubleshooting

| Symptom | Check |
|---|---|
| Icon not visible | Click `^` next to the clock and drag it out, or re-run `install.ps1` |
| "Kan inte läsa kvoten" | Is Claude Code installed (`claude --version`) and logged in with a Pro/Max account? |
| Numbers differ from claude.ai | Reload the claude.ai page — it only refreshes on load |
| Anything else | Logs in `%LOCALAPPDATA%\ClaudeStatusBar\logs` (daily raw log, monthly `window-shape-YYYY-MM.csv`) |

## Development

```powershell
dotnet build ClaudeStatusBar.slnx
dotnet test ClaudeStatusBar.slnx
dotnet run --project src\Windows -- --demo       # cycles through every state, no Claude child
dotnet run --project src\Windows -- --selftest   # icon pipeline GDI/USER handle leak check
```

A running installed copy locks nothing in the repo; a running `dotnet run` copy locks `bin\Debug`.

## Privacy guard

This repo is public and the app reads account data, so nothing personal may land in it.
Before committing, run:

```powershell
.\scripts\check-privacy.ps1 -All        # scan every tracked file
.\scripts\check-privacy.ps1 -Install    # enable the pre-commit hook for this clone
```

It blocks email addresses, UUIDs, user paths and API tokens, allowing only obvious
placeholders (reserved example domains, repeated-character UUIDs). Git hooks are per clone,
so each contributor runs `-Install` once.

## Docs

- `docs/forecast-and-states.md` — forecast model, states, freshness, decision record
- `docs/panel-v2.md` — panel layout and copy rules
- `docs/multi-account.md` — multiple accounts: labels, identity handling, configuration shape
- `docs/reviews/` — independent Codex reviews and the decisions taken on each finding
- `docs/backlog.md` — open ideas, e.g. learning your working-hours pattern for the forecast

## License

MIT, see [`LICENSE`](LICENSE).
